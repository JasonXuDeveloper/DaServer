using System.Runtime.CompilerServices;

namespace DaServer;

/// <summary>
/// The id of one actor type, declared in contracts: <c>public readonly record struct MapId(long Value) : IActorId;</c>.
/// Messages to actors take one (<c>Cluster.Send(mapId, new Enter(..))</c>), so the target reads at the call site and a
/// player id cannot be passed where a map id is expected. Each actor type has its own id type
/// (<c>Map : Actor&lt;MapId, MapState&gt;</c>): the id type names the actor type on the wire and in placement.
/// An id type is a struct holding exactly one <c>long</c>; it can travel inside messages.
/// </summary>
public interface IActorId : IAddress
{
    /// <summary>The number of the actor among those of its type (player id, map instance id, ...).</summary>
    long Value { get; }
}

/// <summary>Typed view of an actor id type (one generic instance per id type, read on send paths).</summary>
internal static class ActorIds<TId> where TId : struct, IActorId
{
    /// <summary>The actor type addressed by <typeparamref name="TId"/> on the wire and in placement.</summary>
    public static readonly uint HandlerTypeId = Check();

    /// <summary>The id of value <paramref name="value"/>.</summary>
    // Invariant (checked once by Check): TId is exactly one long, so reinterpreting the value is the constructor.
    public static TId Of(long value) => Unsafe.As<long, TId>(ref value);

    private static uint Check()
    {
        if (Unsafe.SizeOf<TId>() != sizeof(long))
            throw new InvalidOperationException($"Actor id type {typeof(TId)} must hold exactly one long");
        return TypeId<TId>.Value;
    }
}

/// <summary>
/// An actor id type placed by <see cref="Placement"/> instead of its value (ADR-073): actors whose ids have the same
/// placement, of any type, live on the same node and shard, so their messages to each other stay local. E.g. the
/// seats of a room next to the room: <c>public readonly record struct SeatId(long Value) : IActorId, IPlaced { public
/// long Placement => Value >> 16; }</c> with <c>RoomId</c> placed by its value. A pure function of <c>Value</c>:
/// every node computes it from the id alone.
/// </summary>
public interface IPlaced
{
    /// <summary>The key this actor is placed by (hashed onto nodes and shards in place of its value).</summary>
    long Placement { get; }
}

/// <summary>The placement of the <see cref="IPlaced"/> id types the process references (registered by generated code).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class Placements
{
    private static readonly object Gate = new();
    private static Dictionary<uint, Func<long, long>> _byType = new(); // copied on write: read without locks

    public static void Register<TId>() where TId : struct, IActorId, IPlaced
    {
        lock (Gate)
        {
            var next = new Dictionary<uint, Func<long, long>>(_byType) { [ActorIds<TId>.HandlerTypeId] = static key => ActorIds<TId>.Of(key).Placement };
            Volatile.Write(ref _byType, next);
        }
    }

    /// <summary>
    /// What actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>) is hashed by: its type and key, or, for a
    /// placed id type, group 0 and its placement (so actors of different types meet).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Of(uint handlerTypeId, ref long key)
    {
        var byType = Volatile.Read(ref _byType);
        if (byType.Count == 0 || !byType.TryGetValue(handlerTypeId, out var place)) return handlerTypeId;
        key = place(key);
        return 0;
    }
}

