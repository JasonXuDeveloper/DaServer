using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nino.Core;

namespace DaServer.Rpc;

// Events: any [NinoType] struct published with Cluster.Publish(e); the publisher never knows its handlers.
//   - Handlers are methods marked [Handle] on services and actors, taking the event: [Handle] void On(PlayerLoggedIn e).
//   - Service handlers run once per handling service type, on one node (local first).
//   - Actor handlers need a keyed event (one member marked [Key]): the activation of that key receives it as one more
//     message of its mailbox (activated on demand, turn-based like calls).
//   - Delivery is at most once, like one-way calls; events of one sender travel in order with its calls.

/// <summary>
/// Marks a cluster event handler on a service or actor: one parameter (the event, a <c>[NinoType]</c> struct), returns
/// <c>void</c> or <c>ValueTask</c>. Registration and dispatch are generated.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HandleAttribute : Attribute;

/// <summary>
/// The key of an event (a <c>long</c> field or property of a partial struct): actor handlers receive the event in the
/// activation with this key. <c>[NinoType] public partial record struct PlayerLoggedIn([property: Key] long Player)</c>
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute;

/// <summary>Implemented by the generator for structs with a <see cref="KeyAttribute"/> member (not for user code).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IKeyed
{
    long EventKey { get; }
}

/// <summary>
/// The gateway's session of a client closed (disconnect, kick). Delivered to every actor the session was bound to
/// (<c>Cluster.Bind&lt;TActor&gt;</c>) that handles it: <see cref="Key"/> is the bound key.
/// </summary>
[NinoType]
public readonly partial record struct SessionClosed(SessionRef Session, long Key) : IKeyed
{
    long IKeyed.EventKey => Key;
}

/// <summary>Wire identity of event type <typeparamref name="TEvent"/>: FNV-1a of its full name, computed once.</summary>
public static class EventType<TEvent> where TEvent : struct
{
    public static readonly uint Id = RpcIds.Fnv1a(typeof(TEvent).FullName ?? typeof(TEvent).Name);

    /// <summary>Actor handlers need a key: only keyed events reach them.</summary>
    public static readonly bool Keyed = typeof(IKeyed).IsAssignableFrom(typeof(TEvent));

    // A cast to IKeyed would box the struct; a constrained generic call bound once per event type does not.
    private static readonly KeyReader? ReadKey = Keyed
        ? (KeyReader)Delegate.CreateDelegate(typeof(KeyReader),
            typeof(EventType<TEvent>).GetMethod(nameof(KeyOfKeyed), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(TEvent)))
        : null;

    private delegate long KeyReader(in TEvent e);

    /// <summary>Key of a keyed event, 0 otherwise (no boxing).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long KeyOf(in TEvent e) => ReadKey == null ? 0 : ReadKey(in e);

    private static long KeyOfKeyed<TKeyed>(in TKeyed e) where TKeyed : struct, IKeyed => e.EventKey;
}

/// <summary>One event type handled by an implementation (generated registration).</summary>
public sealed class EventHandlerInfo(uint eventId, bool keyed, RpcDispatcher dispatcher, Type eventType)
{
    public uint EventId { get; } = eventId;
    public bool Keyed { get; } = keyed;
    public RpcDispatcher Dispatcher { get; } = dispatcher;
    public Type EventType { get; } = eventType;

    /// <summary>Handler registration for <typeparamref name="TEvent"/>; <paramref name="dispatcher"/> is generated.</summary>
    public static EventHandlerInfo Of<TEvent>(RpcDispatcher dispatcher) where TEvent : struct =>
        new(EventType<TEvent>.Id, EventType<TEvent>.Keyed, dispatcher, typeof(TEvent));

    public override string ToString() => EventType.FullName ?? EventType.Name;
}
