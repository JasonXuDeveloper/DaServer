namespace DaServer.Runtime;

/// <summary>
/// Removes actors marked <see cref="Removing"/>: destroys their entity (the index forgets it) and routes the mail left in
/// their mailbox again (now to the new owner, or loads the actor here again) or fails it.
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<LoadSystem>]
internal sealed class RemovalSystem : EcsSystem
{
    [ForEach]
    private static void Remove(Entity actor, in Removing removing, in ActorKey key, World world)
    {
        var status = removing.Status;
        var address = key.Address;
        // A node that asked for the record: "none" after any record it got, or where the record went (asking it there
        // instead of loading a second copy from the store beside the one running there).
        if (actor.TryGet<HandoffWanted>(out var wanted))
        {
            if (actor.TryGet<HandedTo>(out var handed) && handed.To != wanted.By) Handoff.Moved(world, address, wanted.By, handed.To);
            else Handoff.Gone(world, address, wanted.By);
        }

        if (actor.TryGet<ClientSessions>(out var sessions)) world.GetSingleton<SessionPool>().Return(in sessions);
        var mail = world.GetSingleton<MailScratch>().Items;
        if (actor.Has<DynamicBuffer<Envelope>>()) // copied out: the entity's buffer goes with it
            foreach (var envelope in actor.Read<DynamicBuffer<Envelope>>().AsReadOnlySpan()) mail.Add(envelope);
        actor.Destroy(); // the refs above are invalid from here on
        foreach (var envelope in mail)
        {
            if (status == RpcStatus.Ok) ShardDispatch.Route(world, envelope.Frame, envelope.Sink); // may load the key again
            else ShardDispatch.Fail(envelope.Frame, envelope.Sink, status);
        }

        mail.Clear();
    }
}
