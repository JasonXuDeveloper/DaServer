namespace DaServer.Persistence;

// What a shard remembers of the handoffs it took, passed on or sent (ADR-072): by handoff id, so a resend is answered
// as its first send was; by key, so an asker is sent where the record went. Both for a bounded time, oldest out first.
internal static partial class Handoff
{
    /// <summary>The handoff a record belongs to (its first send's id, kept by resends).</summary>
    private static ulong IdOf(ReadOnlySpan<byte> payload) => BinaryPrimitives.ReadUInt64LittleEndian(payload[(2 * sizeof(ulong) + sizeof(long))..]);

    /// <summary>Remember for the move window that the record of a key went to <paramref name="to"/>.</summary>
    private static void RememberHanded(World world, uint handlerTypeId, long key, ulong to)
    {
        ref var seen = ref world.GetSingleton<HandoffsSeen>();
        long now = world.GetSingleton<ShardActivity>().NowMs;
        while (seen.HandedOrder.TryPeek(out var oldest) && (oldest.UntilMs < now || seen.HandedOrder.Count >= HandoffsSeen.Max))
        {
            seen.HandedOrder.Dequeue();
            if (seen.Handed.TryGetValue(oldest.Address, out var entry) && entry.UntilMs == oldest.UntilMs) seen.Handed.Remove(oldest.Address);
        }

        long until = now + MoveWindowMs(world.Shard.Node.Options);
        var address = new ActorAddress(handlerTypeId, key);
        seen.Handed[address] = new SeenHandoff(to, until);
        seen.HandedOrder.Enqueue((address, until));
    }

    /// <summary>The key is here again (restored, or loaded from the store): forget where it was handed.</summary>
    public static void ForgetHanded(World world, uint handlerTypeId, long key) =>
        world.GetSingleton<HandoffsSeen>().Handed.Remove(new ActorAddress(handlerTypeId, key));

    /// <summary>Where this shard handed the key's record within the move window, or null.</summary>
    private static ulong? HandedRecordTo(World world, uint handlerTypeId, long key) =>
        world.GetSingleton<HandoffsSeen>().Handed.TryGetValue(new ActorAddress(handlerTypeId, key), out var entry) &&
        entry.UntilMs >= world.GetSingleton<ShardActivity>().NowMs ? entry.PassedTo : null;

    /// <summary>Remember a handoff taken here (<paramref name="passedTo"/> 0) or passed on, for the resends' window.</summary>
    private static void Seen(World world, ulong id, ulong passedTo)
    {
        ref var seen = ref world.GetSingleton<HandoffsSeen>();
        long now = world.GetSingleton<ShardActivity>().NowMs;
        while (seen.Order.TryPeek(out var oldest) && (oldest.UntilMs < now || seen.Order.Count >= HandoffsSeen.Max))
        {
            seen.Order.Dequeue();
            seen.ById.Remove(oldest.Id);
            if (oldest.UntilMs >= now) seen.ForgotUntilMs = oldest.UntilMs; // forgotten early: "not seen" proves less until then
        }

        long until = now + 2 * TimeoutMs(world.Shard.Node);
        if (seen.ById.TryAdd(id, new SeenHandoff(passedTo, until))) seen.Order.Enqueue((id, until));
    }
}
