using System.Runtime.InteropServices;
namespace DaServer.Runtime;

/// <summary>
/// A single-threaded shard: one dedicated thread owning an EasyEcs <see cref="EasyEcs.World"/>, plus the lock-free
/// dispatch queue other threads post to. Everything else is ECS: actors are entities (identity, lifecycle tags, user
/// components), per-shard state (activity, asks in flight, the store) is singletons, and runtime systems in
/// <see cref="ShardMessages"/> route and run messages.
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

    /// <summary>The handoff record's sessions section (stateless: one for every shard).</summary>
    private static readonly Persistence.SessionsSection Sessions = new();

    private readonly MpmcQueue<WorkItem> _dispatchQueue; // new calls and messages: full = refused (Overloaded)
    private readonly MpmcQueue<WorkItem> _completionQueue; // work that finishes admitted work: never refused
    private readonly ConcurrentQueue<WorkItem> _overflow = new(); // only when the completion ring is full
    private int _overflowCount;
    private PaddedLong _queueFull; // producers count refusals exactly under overload: a line of its own
    private ShardCounters _counters; // written by the shard thread every pass: away from what producers read
    private readonly ManualResetEventSlim _wake = new(false, spinCount: 0);
    private PaddedLong _sleeping; // read by every producer on every post: never next to what the shard writes
    private readonly Thread _thread;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopRequested, _stopFenced;
    private readonly TimeProvider _time; // the shard clock (NodeOptions.TimeProvider)
    private readonly long _tickInterval, _timerWake; // in the clock's timestamp units
    private const long IdleScanMs = 100;

    internal Shard(Node node, int index, int dispatchQueueCapacity, int tickRate)
    {
        Node = node;
        Index = index;
        _dispatchQueue = new MpmcQueue<WorkItem>(dispatchQueueCapacity);
        _completionQueue = new MpmcQueue<WorkItem>(CompletionQueueCapacity);
        _time = node.Options.TimeProvider;
        _tickInterval = _time.TimestampFrequency / Math.Max(1, tickRate);
        _timerWake = (long)(ShardTimers.Resolution.TotalSeconds * _time.TimestampFrequency);
        World = new World(new WorldOptions { SystemErrors = SystemErrorPolicy.Disable }); // see ReviveFailedSystems
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
    public long TickCount => Volatile.Read(ref _counters.TickCount);

    /// <summary>Work items waiting in the dispatch queue (approximate, any thread).</summary>
    public int QueueLength => _dispatchQueue.Count + _completionQueue.Count + Volatile.Read(ref _overflowCount);

    /// <summary>Messages dropped unrun for <paramref name="reason"/> since start (any thread).</summary>
    public long Dropped(DropReason reason) => reason switch
    {
        DropReason.QueueFull => Interlocked.Read(ref _queueFull.Value),
        DropReason.MailboxFull => Volatile.Read(ref _counters.MailboxFull),
        _ => Volatile.Read(ref _counters.Expired),
    };


    /// <summary>Messages whose handler failed (threw), and system passes that threw, on this shard since start: poison messages and broken systems show up here.</summary>
    public long Failed => Volatile.Read(ref _counters.Failed);

    /// <summary>Time asleep waiting for work since start, in <see cref="Stopwatch"/> ticks (any thread).</summary>
    public long IdleTicks => Volatile.Read(ref _counters.IdleTicks);

    /// <summary>Ticks that started a whole interval late since start: the shard cannot keep its tick rate (any thread).</summary>
    public long TickOverruns => Volatile.Read(ref _counters.TickOverruns);

    /// <summary>Active loaded actors (approximate, any thread).</summary>
    public int ActorCount => Volatile.Read(ref _counters.ActorCount);

    /// <summary>How long the shard thread has been in its current pass (0 while it sleeps): the stall watchdog's input.</summary>
    internal long PassMs(long now)
    {
        long started = Volatile.Read(ref _counters.PassStartedMs);
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
        if (Volatile.Read(ref _closed) != 0)
        {
            Closed(frame, sink); // the shard stopped (its node leaves)
            return true;
        }

        if (!_dispatchQueue.TryEnqueue(new WorkItem(WorkKind.Dispatch, frame, sink)))
        {
            Interlocked.Increment(ref _queueFull.Value);
            return false;
        }

        Wake();
        if (Volatile.Read(ref _closed) != 0) ForwardQueued(); // it stopped as this frame went in: nobody else takes it
        return true;
    }

    private int _closed; // 1 once the shard's loop ended: admitted frames go on to their owner instead of waiting

    /// <summary>The shard stopped: what is still admitted goes on to the keys' owners (any thread).</summary>
    private void ForwardQueued()
    {
        while (_dispatchQueue.TryDequeue(out var item)) Closed((FrameWriter)item.A!, (IReplySink?)item.B);
    }

    /// <summary>A frame for a stopped shard: a handoff frame is answered here (it holds nothing), mail goes on to its key's owner.</summary>
    private void Closed(FrameWriter frame, IReplySink? sink)
    {
        if (!Persistence.Handoff.Bounce(Node, frame)) Node.Forward(frame, sink);
    }

    /// <summary>On the shard's own thread only: an actor's mailbox was full.</summary>
    internal void CountMailboxFull() => Volatile.Write(ref _counters.MailboxFull, _counters.MailboxFull + 1);

    /// <summary>On the shard's own thread only (no interlocked add: nobody else writes it).</summary>
    internal void CountExpired() => Volatile.Write(ref _counters.Expired, _counters.Expired + 1);

    /// <summary>On the shard's own thread only.</summary>
    internal void CountFailed() => Volatile.Write(ref _counters.Failed, _counters.Failed + 1);

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

    /// <summary>
    /// Wake the shard if it sleeps. One waker per sleep: the first to clear the flag sets the event, the others see it
    /// cleared. Every producer setting it would serialize them on the event's lock (a hot spot under many connections).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Wake()
    {
        if (Volatile.Read(ref _sleeping.Value) != 0 && Interlocked.Exchange(ref _sleeping.Value, 0) != 0)
            _wake.Set();
    }

    /// <summary>Run <paramref name="callback"/> on this shard's thread (thread-safe, allocation-free).</summary>
    public override void Post(Action<object?> callback, object? state) => Post(new WorkItem(WorkKind.Callback, callback, state));

    /// <summary>Thread-safe: ownership may have changed; actors this node no longer owns are unloaded.</summary>
    internal void RequestRebalance() =>
        Post(static s => ((Shard)s!).World.ShardActivity.Rebalance = true, this);

    /// <summary>Thread-safe: unload every current loaded actor (this node was declared Dead; its keys moved).</summary>
    internal void RequestUnloadAll() =>
        Post(static s =>
        {
            ref var activity = ref ((Shard)s!).World.ShardActivity;
            activity.DeclaredDead = activity.Rebalance = true;
        }, this);

    /// <summary>
    /// Thread-safe (the admin endpoint): every loaded actor of <paramref name="handlerTypeId"/> leaves once idle, saved as
    /// on any unload; its next mail loads it again.
    /// </summary>
    internal void RequestUnload(uint handlerTypeId) =>
        Post(static s =>
        {
            var (shard, id) = ((Shard, uint))s!;
            List<Entity>? leaving = null;
            foreach (var item in shard.World.Query<ActorKey>(Live))
                if (item.ActorKey.HandlerTypeId == id) (leaving ??= []).Add(item.Entity);
            foreach (var actor in leaving ?? []) actor.SetUnloadRequested(); // after the query: a structural change
        }, (this, handlerTypeId));

    /// <summary>Stop the shard: unload every actor (<paramref name="fenced"/>: remove them without their <see cref="Unloading"/> mail).</summary>
    internal void RequestStop(bool fenced = false)
    {
        _stopFenced = fenced;
        _stopRequested = true;
        _wake.Set();
    }

    /// <summary>
    /// Queue consumer (shard thread only, used by <see cref="DispatchSystem"/>): work that finishes admitted work first
    /// (it frees calls and their buffers), then new work.
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
            // A world that fails to build (a user service's constructor, a system's OnCreate) crashes the shard: it
            // reads as stalled from here, so /alive fails and the platform restarts the node.
            Volatile.Write(ref _counters.PassStartedMs, Platform.Milliseconds);
            CreateWorld();
            long nextTick = _time.GetTimestamp() + _tickInterval;
            long nextIdleScan = 0;
            bool busy = false; // the last pass had work: under load the next message is microseconds away
            ref var activity = ref World.ShardActivity; // singletons never move
            while (true)
            {
                Volatile.Write(ref _counters.PassStartedMs, Platform.Milliseconds);
                activity.Processed = 0;
                long now = _time.GetTimestamp();
                activity.NowMs = now * 1000 / _time.TimestampFrequency;
                if (_stopRequested && !activity.Stopping)
                {
                    activity.Stopping = activity.Rebalance = true;
                    activity.DeclaredDead |= _stopFenced; // a downed node saves nothing: the majority owns the keys now
                }

                using (FrameChannel.BeginSendBatch()) // everything this wake sends leaves in one write per connection
                {
                    // Cadence is the loop's job; each group's systems only do their one thing.
                    if (activity.Rebalance)
                    {
                        activity.Rebalance = false;
                        World.Update<ShardRebalance>();
                        activity.DeclaredDead = false;
                    }

                    if (activity.NowMs >= nextIdleScan)
                    {
                        nextIdleScan = activity.NowMs + IdleScanMs;
                        World.Update<ShardIdleScan>();
                    }

                    World.Update<ShardMessages>();

                    if (now >= nextTick)
                    {
                        Volatile.Write(ref _counters.TickCount, _counters.TickCount + 1);
                        World.Update();
                        nextTick += _tickInterval;
                        if (now >= nextTick) Volatile.Write(ref _counters.TickOverruns, _counters.TickOverruns + 1); // a tick behind
                        if (now - nextTick > _tickInterval * 5)
                            nextTick = now + _tickInterval; // fell far behind: skip instead of a catch-up storm
                    }
                }

                ReviveFailedSystems();

                int actors = World.Count<ActorKey>();
                if (actors != _counters.ActorCount) Volatile.Write(ref _counters.ActorCount, actors); // written only when it changes
                if (activity.Stopping && IsDrained()) break;
                bool idle = activity.Processed == 0 && !World.Any<ActorKey>(MailSystem.Runnable);
                if (idle)
                    Sleep(World.ShardTimers.Wheel.Count == 0 ? nextTick
                        : Math.Min(nextTick, _time.GetTimestamp() + _timerWake), spin: busy, activity.Stopping); // a delayed send waits for no tick
                busy = !idle;
            }
        }
        catch (Exception e)
        {
            Log.ShardCrashed(Node.Logger, e, Index);
        }
        finally
        {
            if (_stopRequested) Volatile.Write(ref _counters.PassStartedMs, 0); // a shard that stopped is not stalled; one that crashed is, for /alive
            Interlocked.Exchange(ref _closed, 1); // a full fence: the queue is read after the flag is seen, or an admit racing it is stranded
            ForwardQueued();
            try
            {
                if (World.HasShardTimers) World.ShardTimers.Drop();
                World.Dispose();
            }
            finally
            {
                SetCurrent(null);
                _stopped.TrySetResult();
            }
        }
    }

    /// <summary>
    /// A system threw (EasyEcs catches per system while this is subscribed, and runs the group's next system): counted
    /// with the failed handlers, logged at most once a second per shard, and the shard goes on, as a handler that throws
    /// does (<see cref="DispatchSystem"/>). One bug must not stop the shard's other systems or actors.
    /// </summary>
    /// <summary>
    /// A system that threw (one actor's data, a bug in user code) is reported and runs again on its next pass: the
    /// world disables it (<see cref="SystemErrorPolicy.Disable"/>), and one bad entity must not stop the system for
    /// every other one.
    /// </summary>
    private void ReviveFailedSystems()
    {
        foreach (var system in World.Systems)
        {
            if (system.Error is not { } error) continue;
            system.Enabled = true; // clears Error
            CountFailed();
            if (ErrorLogDue(out int unlogged)) Log.SystemFailed(Node.Logger, error, system.GetType().Name, Index, unlogged);
        }
    }

    /// <summary>
    /// Whether an error that can repeat on every pass (a failing system, a record that does not serialize) is logged now:
    /// at most once a second per shard; <paramref name="unlogged"/> is how many were left out since the last. Shard thread.
    /// </summary>
    internal bool ErrorLogDue(out int unlogged) => _errors.Due(out unlogged);

    private LogThrottle _errors = new(1000); // shard thread only

    /// <summary>Singletons, runtime systems, the node's user systems and service instances.</summary>
    private void CreateWorld()
    {
        World.AddSingleton<ShardContext>() = new ShardContext(this);
        World.AddSingleton<ShardActivity>();
        World.AddSingleton<ShardTimers>() = new ShardTimers(new TimerWheel<DelayedSend>(ShardTimers.Resolution, time: _time));
        World.AddSingleton<MailScratch>() = new MailScratch(new List<Envelope>());
        World.AddSingleton<AnswerCalls>() = new AnswerCalls();
        World.AddSingleton<Persistence.HandoffAsks>() = new Persistence.HandoffAsks(new List<Persistence.HandoffAsk>());
        World.AddSingleton<Persistence.HandoffsSeen>() = new Persistence.HandoffsSeen();
        // Every actor moving to another node takes its client sessions along, whatever components it persists.
        Persistence.PersistentType[] moved = [.. ServerRegistry.PersistentTypes, Sessions];
        var stored = Array.FindAll(moved, static t => t.Stored);
        World.AddSingleton<ShardStore>() = new ShardStore(stored.Length > 0 ? Node.Options.ActorStore : null, stored, moved,
            moved.ToFrozenDictionary(static t => t.TypeId), new LoadBatch(this), new SaveBatch(this));

        World.AddSystem<TimerSystem>();
        World.AddSystem<DispatchSystem>();
        World.AddSystem<MailSystem>();
        World.AddSystem<RemovalSystem>();
        World.AddSystem<RebalanceSystem>();
        World.AddSystem<IdleSystem>();
        World.AddSystem<ReplySlotSystem>();
        World.AddSystem<UnloadSystem>();
        World.AddSystem<LoadSystem>();
        World.AddSystem<PersistSystem>();
        World.AddSystem<ChangeSystem>();
        World.AddSystem<CheckpointSystem>();
        World.AddSystem<StoreSystem>();
        World.AddSystem<HandoffSystem>();
        World.AddSystem<HandoffTimeoutSystem>();
        World.AddSystem<HandoffAsksSystem>();
        foreach (var info in Node.HostedSystems) World.AddSystem(info.Factory());
        foreach (var feature in Node.Options.Features) feature.ConfigureShard(World);
    }

    /// <summary>Stopping: every actor removed, no ask of a handler in flight, nothing left in the dispatch queue.</summary>
    private bool IsDrained() =>
        !World.Any<ActorKey>() && World.AnswerCalls.InFlight.Count == 0 && QueueLength == 0;

    /// <summary>Actors that are neither leaving nor going.</summary>
    private static readonly QueryFilter Live = QueryFilter.Create().Without<UnloadRequested>().Without<Leaving>().Without<Removing>();

    /// <summary>Spins of a busy shard before it sleeps (<see cref="Thread.SpinWait"/> rounds; tens of microseconds).</summary>
    private const int BusySpins = 20, SpinRound = 50;

    /// <param name="stopping">The stop request was taken: it no longer cuts sleeps short (a shard waiting for its last
    /// writes and handoffs to end sleeps until their completions wake it, instead of spinning).</param>
    private void Sleep(long nextTick, bool spin, bool stopping)
    {
        // A shard that just had work waits for more by spinning a little first: under load the next message arrives
        // within microseconds, and a sleep and a wake (futex calls, the event's lock) cost more than that.
        if (spin)
            for (int i = 0; i < BusySpins; i++)
            {
                if (QueueLength != 0 || _stopRequested && !stopping) return;
                Thread.SpinWait(SpinRound);
            }

        Interlocked.Exchange(ref _sleeping.Value, 1);
        Volatile.Write(ref _counters.PassStartedMs, 0);
        if (QueueLength == 0 && !(_stopRequested && !stopping)) // a stop request not taken yet ends the sleep
        {
            long remaining = nextTick - _time.GetTimestamp();
            if (remaining > 0) // a manual clock (tests) wakes the shard itself when it moves on (Wake)
            {
                long asleep = Stopwatch.GetTimestamp();
                _wake.Wait(TimeSpan.FromSeconds(Math.Min(1, (double)remaining / _time.TimestampFrequency)));
                Volatile.Write(ref _counters.IdleTicks, _counters.IdleTicks + Stopwatch.GetTimestamp() - asleep);
            }
        }

        Volatile.Write(ref _sleeping.Value, 0);
        _wake.Reset(); // a waker that cleared the flag may still set it: the next sleep then returns at once, harmlessly
    }
}

/// <summary>
/// What a shard's thread writes every pass, read by the node's metrics and watchdog: on lines of their own, padded on
/// both sides (see <see cref="PaddedLong"/>), so producers reading the shard's other fields do not miss on every pass.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct ShardCounters
{
    [FieldOffset(64)] public long PassStartedMs; // Platform.Milliseconds when the running pass started, 0 while asleep
    [FieldOffset(72)] public long TickCount;
    [FieldOffset(80)] public long Expired;
    [FieldOffset(88)] public long Failed;
    [FieldOffset(96)] public int ActorCount;
    [FieldOffset(104)] public long IdleTicks; // Stopwatch ticks spent asleep, waiting for work (busy ratio = the rest)
    [FieldOffset(112)] public long TickOverruns; // ticks that started a whole interval late (the shard cannot keep its rate)
    [FieldOffset(120)] public long MailboxFull;
}
