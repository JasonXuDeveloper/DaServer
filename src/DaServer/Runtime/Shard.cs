namespace DaServer.Runtime;

/// <summary>
/// A single-threaded shard: one dedicated thread owning an EasyEcs <see cref="EasyEcs.World"/>, plus the lock-free
/// inbox other threads post to. Everything else is ECS: actors are entities (mailbox, turn and state components),
/// directories and timers are singletons, and runtime systems in <see cref="ShardMessages"/> route and run messages.
/// <para>Loop: on every wake the <see cref="ShardMessages"/> group runs (so messages never wait for a tick); at the
/// tick rate the default group runs (simulation systems). User code never needs locks.</para>
/// </summary>
internal sealed class Shard
{
    [ThreadStatic] private static Shard? _current;

    private readonly MpmcQueue<WorkItem> _inbox;
    private readonly ConcurrentQueue<WorkItem> _overflow = new(); // completions and callbacks when the ring is full
    private int _overflowCount;
    private readonly int _admitLimit;
    private long _rejected;
    private long _expired;
    private readonly ManualResetEventSlim _wake = new(false, spinCount: 0);
    private int _sleeping;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopRequested;
    private readonly long _tickInterval; // Stopwatch ticks
    private const long IdleScanMs = 100;
    private int _actorCount;

    internal Shard(Node node, int index, int mailboxCapacity, int tickRate)
    {
        Node = node;
        Index = index;
        _inbox = new MpmcQueue<WorkItem>(mailboxCapacity);
        _admitLimit = mailboxCapacity;
        _tickInterval = Stopwatch.Frequency / Math.Max(1, tickRate);
        World = new World();
        _thread = new Thread(Run) { IsBackground = true, Name = $"{node.Options.Name}-shard-{index}" };
    }

    /// <summary>The shard whose thread is executing the current code, or null outside shard threads.</summary>
    public static Shard? Current => _current;

    public Node Node { get; }

    /// <summary>Index of the shard inside its node.</summary>
    public int Index { get; }

    /// <summary>The shard's ECS world: actors, runtime systems and the node's user systems.</summary>
    public World World { get; }

    /// <summary>Number of completed ticks.</summary>
    public long TickCount { get; private set; }

    /// <summary>Work items waiting in the inbox (approximate, any thread).</summary>
    public int QueueLength => _inbox.Count + Volatile.Read(ref _overflowCount);

    /// <summary>Calls and events refused because the shard or an actor mailbox was full (any thread).</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>Requests dropped unrun because their caller had already timed out (the call carried a deadline).</summary>
    public long Expired => Interlocked.Read(ref _expired);

    /// <summary>Active loaded actors (approximate, any thread).</summary>
    public int ActorCount => Volatile.Read(ref _actorCount);

    /// <summary>Completes when the shard thread has exited.</summary>
    public Task Stopped => _stopped.Task;

    internal void Start() => _thread.Start();

    /// <summary>
    /// Thread-safe: admit a new call or event unless the inbox holds <see cref="NodeOptions.ShardInboxCapacity"/> items.
    /// New work is bounded so a flood cannot grow memory without limit; completions and callbacks (<see cref="Post"/>)
    /// always get in, because they finish work already admitted and are bounded by the calls in flight.
    /// </summary>
    internal bool TryAdmit(FrameWriter frame, IReplySink? sink)
    {
        if (QueueLength >= _admitLimit)
        {
            CountRejected();
            return false;
        }

        Post(new WorkItem(WorkKind.Dispatch, frame, sink));
        return true;
    }

    internal void CountRejected() => Interlocked.Increment(ref _rejected);

    internal void CountExpired() => Interlocked.Increment(ref _expired);

    /// <summary>Thread-safe: enqueue work and wake the shard if it is sleeping.</summary>
    internal void Post(in WorkItem item)
    {
        if (!_inbox.TryEnqueue(item))
        {
            _overflow.Enqueue(item);
            Interlocked.Increment(ref _overflowCount);
        }

        if (Volatile.Read(ref _sleeping) != 0)
            _wake.Set();
    }

    /// <summary>Run <paramref name="callback"/> on this shard's thread (thread-safe, allocation-free).</summary>
    public void Post(Action<object?> callback, object? state) => Post(new WorkItem(WorkKind.Callback, callback, state));

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

    internal void RequestStop()
    {
        _stopRequested = true;
        _wake.Set();
    }

    /// <summary>Inbox consumer (shard thread only, used by <see cref="InboxSystem"/>).</summary>
    internal bool TryTake(out WorkItem item)
    {
        if (_inbox.TryDequeue(out item)) return true;
        if (Volatile.Read(ref _overflowCount) == 0 || !_overflow.TryDequeue(out item)) return false;
        Interlocked.Decrement(ref _overflowCount);
        return true;
    }

    // ------------------------------------------------------------------ loop

    private void Run()
    {
        _current = this;
        SynchronizationContext.SetSynchronizationContext(new ShardSynchronizationContext(this));
        try
        {
            CreateWorld();
            long nextTick = Stopwatch.GetTimestamp() + _tickInterval;
            long nextIdleScan = 0;
            ref var activity = ref World.GetSingleton<ShardActivity>(); // singletons never move
            while (true)
            {
                activity.Processed = 0;
                if (_stopRequested && !activity.Stopping) activity.Stopping = activity.Rebalance = true;

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
                    Sleep(nextTick);
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
                World.Dispose();
            }
            finally
            {
                _current = null;
                _stopped.TrySetResult();
            }
        }
    }

    /// <summary>Singletons, runtime systems, the node's user systems and service instances.</summary>
    private void CreateWorld()
    {
        World.AddSingleton<ShardContext>() = new ShardContext(this);
        World.AddSingleton<ShardActivity>();
        World.AddSingleton<ActorDirectory>() = new ActorDirectory(new Dictionary<ActorId, Entity>());
        World.AddSingleton<TurnWatchers>() = new TurnWatchers(new Stack<TurnWatcher>());

        var services = new Dictionary<uint, object>();
        foreach (var handlerType in Node.HostedHandlerTypes)
        {
            if (handlerType.IsActor) continue;
            var instance = handlerType.Factory();
            if (instance is ShardObject shardObject) shardObject.Shard = this;
            services[handlerType.Id] = instance;
        }

        World.AddSingleton<ServiceDirectory>() = new ServiceDirectory(services);

        World.AddSystem<InboxSystem>();
        World.AddSystem<TurnSystem>();
        World.AddSystem<MailboxSystem>();
        World.AddSystem<ServiceCallSystem>();
        World.AddSystem<RemovalSystem>();
        World.AddSystem<RebalanceSystem>();
        World.AddSystem<IdleSystem>();
        World.AddSystem<UnloadSystem>();
        foreach (var info in Node.HostedSystems) World.AddSystem(info.Factory());
    }

    /// <summary>Stopping: every actor removed, no service call running, nothing left in the inbox.</summary>
    private bool IsDrained() =>
        World.GetSingleton<ActorDirectory>().Actors.Count == 0 &&
        !World.Any<ServiceCall>() &&
        QueueLength == 0;

    private void Sleep(long nextTick)
    {
        Interlocked.Exchange(ref _sleeping, 1);
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
