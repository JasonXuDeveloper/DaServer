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

    [Test]
    public async Task AFilterRefusesWithARetryHintThatReachesTheClient()
    {
        var gateway = await Start("gw", gateway: true, configure: static o => o.Features.Add(new LoginLimit()));
        var client = await Connect(gateway);
        var error = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new Login(new PlayerId(42))));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.RateLimited));
        Assert.That(error.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(2)));

        IIngress ingress = ((INodeContext)gateway).Ingress;
        var typed = Assert.ThrowsAsync<RpcException>(async () =>
            await ingress.Ask<Login, bool>(new Login(new PlayerId(42)), new Origin(gateway.Id, 1), TimeSpan.FromSeconds(1)));
        Assert.That((typed!.Status, typed.RetryAfter), Is.EqualTo((RpcStatus.RateLimited, TimeSpan.FromSeconds(2))));
    }

    [Test]
    public void TokenBucketsAllowBurstsThenTheRate()
    {
        var bucket = new TokenBucket(rate: 10, burst: 3);
        for (int i = 0; i < 3; i++) Assert.That(bucket.TryTake(out _), Is.True, "the burst");
        Assert.That(bucket.TryTake(out var retryAfter), Is.False);
        Assert.That(retryAfter, Is.InRange(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)));
        Thread.Sleep(150);
        Assert.That(bucket.TryTake(out _), Is.True, "refilled at the rate");

        var limit = new ConcurrencyLimit(2);
        Assert.That(limit.TryEnter() && limit.TryEnter(), Is.True);
        Assert.That(limit.TryEnter(), Is.False);
        limit.Exit();
        Assert.That(limit.TryEnter(), Is.True);
    }

    [Test]
    public void SignedTokensKeepTheirContentAndRefuseForgeries()
    {
        var secret = "cluster secret"u8.ToArray();
        var token = new byte[SignedToken.SizeOf(4)];
        "sale"u8.CopyTo(SignedToken.Content(token));
        SignedToken.Sign(token, secret, nowMs: 1000, lifetimeMs: 500);

        Assert.That(SignedToken.TryRead(token, secret, 1200, out var content) && content.SequenceEqual("sale"u8), Is.True);
        Assert.That(SignedToken.TryRead(token, secret, 1500, out _), Is.False, "expired");
        Assert.That(SignedToken.TryRead(token, "other"u8, 1200, out _), Is.False, "another cluster");
        token[17] ^= 1;
        Assert.That(SignedToken.TryRead(token, secret, 1200, out _), Is.False, "tampered");
    }

    /// <summary>Refuses every login for two seconds (an ingress filter of a feature).</summary>
    private sealed class LoginLimit : NodeFeature, IIngressFilter
    {
        protected internal override void Start(INodeContext node) => node.AddIngressFilter(this);

        public IngressDecision Check(uint messageId, uint target, long key, Origin origin) =>
            messageId == MessageType<Login>.Id ? IngressDecision.Refuse(RpcStatus.RateLimited, TimeSpan.FromSeconds(2)) : IngressDecision.Admit;
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
