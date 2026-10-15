namespace DaServer.Tests;

/// <summary>Messages from outside the cluster (<see cref="IIngress"/>): origin, raw pass-through, early refusal.</summary>
public class IngressTests : ClusterTest
{
    [Test]
    public async Task TheOriginReachesTheHandlerAsCaller()
    {
        var edge = await Start("edge", hosts: static _ => false);
        await Start("services");
        await Converged();
        IIngress ingress = ((INodeContext)edge).Ingress;

        long caller = await ingress.Ask<WhoAsks, long>(new WhoAsks(), new Origin(edge.Id, 42), TimeSpan.FromSeconds(5));
        Assert.That(caller, Is.EqualTo(42));
        Assert.That(await edge.Ask(new WhoAsks()), Is.Zero, "a message sent inside the cluster has no origin");
    }

    [Test]
    public async Task RawRequestsPassThroughAndReplyToTheIngressSink()
    {
        var edge = await Start("edge", hosts: static _ => false);
        var owner = await Start("owner");
        await Converged();
        IIngress ingress = ((INodeContext)edge).Ingress;

        var payload = Nino.Core.NinoSerializer.Serialize(new Add(5));
        var sink = new CapturingSink();
        long key = KeyOwnedBy<CounterId>(owner);
        ingress.Ask(MessageType<Add>.Id, ActorIds<CounterId>.HandlerTypeId, key, payload, new Origin(edge.Id, 1), sink, 77, TimeSpan.FromSeconds(5));
        var (callId, status, reply) = await sink.Reply.Task;
        Assert.That((callId, status), Is.EqualTo((77u, RpcStatus.Ok)));
        var reader = new Nino.Core.Reader(reply);
        Nino.Core.NinoDeserializer.Deserialize(out int value, ref reader);
        Assert.That(value, Is.EqualTo(5));
    }

    [Test]
    public async Task RequestsToAnOverloadedNodeAreRefusedBeforeTheyAreSent()
    {
        var edge = await Start("edge", hosts: static _ => false, configure: static o => o.IngressLoadLimit = 8);
        var owner = await Start("owner", shards: 1);
        await Converged();
        using var gate = new ManualResetEventSlim();
        owner.Shards[0].Post(static g => ((ManualResetEventSlim)g!).Wait(), gate);
        for (int i = 0; i < 16; i++) owner.Send(new CounterId(1), new Bump()); // queued behind the blocked shard
        await Eventually(() => edge.View.TryGetMember(owner.Id, out var m) && m.Load >= 8, "the owner's load was not gossiped");

        IIngress ingress = ((INodeContext)edge).Ingress;
        var refused = ingress.Ask<CounterId, Add, int>(new CounterId(1), new Add(1), new Origin(edge.Id, 1), TimeSpan.FromSeconds(5));
        Assert.That(refused.IsCompleted, Is.True, "refused at once, nothing sent");
        var error = Assert.ThrowsAsync<RpcException>(async () => await refused);
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Overloaded));
        gate.Set();
    }

    private sealed class CapturingSink : IReplySink
    {
        public readonly TaskCompletionSource<(uint, RpcStatus, byte[])> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SendReply(uint callId, RpcStatus status, FrameWriter reply)
        {
            Reply.TrySetResult((callId, status, reply.Payload.ToArray()));
            reply.Dispose();
        }
    }
}
