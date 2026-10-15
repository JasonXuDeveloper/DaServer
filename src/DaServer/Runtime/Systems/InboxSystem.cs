using DaServer.Buffers;
using DaServer.Protocol;
using DaServer.Rpc;
using EasyEcs;
using Microsoft.Extensions.Logging;

namespace DaServer.Runtime;

/// <summary>
/// Drains the shard's cross-thread inbox: routes received frames (to service instances or actor mailboxes), completes
/// this shard's outgoing calls, runs posted callbacks (await continuations) and tags ended turns (<see cref="TurnEnded"/>).
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed class InboxSystem : EcsSystem
{
    /// <summary>Upper bound per wake, so timers and the tick stay on time under a flood.</summary>
    private const int Budget = 4096;

    public override int Priority => 0;

    protected override void OnUpdate()
    {
        var world = World;
        var shard = world.Shard();
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
                    case WorkKind.TurnEnded:
                        ((TurnWatcher)item.A!).Ended(world);
                        break;
                }
            }
            catch (System.Exception e)
            {
                world.Logger().LogError(e, "Unhandled exception in shard {Shard}", shard.Index);
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
