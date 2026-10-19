namespace DaServer.Runtime;

/// <summary>
/// Drains the shard's dispatch queue, which other threads post to: routes received frames (to service instances or actor
/// mailboxes), completes this shard's outgoing calls (asks of handlers become answers) and runs posted callbacks.
/// <see cref="MailSystem"/> then runs the mail that waited.
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<TimerSystem>]
internal sealed class DispatchSystem : EcsSystem
{
    /// <summary>Upper bound per wake, so timers and the tick stay on time under a flood.</summary>
    private const int Budget = 4096;

    protected override void OnUpdate()
    {
        var world = World;
        var shard = world.Shard;
        int processed = 0;
        while (processed < Budget && shard.TryTake(out var item))
        {
            processed++;
            try
            {
                switch (item.Kind)
                {
                    case WorkKind.Dispatch:
                        ShardDispatch.Route(world, (FrameWriter)item.A!, (IReplySink?)item.B);
                        break;
                    case WorkKind.Complete:
                        Complete((IPendingCall)item.A!, (FrameWriter)item.B!);
                        break;
                    case WorkKind.Callback:
                        if (item.A is System.Threading.SendOrPostCallback post) post(item.B);
                        else ((System.Action<object?>)item.A!)(item.B);
                        break;
                }
            }
            catch (System.Exception e)
            {
                Log.ShardFailed(world.Logger, e, shard.Index);
            }
        }

        world.GetSingleton<ShardActivity>().Processed += processed;
    }

    private static void Complete(IPendingCall call, FrameWriter frame)
    {
        try
        {
            call.Complete(FrameHeader.Read(frame.Frame).Status, frame.Payload);
        }
        finally
        {
            frame.Dispose();
        }
    }
}
