namespace DaServer.Runtime;

/// <summary>
/// The identity of an actor's entity: which actor type (by its id type) and which key. Indexed, so the shard finds a
/// loaded actor by its address (<c>world.ActorKeyIndex</c>), and systems read an actor's id and client group from it
/// (<c>[ForEach] static void Send(ref MapGrid grid, in ActorKey self, World world) => .. self.Id&lt;MapId&gt;() ..</c>).
/// </summary>
public readonly record struct ActorKey(uint HandlerTypeId, long Key) : IComponent, IIndexed<ActorKey>
{
    ActorKey IIndexed<ActorKey>.Key => this;

    /// <summary>The actor's id, of its id type <typeparamref name="TId"/>.</summary>
    public TId Id<TId>() where TId : struct, IActorId => IdOf<TId>(new ActorAddress(HandlerTypeId, Key));

    /// <summary>The actor's client group: the sessions it bound or granted.</summary>
    public ClientGroup Group => ClientGroup.Of(HandlerTypeId, Key);

    internal ActorAddress Address => new(HandlerTypeId, Key);

    internal static ActorKey Of(ActorAddress address) => new(address.HandlerTypeId, address.Key);

    internal static TId IdOf<TId>(ActorAddress address) where TId : struct, IActorId =>
        address.HandlerTypeId == ActorIds<TId>.HandlerTypeId ? ActorIds<TId>.Of(address.Key) : throw WrongIdType(typeof(TId), address);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException WrongIdType(Type type, ActorAddress address) =>
        new($"The actor of handler type 0x{address.HandlerTypeId:x8} is not addressed by {type}");
}
