using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nino.Core;

namespace DaServer.Rpc;

/// <summary>
/// A cluster event: a <c>[NinoType]</c> struct describing something that happened ("player logged in"), published with
/// <c>Cluster.Publish(e)</c>. The publisher does not know its handlers: every hosted implementation of
/// <see cref="IHandle{TEvent}"/> anywhere in the cluster receives it.
/// <list type="bullet">
/// <item>Service handlers: once per handling service type, on one node (local preferred).</item>
/// <item>Actor handlers: only <see cref="IKeyedEvent"/>s, in the activation whose key is <see cref="IKeyedEvent.Key"/>
/// (activated on demand), as one more message of its mailbox (turn-based like calls).</item>
/// </list>
/// Delivery is at most once, like one-way calls: the event travels with the calls of the same sender in order.
/// </summary>
public interface IEvent;

/// <summary>An event about one keyed entity (a player, a guild): actor handlers receive it in the activation with <see cref="Key"/>.</summary>
public interface IKeyedEvent : IEvent
{
    /// <summary>Key of the actor activations that receive this event.</summary>
    long Key { get; }
}

/// <summary>Handles cluster events of type <typeparamref name="TEvent"/> (implemented by actors and services).</summary>
public interface IHandle<TEvent> where TEvent : struct, IEvent
{
    ValueTask Handle(TEvent e);
}

/// <summary>
/// The gateway's session of a client closed (disconnect, kick). Delivered to every actor the session was bound to
/// (<c>Cluster.Bind&lt;TActor&gt;</c>) that handles it: <see cref="Key"/> is the bound key.
/// </summary>
[NinoType]
public readonly partial record struct SessionClosed(SessionRef Session, long Key) : IKeyedEvent;

/// <summary>Wire identity of event type <typeparamref name="TEvent"/>: FNV-1a of its full name, computed once.</summary>
public static class EventType<TEvent> where TEvent : struct, IEvent
{
    public static readonly uint Id = RpcIds.Fnv1a(typeof(TEvent).FullName ?? typeof(TEvent).Name);

    /// <summary>Actor handlers need a key: only keyed events reach them.</summary>
    public static readonly bool Keyed = typeof(IKeyedEvent).IsAssignableFrom(typeof(TEvent));

    // A cast to IKeyedEvent would box the struct; a constrained generic call bound once per event type does not.
    private static readonly KeyReader? ReadKey = Keyed
        ? (KeyReader)Delegate.CreateDelegate(typeof(KeyReader),
            typeof(EventType<TEvent>).GetMethod(nameof(KeyOfKeyed), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(TEvent)))
        : null;

    private delegate long KeyReader(in TEvent e);

    /// <summary>Key of a keyed event, 0 otherwise (no boxing).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long KeyOf(in TEvent e) => ReadKey == null ? 0 : ReadKey(in e);

    private static long KeyOfKeyed<TKeyed>(in TKeyed e) where TKeyed : struct, IKeyedEvent => e.Key;

    /// <summary>Deserializes the event and invokes <see cref="IHandle{TEvent}.Handle"/> (the dispatcher of a handler).</summary>
    public static ValueTask Dispatch(object target, uint eventId, ref Reader reader, in CallContext context)
    {
        NinoDeserializer.Deserialize(out TEvent e, ref reader);
        return Unsafe.As<IHandle<TEvent>>(target).Handle(e);
    }
}

/// <summary>One event type handled by an implementation (generated registration).</summary>
public sealed class EventHandlerInfo(uint eventId, bool keyed, RpcDispatcher dispatcher, Type eventType)
{
    public uint EventId { get; } = eventId;
    public bool Keyed { get; } = keyed;
    public RpcDispatcher Dispatcher { get; } = dispatcher;
    public Type EventType { get; } = eventType;

    /// <summary>Handler registration for <typeparamref name="TEvent"/> (used by generated code).</summary>
    public static EventHandlerInfo Of<TEvent>() where TEvent : struct, IEvent =>
        new(EventType<TEvent>.Id, EventType<TEvent>.Keyed, EventType<TEvent>.Dispatch, typeof(TEvent));

    public override string ToString() => EventType.FullName ?? EventType.Name;
}
