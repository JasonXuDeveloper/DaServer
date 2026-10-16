namespace DaServer.Runtime;

/// <summary>
/// A single-threaded shard: one dedicated thread owning an EasyEcs <see cref="EasyEcs.World"/>, plus the lock-free
/// dispatch queue other threads post to. Everything else is ECS: actors are entities (mailbox, turn and state components),
/// per-shard state (actor directory, activity) is singletons, and runtime systems in <see cref="ShardMessages"/> route and run messages.
/// <para>Loop: on every wake the <see cref="ShardMessages"/> group runs (so messages never wait for a tick); at the
/// tick rate the default group runs (simulation systems). User code never needs locks.</para>
/// </summary>
internal sealed class Shard : CompletionThread
{
    /// <summary>
    /// Completions, callbacks and turn ends that may wait at once before <see cref="_overflow"/> takes the rest. They
    /// finish work already admitted (at most one per call or turn in flight), so they never compete with new work for
    /// space; 16k slots (512 KB) cover the widest bursts measured, beyond them a burst allocates queue segments once.
    /// </summary>
    private const int CompletionQueueCapacity = 16 * 1024;

    private readonly MpmcQueue<WorkItem> _dispatchQueue; // new calls and messages: full = refused (Overloaded)
    private readonly MpmcQueue<WorkItem> _completionQueue; // work that finishes admitted work: never refused
    private readonly ConcurrentQueue<WorkItem> _overflow = new(); // only when the completion ring is full
    private int _overflowCount;
    private long _rejected;
    private long _expired;
    private long _failed;
    private readonly ManualResetEventSlim _wake = new(false, spinCount: 0);
    private int _sleeping;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopRequested, _stopFenced;
    private readonly long _tickInterval; // Stopwatch ticks
    private const long IdleScanMs = 100;
    private int _actorCount;

    internal Shard(Node node, int index, int dispatchQueueCapacity, int tickRate)
    {
        Node = node;
        Index = index;
        _dispatchQueue = new MpmcQueue<WorkItem>(dispatchQueueCapacity);
        _completionQueue = new MpmcQueue<WorkItem>(CompletionQueueCapacity);
        _tickInterval = Stopwatch.Frequency / Math.Max(1, tickRate);
        World = new World();
        _thread = new Thread(Run) { IsBackground = true, Name = $"{node.Options.Name}-shard-{index}" };
    }

    /// <summary>The shard whose thread is executing the current code, or null outside shard threads.</summary>
    // Invariant: shards are the only caller threads on servers.
    public static new Shard? Current => Unsafe.As<Shard?>(CompletionThread.Current);

    public Node Node { get; }

    /// <summary>Index of the shard inside its node.</summary>
    public int Index { get; }

    /// <summary>The shard's ECS world: actors, runtime systems and the node's user systems.</summary>
    public World World { get; }

    /// <summary>Number of completed ticks.</summary>
    public long TickCount { get; private set; }

    /// <summary>Work items waiting in the dispatch queue (approximate, any thread).</summary>
    public int QueueLength => _dispatchQueue.Count + _completionQueue.Count + Volatile.Read(ref _overflowCount);

    /// <summary>Calls and events refused because the shard or an actor mailbox was full (any thread).</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>Requests dropped unrun because their caller had already timed out (the call carried a deadline).</summary>
    public long Expired => Interlocked.Read(ref _expired);

    /// <summary>Messages whose handler failed (threw) on this shard since start: poison messages show up here.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Active loaded actors (approximate, any thread).</summary>
    public int ActorCount => Volatile.Read(ref _actorCount);

    private long _passStartedMs; // Environment.TickCount64 when the running pass started, 0 while asleep

    /// <summary>How long the shard thread has been in its current pass (0 while it sleeps): the stall watchdog's input.</summary>
    internal long PassMs(long now)
    {
        long started = Volatile.Read(ref _passStartedMs);
        return started == 0 ? 0 : now - started;
    }

    /// <summary>Completes when the shard thread has exited.</summary>
    public Task Stopped => _stopped.Task;

    internal void Start() => _thread.Start();

    /// <summary>
    /// Thread-safe: admit a new call or event unless the dispatch queue is full (<see cref="NodeOptions.DispatchQueueCapacity"/>).
    /// New work is bounded so a flood cannot grow memory without limit; completions and callbacks (<see cref="Post"/>)
    /// have their own queue and always get in, because they finish work already admitted.
    /// </summary>
    internal bool TryAdmit(FrameWriter frame, IReplySink? sink)
    {
        if (!_dispatchQueue.TryEnqueue(new WorkItem(WorkKind.Dispatch, frame, sink)))
        {
            CountRejected();
            return false;
        }

        Wake();
        return true;
    }

    internal void CountRejected() => Interlocked.Increment(ref _rejected);

    internal void CountExpired() => Interlocked.Increment(ref _expired);

    internal void CountFailed() => Interlocked.Increment(ref _failed);

    /// <summary>Thread-safe: enqueue a completion, callback or turn end and wake the shard if it is sleeping.</summary>
    internal void Post(in WorkItem item)
    {
        if (!_completionQueue.TryEnqueue(item)) PostOverflow(item);
        Wake();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PostOverflow(in WorkItem item)
    {
        _overflow.Enqueue(item);
        Interlocked.Increment(ref _overflowCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Wake()
    {
        if (Volatile.Read(ref _sleeping) != 0)
            _wake.Set();
    }

    /// <summary>Run <paramref name="callback"/> on this shard's thread (thread-safe, allocation-free).</summary>
    public override void Post(Action<object?> callback, object? state) => Post(new WorkItem(WorkKind.Callback, callback, state));

    /// <summary>Thread-safe: ownership may have changed; actors this node no longer owns are unloaded.</summary>
    internal void RequestRebalance() =>
        Post(static s => ((Shard)s!).World.GetSingleton<ShardActivity>().Rebalance = true, this);

    /// <summary>Thread-safe: unload every current loaded actor (this node was declared Dead; its keys moved).</summary>
    internal void RequestUnloadAll() =>
        Post(static s =>
        {
            ref var activity = ref ((Shard)s!).World.GetSingleton<ShardActivity>();
            activity.DeclaredDead = activity.Rebalance = true;
        }, this);

    /// <summary>Stop the shard: unload every actor (<paramref name="fenced"/>: remove them without <see cref="Actor.OnUnload"/>).</summary>
    internal void RequestStop(bool fenced = false)
    {
        _stopFenced = fenced;
        _stopRequested = true;
        _wake.Set();
    }

    /// <summary>
    /// Queue consumer (shard thread only, used by <see cref="DispatchSystem"/>): work that finishes admitted work first
    /// (it frees calls, turns and their buffers), then new work.
    /// </summary>
    internal bool TryTake(out WorkItem item)
    {
        if (_completionQueue.TryDequeue(out item)) return true;
        if (Volatile.Read(ref _overflowCount) != 0 && _overflow.TryDequeue(out item))
        {
            Interlocked.Decrement(ref _overflowCount);
            return true;
        }

        return _dispatchQueue.TryDequeue(out item);
    }

    // ------------------------------------------------------------------ loop

    private void Run()
    {
        SetCurrent(this);
        SynchronizationContext.SetSynchronizationContext(new ShardSynchronizationContext(this));
        try
        {
            CreateWorld();
            long nextTick = Stopwatch.GetTimestamp() + _tickInterval;
            long nextIdleScan = 0;
            ref var activity = ref World.GetSingleton<ShardActivity>(); // singletons never move
            while (true)
            {
                Volatile.Write(ref _passStartedMs, Environment.TickCount64);
                activity.Processed = 0;
                if (_stopRequested && !activity.Stopping)
                {
                    activity.Stopping = activity.Rebalance = true;
                    activity.DeclaredDead |= _stopFenced; // a downed node saves nothing: the majority owns the keys now
                }

                using (Connection.BeginSendBatch()) // everything this wake sends leaves in one write per connection
                {
                    // Cadence is the loop's job; each group's systems only do their one thing.
                    if (activity.Rebalance)
                    {
                        activity.Rebalance = false;
                        World.Update<ShardRebalance>();
                        activity.DeclaredDead = false;
                    }

                    if (Environment.TickCount64 >= nextIdleScan)
                    {
                        nextIdleScan = Environment.TickCount64 + IdleScanMs;
                        World.Update<ShardIdleScan>();
                    }

                    World.Update<ShardMessages>();

                    long now = Stopwatch.GetTimestamp();
                    if (now >= nextTick)
                    {
                        TickCount++;
                        World.Update();
                        nextTick += _tickInterval;
                        if (now - nextTick > _tickInterval * 5)
                            nextTick = now + _tickInterval; // fell far behind: skip instead of a catch-up storm
                    }
                }

                Volatile.Write(ref _actorCount, World.GetSingleton<ActorDirectory>().Actors.Count);
                if (activity.Stopping && IsDrained()) break;
                if (activity.Processed == 0 && !World.Any<Mailbox>(MailboxSystem.Runnable))
                    Sleep(World.GetSingleton<ShardTimers>().Wheel.Count == 0 ? nextTick
                        : Math.Min(nextTick, Stopwatch.GetTimestamp() + TimerWake)); // a delayed send waits for no tick
            }
        }
        catch (Exception e)
        {
            Node.Logger.LogCritical(e, "Shard {Shard} crashed", Index);
        }
        finally
        {
            try
            {
                if (World.HasSingleton<ShardTimers>()) World.GetSingleton<ShardTimers>().Drop();
                World.Dispose();
            }
            finally
            {
                SetCurrent(null);
                _stopped.TrySetResult();
            }
        }
    }

    /// <summary>Singletons, runtime systems, the node's user systems and service instances.</summary>
    private void CreateWorld()
    {
        World.AddSingleton<ShardContext>() = new ShardContext(this);
        World.AddSingleton<ShardActivity>();
        World.AddSingleton<CallChains>() = new CallChains { Last = Random.Shared.NextInt64() };
        World.AddSingleton<ShardTimers>() = new ShardTimers(new TimerWheel<DelayedSend>(ShardTimers.Resolution));
        World.AddSingleton<ActorDirectory>() = new ActorDirectory(new Dictionary<ActorAddress, Entity>());
        World.AddSingleton<TurnWatchers>() = new TurnWatchers(new Stack<TurnWatcher>());
        World.AddSingleton<ActorPools>() = new ActorPools(new Dictionary<uint, Stack<Actor>>(), new Stack<Queue<Envelope>>(),
            new Stack<ClientSessions>());

        var services = new Dictionary<uint, object>();
        foreach (var handlerType in Node.HostedHandlerTypes)
        {
            if (handlerType.IsActor) continue;
            var instance = handlerType.Factory();
            if (instance is ShardObject shardObject) shardObject.Shard = this;
            services[handlerType.Id] = instance;
        }

        World.AddSingleton<ServiceDirectory>() = new ServiceDirectory(services);
        var persisted = ServerRegistry.PersistentTypes;
        World.AddSingleton<ShardStore>() = new ShardStore(persisted.Length > 0 ? Node.Options.ActorStore : null, persisted,
            persisted.ToFrozenDictionary(static t => t.TypeId), new LoadBatch(this), new SaveBatch(this));

        World.AddSystem<TimerSystem>();
        World.AddSystem<DispatchSystem>();
        World.AddSystem<TurnSystem>();
        World.AddSystem<MailboxSystem>();
        World.AddSystem<ServiceCallSystem>();
        World.AddSystem<RemovalSystem>();
        World.AddSystem<RebalanceSystem>();
        World.AddSystem<IdleSystem>();
        World.AddSystem<UnloadSystem>();
        World.AddSystem<LoadSystem>();
        World.AddSystem<PersistSystem>();
        World.AddSystem<CheckpointSystem>();
        World.AddSystem<StoreSystem>();
        foreach (var info in Node.HostedSystems) World.AddSystem(info.Factory());
        foreach (var feature in Node.Options.Features) feature.ConfigureShard(World);
    }

    /// <summary>Stopping: every actor removed, no service call running, nothing left in the dispatch queue.</summary>
    private bool IsDrained() =>
        World.GetSingleton<ActorDirectory>().Actors.Count == 0 &&
        !World.Any<ServiceCall>() &&
        QueueLength == 0;

    private static readonly long TimerWake = (long)(ShardTimers.Resolution.TotalSeconds * Stopwatch.Frequency);

    private void Sleep(long nextTick)
    {
        Interlocked.Exchange(ref _sleeping, 1);
        Volatile.Write(ref _passStartedMs, 0);
        if (QueueLength == 0 && !_stopRequested)
        {
            long remaining = nextTick - Stopwatch.GetTimestamp();
            if (remaining > 0)
                _wake.Wait(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
        }

        Volatile.Write(ref _sleeping, 0);
        _wake.Reset();
    }
}
