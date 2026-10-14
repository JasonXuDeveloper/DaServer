using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Concurrency;
using EasyEcs;
using Microsoft.Extensions.Logging;

namespace DaServer.Runtime;

/// <summary>
/// A single-threaded shard: one dedicated thread owning an EasyEcs <see cref="EasyEcs.World"/>, plus the lock-free
/// inbox other threads post to. Everything else is ECS: actors are entities (mailbox, turn and state components),
/// directories and timers are singletons, and runtime systems in <see cref="ShardMessages"/> route and run messages.
/// <para>Loop: on every wake the <see cref="ShardMessages"/> group runs (so messages never wait for a tick); at the
/// tick rate the default group runs (simulation systems). User code never needs locks.</para>
/// </summary>
public sealed class Shard : IDisposable
{
    [ThreadStatic] private static Shard? _current;

    private readonly MpmcQueue<WorkItem> _inbox;
    private readonly ConcurrentQueue<WorkItem> _overflow = new(); // only used if the bounded ring is full
    private int _overflowCount;
    private readonly ManualResetEventSlim _wake = new(false, spinCount: 0);
    private int _sleeping;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopRequested;
    private readonly long _tickInterval; // Stopwatch ticks
    private int _actorCount;

    internal Shard(Node node, int index, int mailboxCapacity, int tickRate)
    {
        Node = node;
        Index = index;
        _inbox = new MpmcQueue<WorkItem>(mailboxCapacity);
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

    /// <summary>Active actor activations (approximate, any thread).</summary>
    public int ActorCount => Volatile.Read(ref _actorCount);

    /// <summary>Completes when the shard thread has exited.</summary>
    public Task Stopped => _stopped.Task;

    internal void Start() => _thread.Start();

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

    /// <summary>Run <paramref name="callback"/> on the shard thread after <paramref name="delay"/>. Shard thread only.</summary>
    public void Schedule(TimeSpan delay, Action<object?> callback, object? state)
    {
        AssertShardThread();
        long due = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        World.GetSingleton<ShardTimers>().Queue.Enqueue(new TimerEntry(callback, state), due);
    }

    /// <summary>Thread-safe: ownership may have changed; actors this node no longer owns are deactivated.</summary>
    internal void RequestRebalance() =>
        Post(static s => ((Shard)s!).World.GetSingleton<ShardActivity>().Rebalance = true, this);

    internal void RequestStop()
    {
        _stopRequested = true;
        _wake.Set();
    }

    public void Dispose() => RequestStop();

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
            while (true)
            {
                ref var activity = ref World.GetSingleton<ShardActivity>();
                activity.Processed = 0;
                if (_stopRequested && !activity.Stopping) activity.Stopping = activity.Rebalance = true;

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

                activity = ref World.GetSingleton<ShardActivity>();
                Volatile.Write(ref _actorCount, World.GetSingleton<ActorDirectory>().Actors.Count);
                if (activity.Stopping && IsDrained()) break;
                if (activity.Processed == 0 && World.GetSingleton<ReadyActors>().Queue.Count == 0)
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
        World.AddSingleton<ReadyActors>() = new ReadyActors(new Queue<Entity>());
        World.AddSingleton<ServiceTasks>() = new ServiceTasks(new List<ValueTask>());
        World.AddSingleton<ShardTimers>() = new ShardTimers(new PriorityQueue<TimerEntry, long>());

        var services = new Dictionary<uint, object>();
        foreach (var impl in Node.HostedImplementations)
        {
            if (impl.Kind != Rpc.RpcContractKind.Service) continue;
            var instance = impl.Factory();
            if (instance is ShardObject shardObject) shardObject.Shard = this;
            foreach (var contractType in impl.Contracts)
                services[Rpc.RpcRegistry.GetContract(contractType).Id] = instance;
        }

        World.AddSingleton<ServiceDirectory>() = new ServiceDirectory(services);

        World.AddSystem<InboxSystem>();
        World.AddSystem<TurnSystem>();
        World.AddSystem<MailboxSystem>();
        World.AddSystem<ServiceTaskSystem>();
        World.AddSystem<TimerSystem>();
        World.AddSystem<RebalanceSystem>();
        foreach (var info in Node.HostedSystems) World.AddSystem(info.Factory());
    }

    /// <summary>Stopping: every actor retired, no service call running, nothing left in the inbox.</summary>
    private bool IsDrained() =>
        World.GetSingleton<ActorDirectory>().Actors.Count == 0 &&
        World.GetSingleton<ServiceTasks>().Tasks.Count == 0 &&
        QueueLength == 0;

    private void Sleep(long nextTick)
    {
        long wakeAt = nextTick;
        if (World.GetSingleton<ShardTimers>().Queue.TryPeek(out _, out long timerDue) && timerDue < wakeAt)
            wakeAt = timerDue;

        Interlocked.Exchange(ref _sleeping, 1);
        if (QueueLength == 0 && !_stopRequested)
        {
            long remaining = wakeAt - Stopwatch.GetTimestamp();
            if (remaining > 0)
                _wake.Wait(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
        }

        Volatile.Write(ref _sleeping, 0);
        _wake.Reset();
    }

    [Conditional("DEBUG")]
    private void AssertShardThread()
    {
        if (_current != this)
            throw new InvalidOperationException($"Shard {Index} API called from outside its thread");
    }
}
