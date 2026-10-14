using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Buffers;
using DaServer.Concurrency;
using DaServer.Protocol;
using DaServer.Rpc;
using EasyEcs.Core;
using Microsoft.Extensions.Logging;
using Nino.Core;

namespace DaServer.Runtime;

/// <summary>
/// A single-threaded shard: lock-free mailbox + an EasyEcs <see cref="EasyEcs.Core.World"/> + timers + fixed-rate tick,
/// on one dedicated thread.
/// Every actor method, service method, continuation and ECS system of the shard runs on that thread, so user code
/// never needs locks. Messages are processed as soon as they arrive (the tick rate only drives ECS systems/timers).
/// </summary>
public sealed class Shard : IDisposable
{
    [ThreadStatic] private static Shard? _current;

    private readonly MpmcQueue<WorkItem> _mailbox;
    private readonly ConcurrentQueue<WorkItem> _overflow = new(); // only used if the bounded ring is full
    private int _overflowCount;
    private readonly ManualResetEventSlim _wake = new(false, spinCount: 0);
    private int _sleeping;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;

    private readonly long _tickInterval; // Stopwatch ticks
    private readonly PriorityQueue<TimerEntry, long> _timers = new();
    private readonly Dictionary<ActorId, ActorSlot> _actors = new();
    private readonly Dictionary<uint, object> _services = new();
    private readonly List<ValueTask> _inflight = new();
    private readonly List<ActorSlot> _activating = new();

    internal Shard(Node node, int index, int mailboxCapacity, int tickRate)
    {
        Node = node;
        Index = index;
        _mailbox = new MpmcQueue<WorkItem>(mailboxCapacity);
        _tickInterval = Stopwatch.Frequency / Math.Max(1, tickRate);
        World = new World();
        _thread = new Thread(Run) { IsBackground = true, Name = $"{node.Options.Name}-shard-{index}" };
    }

    /// <summary>The shard whose thread is executing the current code, or null outside shard threads.</summary>
    public static Shard? Current => _current;

    public Node Node { get; }

    /// <summary>Index of the shard inside its node.</summary>
    public int Index { get; }

    /// <summary>The shard's ECS world (systems registered for this node's roles run every tick).</summary>
    public World World { get; }

    /// <summary>Number of completed ticks.</summary>
    public long TickCount { get; private set; }

    /// <summary>Messages waiting in the mailbox (approximate).</summary>
    public int QueueLength => _mailbox.Count + Volatile.Read(ref _overflowCount);

    public int ActorCount => _actors.Count;

    /// <summary>Completes when the shard thread has exited.</summary>
    public Task Stopped => _stopped.Task;

    internal void Start() => _thread.Start();

    /// <summary>Thread-safe: enqueue work and wake the shard if it is sleeping.</summary>
    internal void Post(in WorkItem item)
    {
        if (!_mailbox.TryEnqueue(item))
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
        AssertWorldThread();
        long due = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        _timers.Enqueue(new TimerEntry(callback, state), due);
    }

    internal void RequestStop()
    {
        _stopping = true;
        _wake.Set();
    }

    public void Dispose() => RequestStop();

    // ------------------------------------------------------------------ loop

    private void Run()
    {
        _current = this;
        SynchronizationContext.SetSynchronizationContext(new ShardSynchronizationContext(this));
        try
        {
            World.AddSingleton<ShardInfo>().Shard = this;
            foreach (var system in Node.HostedSystems)
                system.Add(World);
            CreateServices();

            long nextTick = Stopwatch.GetTimestamp() + _tickInterval;
            while (true)
            {
                int processed = Drain(4096);
                SweepInflight();

                long now = Stopwatch.GetTimestamp();
                RunTimers(now);

                if (now >= nextTick)
                {
                    TickCount++;
                    World.Update();
                    nextTick += _tickInterval;
                    if (now - nextTick > _tickInterval * 5)
                        nextTick = now + _tickInterval; // fell far behind: skip instead of a catch-up storm
                }

                if (_stopping && processed == 0 && _inflight.Count == 0 && _activating.Count == 0)
                    break;

                if (processed == 0)
                    Sleep(nextTick);
            }

            ShutdownActors();
            World.Dispose();
        }
        catch (Exception e)
        {
            Node.Logger.LogCritical(e, "Shard {Shard} crashed", Index);
        }
        finally
        {
            _current = null;
            _stopped.TrySetResult();
        }
    }

    private void Sleep(long nextTick)
    {
        long wakeAt = nextTick;
        if (_timers.TryPeek(out _, out long timerDue) && timerDue < wakeAt)
            wakeAt = timerDue;

        Interlocked.Exchange(ref _sleeping, 1);
        if (_mailbox.IsEmpty && Volatile.Read(ref _overflowCount) == 0 && !_stopping)
        {
            long remaining = wakeAt - Stopwatch.GetTimestamp();
            if (remaining > 0)
                _wake.Wait(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
        }

        Volatile.Write(ref _sleeping, 0);
        _wake.Reset();
    }

    private int Drain(int budget)
    {
        int processed = 0;
        while (processed < budget)
        {
            if (!_mailbox.TryDequeue(out var item))
            {
                if (Volatile.Read(ref _overflowCount) == 0 || !_overflow.TryDequeue(out item))
                    break;
                Interlocked.Decrement(ref _overflowCount);
            }

            Execute(in item);
            processed++;
        }

        return processed;
    }

    private void Execute(in WorkItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case WorkKind.Dispatch:
                    Dispatch((FrameWriter)item.A!, (IReplySink?)item.B);
                    break;
                case WorkKind.Complete:
                    var frame = (FrameWriter)item.B!;
                    try
                    {
                        var header = FrameHeader.Read(frame.Frame);
                        ((IPendingCall)item.A!).Complete(header.Status, frame.Payload);
                    }
                    finally
                    {
                        frame.Dispose();
                    }

                    break;
                case WorkKind.Callback:
                    if (item.A is SendOrPostCallback post) post(item.B);
                    else ((Action<object?>)item.A!)(item.B);
                    break;
            }
        }
        catch (Exception e)
        {
            Node.Logger.LogError(e, "Unhandled exception in shard {Shard}", Index);
        }
    }

    private void RunTimers(long now)
    {
        while (_timers.TryPeek(out var timer, out long due) && due <= now)
        {
            _timers.Dequeue();
            try
            {
                timer.Callback(timer.State);
            }
            catch (Exception e)
            {
                Node.Logger.LogError(e, "Timer callback failed in shard {Shard}", Index);
            }
        }
    }

    private void SweepInflight()
    {
        for (int i = _inflight.Count - 1; i >= 0; i--)
        {
            var task = _inflight[i];
            if (!task.IsCompleted) continue;

            _inflight.RemoveAt(i);
            try
            {
                task.GetAwaiter().GetResult(); // returns pooled state machines to their pools
            }
            catch (Exception e)
            {
                Node.Logger.LogError(e, "Async handler failed in shard {Shard}", Index);
            }
        }

        for (int i = _activating.Count - 1; i >= 0; i--)
        {
            var slot = _activating[i];
            if (!slot.Activation.IsCompleted) continue;

            _activating.RemoveAt(i);
            CompleteActivation(slot);
        }
    }

    // ------------------------------------------------------------------ dispatch

    private void Dispatch(FrameWriter frame, IReplySink? sink)
    {
        var header = FrameHeader.Read(frame.Frame);
        uint requestId = header.Kind == FrameKind.Request ? header.RequestId : 0;
        try
        {
            if (!RpcRegistry.TryGetMethod(header.MethodId, out var method))
            {
                Reply(sink, requestId, RpcStatus.UnknownMethod, "Unknown method");
                return;
            }

            var contract = method.Owner;
            object? target;
            if (contract.Kind == RpcContractKind.Actor)
            {
                if (!Node.Placement.IsLocalOwner(contract, header.Key))
                {
                    Node.Forward(frame, sink); // takes ownership of the frame
                    frame = null!;
                    return;
                }

                var slot = GetOrActivate(contract, header.Key);
                if (slot == null)
                {
                    Reply(sink, requestId, RpcStatus.NotHosted, contract.ToString());
                    return;
                }

                if (!slot.Activated)
                {
                    slot.Queue(frame, sink); // replayed once OnActivateAsync completes
                    frame = null!;
                    return;
                }

                target = slot.Actor;
            }
            else if (!_services.TryGetValue(contract.Id, out target))
            {
                Reply(sink, requestId, RpcStatus.NotHosted, contract.ToString());
                return;
            }

            Invoke(contract, target!, in header, frame, sink, requestId);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void Invoke(RpcContractInfo contract, object target, in FrameHeader header, FrameWriter frame, IReplySink? sink,
        uint requestId)
    {
        var payload = frame.Payload;
        var caller = default(SessionRef);
        if ((header.Flags & FrameFlags.HasCaller) != 0)
        {
            var callerReader = new Reader(payload);
            NinoDeserializer.Deserialize(out caller, ref callerReader);
            payload = payload.Slice(SessionRefSize);
        }

        var context = new CallContext(sink, requestId, header.MethodId, header.Key, caller);
        var previous = CallContext.Enter(in context);
        try
        {
            var reader = new Reader(payload);
            var task = contract.Dispatcher(target, header.MethodId, ref reader, in context);
            if (task.IsCompleted)
                task.GetAwaiter().GetResult();
            else
                _inflight.Add(task);
        }
        catch (Exception e)
        {
            context.ReplyException(e);
        }
        finally
        {
            CallContext.Exit(in previous);
        }
    }

    /// <summary>Serialized size of a <see cref="SessionRef"/> caller prefix.</summary>
    internal static readonly int SessionRefSize = NinoSerializer.Serialize(default(SessionRef)).Length;

    private static void Reply(IReplySink? sink, uint requestId, RpcStatus status, string message)
    {
        if (sink == null || requestId == 0) return;
        var w = FrameWriter.Rent();
        var nino = w.Nino;
        NinoSerializer.Serialize(message, ref nino);
        sink.SendReply(requestId, status, w);
    }

    // ------------------------------------------------------------------ services & actors

    private void CreateServices()
    {
        foreach (var impl in Node.HostedImplementations)
        {
            if (impl.Kind != RpcContractKind.Service) continue;
            var instance = impl.Factory();
            if (instance is IShardAware aware) aware.Attach(this);
            foreach (var contractType in impl.Contracts)
                _services[RpcRegistry.GetContract(contractType).Id] = instance;
        }
    }

    private ActorSlot? GetOrActivate(RpcContractInfo contract, long key)
    {
        var impl = Node.ImplementationOf(contract);
        if (impl == null) return null;

        var id = new ActorId(impl.Id, key);
        if (_actors.TryGetValue(id, out var slot))
            return slot;

        var actor = (Actor)impl.Factory();
        actor.Key = key;
        actor.Shard = this;
        var entity = World.CreateEntity();
        entity.Value.AddComponent<ActorLink>().Value.Actor = actor;
        actor.Entity = entity;

        slot = new ActorSlot(id, actor);
        _actors.Add(id, slot);

        ValueTask activation;
        try
        {
            activation = actor.OnActivateAsync();
        }
        catch (Exception e)
        {
            activation = ValueTask.FromException(e);
        }

        slot.Activation = activation;
        if (activation.IsCompleted)
            CompleteActivation(slot);
        else
            _activating.Add(slot);
        return slot;
    }

    private void CompleteActivation(ActorSlot slot)
    {
        try
        {
            slot.Activation.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Node.Logger.LogError(e, "Activation of {Actor} {Key} failed", slot.Actor.GetType().Name, slot.Id.Key);
            RemoveActivation(slot);
            slot.FailPending(this, RpcStatus.Error, "Activation failed: " + e.Message);
            return;
        }

        slot.Activated = true;
        slot.ReplayPending(this);
    }

    internal void ReplayDispatch(FrameWriter frame, IReplySink? sink) => Dispatch(frame, sink);

    /// <summary>Deactivate an actor (OnDeactivateAsync, then remove its entity). Shard thread only.</summary>
    public async ValueTask DeactivateAsync(Actor actor)
    {
        AssertWorldThread();
        var id = new ActorId(Node.ImplementationIdOf(actor), actor.Key);
        if (!_actors.TryGetValue(id, out var slot) || slot.Actor != actor) return;

        _actors.Remove(id);
        try
        {
            await actor.OnDeactivateAsync();
        }
        finally
        {
            World.DestroyEntity(actor.Entity);
        }
    }

    private void RemoveActivation(ActorSlot slot)
    {
        _actors.Remove(slot.Id);
        World.DestroyEntity(slot.Actor.Entity);
    }

    /// <summary>Deactivate actors this node no longer owns (membership changed). Shard thread only.</summary>
    internal void RebalanceActors()
    {
        List<Actor>? moved = null;
        foreach (var slot in _actors.Values)
        {
            if (slot.Activated && !Node.Placement.IsLocalOwner(slot.Id.ImplementationId, slot.Id.Key))
                (moved ??= new()).Add(slot.Actor);
        }

        if (moved == null) return;
        foreach (var actor in moved)
            _inflight.Add(DeactivateAsync(actor));
    }

    private void ShutdownActors()
    {
        var actors = new List<Actor>();
        foreach (var slot in _actors.Values)
            if (slot.Activated) actors.Add(slot.Actor);

        foreach (var actor in actors)
        {
            var task = DeactivateAsync(actor);
            while (!task.IsCompleted)
            {
                if (Drain(256) == 0) Thread.Yield();
            }

            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Node.Logger.LogError(e, "Deactivation of {Actor} {Key} failed", actor.GetType().Name, actor.Key);
            }
        }
    }

    [Conditional("DEBUG")]
    private void AssertWorldThread()
    {
        if (_current != this)
            throw new InvalidOperationException($"Shard {Index} API called from outside its thread");
    }

    private readonly record struct TimerEntry(Action<object?> Callback, object? State);

    internal readonly record struct ActorId(uint ImplementationId, long Key);

    private sealed class ActorSlot(ActorId id, Actor actor)
    {
        public readonly ActorId Id = id;
        public readonly Actor Actor = actor;
        public ValueTask Activation;
        public bool Activated;
        private Queue<(FrameWriter Frame, IReplySink? Sink)>? _pending;

        public void Queue(FrameWriter frame, IReplySink? sink) => (_pending ??= new()).Enqueue((frame, sink));

        public void ReplayPending(Shard shard)
        {
            if (_pending == null) return;
            while (_pending.TryDequeue(out var item))
                shard.ReplayDispatch(item.Frame, item.Sink);
            _pending = null;
        }

        public void FailPending(Shard shard, RpcStatus status, string message)
        {
            if (_pending == null) return;
            while (_pending.TryDequeue(out var item))
            {
                var header = FrameHeader.Read(item.Frame.Frame);
                item.Frame.Dispose();
                if (header.Kind == FrameKind.Request)
                    Reply(item.Sink, header.RequestId, status, message);
            }

            _pending = null;
        }
    }
}

/// <summary>Singleton component giving ECS systems access to their shard.</summary>
public struct ShardInfo : EasyEcs.Core.Components.ISingletonComponent
{
    public Shard Shard;
}

/// <summary>Implemented by services that want their shard injected at creation.</summary>
public interface IShardAware
{
    void Attach(Shard shard);
}
