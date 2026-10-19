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
    private static void Remove(Entity actor, in Removing removing, in ActorKey key, Maybe<HandoffWanted> wanted, Maybe<HandedTo> handed,
        Maybe<DynamicBuffer<Envelope>> mailbox, in MailScratch scratch, World world)
    {
        var status = removing.Status;
        var address = key.Address;
        // A node that asked for the record: "none" after any record it got, or where the record went (asking it there
        // instead of loading a second copy from the store beside the one running there).
        if (wanted.HasValue)
        {
            var by = wanted.Value.By;
            if (handed.HasValue && handed.Value.To != by) Handoff.Moved(world, address, by, handed.Value.To);
            else Handoff.Gone(world, address, by);
        }

        var mail = scratch.Items;
        if (mailbox.HasValue) // copied out: the entity's buffer goes with it
            foreach (var envelope in mailbox.Value.AsReadOnlySpan()) mail.Add(envelope);
        actor.Destroy(); // its session entities go with it (children); the refs above are invalid from here on
        foreach (var envelope in mail)
        {
            if (status == RpcStatus.Ok) ShardDispatch.Route(world, envelope.Frame, envelope.Sink); // may load the key again
            else ShardDispatch.Fail(envelope.Frame, envelope.Sink, status);
        }

        mail.Clear();
    }
}
