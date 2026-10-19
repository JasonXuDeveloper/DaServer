using System.Buffers.Binary;
namespace DaServer.Tests;

/// <summary>Failure detection under pauses (C1) and keep-majority downing on a split (C2, ADR-064).</summary>
public class MembershipTests : ClusterTest
{
    [Test]
    public async Task APauseOfThisProcessDeclaresNobodyDead()
    {
        var nodes = await StartCluster(3);
        // No gossip reaches a for a moment (as during its pause), so only a's own clock can make the verdict.
        int Port(Node n) => n.ClusterEndPoint.Port;
        nodes[0].Membership.BlockedPorts = [Port(nodes[1]), Port(nodes[2])];
        nodes[1].Membership.BlockedPorts = nodes[2].Membership.BlockedPorts = [Port(nodes[0])];
        nodes[0].SimulatePause(TimeSpan.FromSeconds(5)); // longer than DeadAfter (1.5 s)
        await Task.Delay(300); // a few rounds
        Assert.That(nodes[0].View.UpMembers, Has.Length.EqualTo(3), "the silence was this node's pause, not the others'");
        foreach (var node in nodes) node.Membership.BlockedPorts = null;
    }

    [Test]
    public async Task ANodeLateOnEveryTickStillDeclaresTheDeadDead()
    {
        Faults.Allow("MinoritySide"); // c, cut off, downs itself
        // a and b are starved for good: each maintenance tick runs 1.35 s late (DeadAfter is 1.5 s; a later one fences the
        // node itself). c goes silent. A pause is excused, but a process late on every tick must still age the others, or
        // c would keep its keys forever.
        var nodes = await StartCluster(3);
        int Port(Node n) => n.ClusterEndPoint.Port;
        nodes[2].Membership.BlockedPorts = [Port(nodes[0]), Port(nodes[1])];
        nodes[0].Membership.BlockedPorts = nodes[1].Membership.BlockedPorts = [Port(nodes[2])];
        var late = Task.Run(async () =>
        {
            while (nodes[0].View.UpMembers.Length == 3)
            {
                nodes[0].SimulatePause(TimeSpan.FromSeconds(1.35), aged: false); // the tick sees a pause the clocks did not
                nodes[1].SimulatePause(TimeSpan.FromSeconds(1.35), aged: false);
                await Task.Delay(100);
            }
        });
        await Eventually(() => nodes[0].View.TryGetMember(nodes[2].Id, out var c) && c.Status == MemberStatus.Dead, "c declared dead", 15_000);
        await late;
        foreach (var node in nodes) node.Membership.BlockedPorts = null;
    }

    [Test]
    public async Task SeedsResolveThroughTheResolverOffTheGossipRound()
    {
        // A resolver that takes 2 s (a slow DNS server, a registry): the node joins once it answers, and its rounds run
        // on time meanwhile instead of waiting for it.
        var a = await Start("a");
        var resolver = new SlowResolver(a.ClusterEndPoint.Port);
        var b = await Start("b", configure: o => (o.Seeds, o.SeedResolver) = (["peers-of-a"], resolver));
        await Converged();
        Assert.That(resolver.Asked, Is.GreaterThanOrEqualTo(1));
        Assert.That(Faults.Count("RoundLate"), Is.Zero, "no round waited for the resolver");
        Assert.That(b.View.UpMembers, Has.Length.EqualTo(2));
    }

    [Test]
    public async Task AResolverThatNeverAnswersDoesNotStopTheRefreshes()
    {
        // The first resolution hangs and ignores its token (a registry that is down): it is given up after the refresh
        // period, the next one answers, and the node joins.
        var a = await Start("a");
        var resolver = new HangingOnceResolver(a.ClusterEndPoint.Port);
        var b = await Start("b", configure: o => (o.Seeds, o.SeedResolver) = (["peers-of-a"], resolver));
        await Eventually(() => b.View.UpMembers.Length == 2, "b joined through the second resolution", 20_000);
        Assert.That(resolver.Asked, Is.GreaterThanOrEqualTo(2));
    }

    private sealed class HangingOnceResolver(int port) : ISeedResolver
    {
        public int Asked;

        public async ValueTask<IReadOnlyList<System.Net.DnsEndPoint>> ResolveAsync(string seed, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Asked) == 1) await new TaskCompletionSource().Task; // never, whatever the token
            return [new System.Net.DnsEndPoint("127.0.0.1", port)];
        }
    }

    private sealed class SlowResolver(int port) : ISeedResolver
    {
        public int Asked;

        public async ValueTask<IReadOnlyList<System.Net.DnsEndPoint>> ResolveAsync(string seed, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Asked);
            await Task.Delay(2000, cancellationToken);
            return seed == "peers-of-a" ? [new System.Net.DnsEndPoint("127.0.0.1", port)] : [];
        }
    }

    [Test]
    public async Task AFlappingLinkMakesAMemberSuspectNeverDead()
    {
        // c's links to a and b drop for 0.9 s at a time (SuspectAfter 0.6 s, DeadAfter 1.5 s), five times: c may be
        // suspected, refutes each time the network comes back, is never declared dead, and keeps its keys.
        var nodes = await StartCluster(3);
        var (a, c) = (nodes[0], nodes[2]);
        long key = a.KeyOwnedBy<CounterId>(c, 9400);
        Assert.That(await a.Ask(new CounterId(key), new Add(1)), Is.EqualTo(1));
        int Port(Node n) => n.ClusterEndPoint.Port;
        bool suspected = false, dead = false;
        for (int round = 0; round < 5; round++)
        {
            c.Membership.BlockedPorts = [Port(nodes[0]), Port(nodes[1])];
            nodes[0].Membership.BlockedPorts = nodes[1].Membership.BlockedPorts = [Port(c)];
            for (int i = 0; i < 9; i++)
            {
                await Task.Delay(100);
                if (a.View.TryGetMember(c.Id, out var seen)) (suspected, dead) = (suspected || seen.Status == MemberStatus.Suspect, dead || seen.Status == MemberStatus.Dead);
            }

            foreach (var node in nodes) node.Membership.BlockedPorts = null;
            await Task.Delay(700);
        }

        Assert.That(dead, Is.False, "never declared dead");
        Assert.That(suspected, Is.True, "the silences were long enough to suspect it");
        await Converged();
        Assert.That(a.OwnerOf<CounterId>(key), Is.EqualTo(c.Id), "it kept its keys");
        Assert.That(await a.Ask(new CounterId(key), new Add(1)), Is.EqualTo(2), "and its actor, state and all");
    }

    [Test]
    public async Task AStalePulseDoesNotLowerAHeartbeat()
    {
        // A pulse saying "suspect" at an older heartbeat supersedes "up" (a worse status), but the heartbeat stays the
        // newest seen: a lower one would make every later stale pulse look like progress of a member that may be dead.
        var (a, b) = (await StartCluster(2)) is var n ? (n[0], n[1]) : default;
        int Port(Node node) => node.ClusterEndPoint.Port;
        a.Membership.BlockedPorts = [Port(b)];
        b.Membership.BlockedPorts = [Port(a)];
        Assert.That(a.View.TryGetMember(b.Id, out var seen), Is.True);
        long heartbeat = seen.Heartbeat;
        Span<byte> pulse = stackalloc byte[MemberPulse.Size];
        new MemberPulse(b.Id, seen.Incarnation, heartbeat - 5, MemberStatus.Suspect, 0, 0, 0, 0).Write(pulse);
        a.Membership.MergePulses(pulse);
        Assert.That(a.View.TryGetMember(b.Id, out var suspect) && suspect.Status == MemberStatus.Suspect, Is.True);
        Assert.That(suspect.Heartbeat, Is.GreaterThanOrEqualTo(heartbeat), "the newest heartbeat is kept");
        foreach (var node in new[] { a, b }) node.Membership.BlockedPorts = null;
    }

    [Test]
    public async Task ANodeThatLeftIsForgottenEverywhere()
    {
        // b leaves gracefully: it is never declared dead, only forgotten. a must not keep the link b introduced itself
        // on (one entry per node that ever joined, for good).
        var a = await Start("a");
        var b = await Start("b");
        await Converged();
        await Stop(b);
        await Eventually(() => !a.View.TryGetMember(b.Id, out _), "a forgot b", 20_000);
        Assert.That(a.Introduced(b.Id), Is.False);
    }

    [Test]
    public async Task ASeedSpelledOtherwiseIsOneLink()
    {
        // b's seed names a as "localhost", a's card says 127.0.0.1: two links to a would each say hello on a's one
        // inbound slot for b, and every reconnect of either would close the other's connection and reset its stream.
        var a = await Start("a");
        var b = await Start("b", configure: o => o.Seeds = ["localhost:" + a.ClusterEndPoint.Port]);
        await Converged();
        Assert.That(await b.Ask(new CounterId(KeyOwnedBy<CounterId>(a)), new Add(1)), Is.EqualTo(1));
        Assert.That(b.LinksTo(a.Id), Is.EqualTo(1));
    }

    [Test]
    public async Task TheMinoritySideOfASplitDownsItselfAndTheMajorityKeepsRunning()
    {
        Faults.Allow("MinoritySide"); // what the test provokes
        var nodes = await StartCluster(3);
        var downed = nodes.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (int i = 0; i < nodes.Length; i++)
        {
            int index = i;
            nodes[i].Downed += () => downed[index].TrySetResult();
        }

        // {a} | {b, c}: gossip between the sides is dropped.
        int Port(Node n) => n.ClusterEndPoint.Port;
        nodes[0].Membership.BlockedPorts = [Port(nodes[1]), Port(nodes[2])];
        nodes[1].Membership.BlockedPorts = [Port(nodes[0])];
        nodes[2].Membership.BlockedPorts = [Port(nodes[0])];

        await downed[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually(() => nodes[1].View.UpMembers.Length == 2 && nodes[2].View.UpMembers.Length == 2, "the majority saw the minority die");
        await Task.Delay(500);
        Assert.That(downed[1].Task.IsCompleted || downed[2].Task.IsCompleted, Is.False, "the majority side keeps running");
        Assert.That(nodes[0].IsReady, Is.False);
        Assert.That(await nodes[1].Ask(new CounterId(1), new Add(1)), Is.EqualTo(1), "the majority serves every key");
    }

    [Test]
    public async Task BothNodesOfATwoNodeMinorityDownPromptly()
    {
        // {a, b} | {c, d, e} at once. Whichever of a and b declares c, d, e dead first gossips it to the other, which
        // must count the sides on that news too (its own detector then skips members already dead): both down within
        // about DeadAfter of the cut, not one of them only after the other went silent.
        Faults.Allow("MinoritySide");
        var nodes = await StartCluster(5, shards: 1);
        var downedAt = nodes.Select(_ => new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (int i = 0; i < nodes.Length; i++)
        {
            int index = i;
            nodes[i].Downed += () => downedAt[index].TrySetResult(Environment.TickCount64);
        }

        int Port(Node n) => n.ClusterEndPoint.Port;
        long cut = Environment.TickCount64;
        nodes[0].Membership.BlockedPorts = nodes[1].Membership.BlockedPorts = [Port(nodes[2]), Port(nodes[3]), Port(nodes[4])];
        foreach (var n in nodes[2..]) n.Membership.BlockedPorts = [Port(nodes[0]), Port(nodes[1])];

        double deadAfter = nodes[0].Options.DeadAfter.TotalMilliseconds;
        foreach (int i in new[] { 0, 1 })
            Assert.That(await downedAt[i].Task.WaitAsync(TimeSpan.FromSeconds(10)) - cut, Is.LessThan(deadAfter * 1.6),
                $"{nodes[i].Options.Name} downed on its own count or on the other's news");
    }

    [Test]
    public async Task ASurvivorOfATwoNodeClusterKeepsRunning()
    {
        var nodes = await StartCluster(2);
        bool downed = false;
        foreach (var node in nodes) node.Downed += () => downed = true;
        await nodes[1].KillAsync(); // two members: a crash and a split look the same, nobody downs itself
        await Eventually(() => !nodes[0].View.TryGetMember(nodes[1].Id, out var b) || b.Status == MemberStatus.Dead,
            "the crash was detected (a suspect member still owns its keys)");
        await Task.Delay(500);
        Assert.That(downed, Is.False);
        Assert.That(await nodes[0].Ask(new CounterId(2), new Add(1)), Is.EqualTo(1));
    }

    [Test]
    public async Task AForgottenMemberDoesNotComeBack()
    {
        // x died and was forgotten here; a node that forgets it later still gossips about it: neither its Dead nor an
        // older Up card may bring it back (it would stay for good and count as lost for keep-majority).
        var a = await Start("a");
        static byte[] Cards(MemberStatus status) => Nino.Core.NinoSerializer.Serialize(new[]
        {
            new ClusterMember { NodeId = 4242, Name = "x", Host = "127.0.0.1", Port = 1, Status = status, Incarnation = 1, UpSince = 1 },
        });
        a.Membership.OnGossip(null!, ControlOp.Cards, Cards(MemberStatus.Up));
        Assert.That(a.View.TryGetMember(4242, out _), Is.True);

        long dead = (long)a.Options.DeadAfter.TotalMilliseconds;
        a.Membership.Age(2 * dead);
        a.Membership.RoundNow(); // dead
        a.Membership.Age(4 * dead);
        a.Membership.RoundNow(); // forgotten
        Assert.That(a.View.TryGetMember(4242, out _), Is.False, "forgotten");

        a.Membership.OnGossip(null!, ControlOp.Cards, Cards(MemberStatus.Dead));
        a.Membership.OnGossip(null!, ControlOp.Cards, Cards(MemberStatus.Up));
        Assert.That(a.View.TryGetMember(4242, out _), Is.False, "not brought back by later gossip");
    }

    /// <summary>Two nodes on one store; a key a owns, deposited 5 on a (written only when a unloads it gracefully).</summary>
    private async Task<(Node A, Node B, BankId Key)> Deposited()
    {
        var store = new DaServer.Persistence.InMemoryActorStore();
        var a = await Start("a", configure: o => o.ActorStore = store);
        var b = await Start("b", configure: o => o.ActorStore = store);
        await Converged();
        long key = 100;
        while (a.OwnerOf<BankId>(key) != a.Id) key++;
        Assert.That(await a.Ask(new BankId(key), new Deposit(5, false)), Is.EqualTo(5));
        return (a, b, new BankId(key));
    }

    [Test]
    public async Task APauseLongerThanDeadAfterFencesThisNode()
    {
        // The others declared a dead during its pause and own its keys now: a's actors go without a write, and a comes
        // back at a new incarnation (no Dead pulse may ever reach it: it may be forgotten already).
        var (a, _, key) = await Deposited();
        int incarnation = a.Membership.Self.Incarnation;
        a.Membership.Tick(DaServer.Concurrency.Platform.Milliseconds, pausedMs: 3 * (long)a.Options.DeadAfter.TotalMilliseconds);
        await Eventually(() => a.ActorCount == 0, "a's actors went");
        Assert.That(a.Membership.Self.Incarnation, Is.GreaterThan(incarnation));
        var c = await Start("c", configure: o => o.ActorStore = a.Options.ActorStore);
        Assert.That(await c.Ask(key, new Balance()), Is.Zero, "nothing written: the new owner's state is not overwritten");
    }

}

/// <summary>Node authentication (C3, ADR-065): only nodes of the same cluster holding its secret join.</summary>
public class LinkAuthTests : ClusterTest
{
    private static Action<NodeOptions> With(string? secret, string cluster = "daserver") => o =>
    {
        o.SigningSecret = secret;
        o.ClusterName = cluster;
    };

    [Test]
    public async Task NodesWithTheSecretJoinOthersAreRefused()
    {
        Faults.Allow("LinkRefused"); // what the test provokes
        var a = await Start("a", configure: With("s3cret"));
        var b = await Start("b", configure: With("s3cret"));
        await Converged();

        var intruder = await Start("intruder", configure: With("guess"));
        var stranger = await Start("stranger", configure: With("s3cret", cluster: "other"));
        await Task.Delay(1500); // several gossip rounds against the seed
        Assert.That(a.View.UpMembers.Select(static m => m.Name), Is.EquivalentTo(new[] { "a", "b" }),
            "a wrong secret and another cluster's name cannot join");
        Assert.That(intruder.View.UpMembers, Has.Length.EqualTo(1));
        Assert.That(stranger.View.UpMembers, Has.Length.EqualTo(1));
        Assert.That(Faults.Count("LinkRefused"), Is.LessThan(100), "a refused node backs off instead of reconnecting in a loop");
    }

    [Test]
    public void ProtocolLevelsMeetWhenTheirRangesOverlap()
    {
        var levels = new ProtocolLevels(3, 4);
        Assert.That(ProtocolLevels.Of(levels.Packed), Is.EqualTo(levels), "one header key carries both ends");
        Assert.That(levels.Overlaps(new ProtocolLevels(4, 5)), Is.True, "a bridge release meets the next one");
        Assert.That(levels.Overlaps(new ProtocolLevels(2, 2)), Is.False);
        Assert.That(levels.Overlaps(new ProtocolLevels(5, 6)), Is.False);
        Assert.That((levels.ToString(), new ProtocolLevels(3, 3).ToString()), Is.EqualTo(("3-4", "3")));
    }

    [Test]
    public async Task ANodeOfAnotherLinkProtocolIsRefusedBeforeItsHelloIsRead()
    {
        // Its levels come as raw bytes ahead of the Nino body: a build that writes another encoding is named, not misread.
        Faults.Allow("LinkRefused"); // what the test provokes
        var a = await Start("a");
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync("127.0.0.1", a.Membership.Self.Port);
        var w = FrameWriter.Rent();
        var older = new ProtocolLevels(ControlOp.Link.Min - 1, ControlOp.Link.Min - 1);
        BinaryPrimitives.WriteInt64LittleEndian(w.GetSpan(sizeof(long)), older.Packed);
        w.Advance(sizeof(long));
        w.WriteRaw([0xFF, 0xFF, 0xFF, 0x7F]); // a body this build cannot read
        w.SetHeader(FrameKind.Control, 0, ControlOp.Hello, 99);
        await socket.GetStream().WriteAsync(w.Frame.ToArray());
        w.Dispose();
        await Eventually(() => Faults.Count("LinkRefused") == 1, "refused for its protocol");
    }

    [Test]
    public void ASecretRotatesInThreeStepsEachAcceptingTheNext()
    {
        // SigningSecret, AcceptedSigningSecret per step: old; old+new; new+old; new. Nodes one step apart link both ways.
        LinkAuth old = new("c", "one"), first = new("c", "one", "two"), second = new("c", "two", "one"), done = new("c", "two");
        static bool Links(LinkAuth a, LinkAuth b)
        {
            var challenge = LinkAuth.NewChallenge();
            return b.Verify(7, 1, "h", 1, challenge, a.Prove(7, 1, "h", 1, challenge)) == null &&
                   a.Verify(8, 1, "h", 1, challenge, b.Prove(8, 1, "h", 1, challenge)) == null;
        }

        Assert.That((Links(old, first), Links(first, second), Links(second, done)), Is.EqualTo((true, true, true)), "a step apart");
        Assert.That(Links(old, second), Is.False, "two steps apart: every node takes the first step before any takes the second");
        Assert.That(Links(old, done), Is.False);
    }

    [Test]
    public async Task NodesOneRotationStepApartFormOneCluster()
    {
        // A rotation's first step on a and c, its second on b: every pair links, in both directions.
        static Action<NodeOptions> Step(string sign, string accept) => o => (o.SigningSecret, o.AcceptedSigningSecret) = (sign, accept);
        await Start("a", configure: Step("one", "two"));
        await Start("b", configure: Step("two", "one"));
        await Start("c", configure: Step("one", "two"));
        await Converged();
    }

    [Test]
    public void AHelloProvesItsChallengeEndpointStreamAndCluster()
    {
        // The proof answers one connection's challenge: a recorded Hello is good on no other connection, at no other
        // node, and cannot name another endpoint for the replies.
        var auth = new LinkAuth("c", "s3cret");
        var challenge = LinkAuth.NewChallenge();
        var proof = auth.Prove(7, 1, "10.0.0.1", 5000, challenge);
        Assert.That(auth.Verify(7, 1, "10.0.0.1", 5000, challenge, proof), Is.Null);
        Assert.That(auth.Verify(7, 1, "10.0.0.1", 5000, LinkAuth.NewChallenge(), proof), Is.Not.Null, "another connection's challenge");
        Assert.That(auth.Verify(7, 1, "10.0.0.66", 5000, challenge, proof), Is.Not.Null, "another host for the replies");
        Assert.That(auth.Verify(7, 1, "10.0.0.1", 5001, challenge, proof), Is.Not.Null, "another port");
        Assert.That(auth.Verify(7, 2, "10.0.0.1", 5000, challenge, proof), Is.Not.Null, "another stream");
        Assert.That(auth.Verify(8, 1, "10.0.0.1", 5000, challenge, proof), Is.Not.Null, "another node id");
        Assert.That(auth.Verify(7, 1, "10.0.0.1", 5000, default, proof), Does.Contain("challenge"), "a Hello before any challenge");
        Assert.That(new LinkAuth("other", "s3cret").Verify(7, 1, "10.0.0.1", 5000, challenge, proof), Is.Not.Null, "another cluster");
    }

    /// <summary>A feature whose pulse source throws (user code read on every gossip round).</summary>
    private sealed class BadPulse : NodeFeature
    {
        protected internal override void Start(INodeContext node) => node.AddPulse(7, static () => throw new InvalidOperationException("a pulse source threw"));
    }

    [Test]
    public async Task ANodeWhoseFeaturePulseThrowsStillDetectsACrash()
    {
        Faults.Allow("FeatureFailed"); // what the test provokes
        var a = await Start("a", configure: static o => o.Features.Add(new BadPulse()));
        var b = await Start("b");
        await Eventually(() => a.View.UpMembers.Length == 2 && b.View.UpMembers.Length == 2, "both joined");
        await b.KillAsync();
        await Eventually(() => a.View.TryGetMember(b.Id, out var m) && m.Status == MemberStatus.Dead, "a's failure detection still runs", 15_000);
    }
}
