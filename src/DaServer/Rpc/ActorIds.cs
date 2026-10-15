using System.Runtime.CompilerServices;

namespace DaServer.Rpc;

/// <summary>
/// The id of one actor type, declared in contracts: <c>public readonly record struct MapId(long Value) : IActorId;</c>.
/// Messages to actors take one (<c>Cluster.Send(mapId, new Enter(..))</c>), so the target reads at the call site and a
/// player id cannot be passed where a map id is expected. Each actor type has its own id type
/// (<c>Map : Actor&lt;MapId, MapState&gt;</c>): the id type names the actor type on the wire and in placement.
/// An id type is a struct holding exactly one <c>long</c>; it can travel inside messages.
/// </summary>
public interface IActorId
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
