using System.Text;

namespace DaServer.Tests;

/// <summary>Tickets presented twice, late, after a kick or forged: a ticket resumes one session once, within the grace.</summary>
public partial class ResumeTests
{
    /// <summary>A second client presents <paramref name="ticket"/>: the status it gets (Ok: resumed).</summary>
    private async Task<RpcStatus> ResumeAs(Node[] gateways, byte[] ticket) => (await ResumeWith(gateways, ticket)).Status;

    private async Task<(RpcStatus Status, ClusterClient Client)> ResumeWith(Node[] gateways, byte[] ticket, int gateway = 1)
    {
        var client = Track(new ClusterClient { AutoReconnect = false });
        await client.ConnectAsync("127.0.0.1", gateways[gateway].ClientPort());
        try
        {
            await client.ResumeWith(ticket);
            return (RpcStatus.Ok, client);
        }
        catch (RpcException e)
        {
            return (e.Status, client);
        }
    }

    [Test]
    public async Task AKickIsNotEvadedByAResumeWaitingBehindIt()
    {
        // The player kicks a session in a turn while a resume of that session waits in its mailbox (a move): the resume
        // is refused and the player learns the session closed, instead of the kicked client carrying on.
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 595);
        var ticket = client.Ticket!;
        TurnGate.Current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kick = client.Ask(new KickAfterGate()).AsTask();
        await Task.Delay(200);
        var resume = ResumeWith(gateways, ticket);
        await Task.Delay(300); // the resume waits behind the held turn
        TurnGate.Current.TrySetResult();

        Assert.That((await resume).Status, Is.EqualTo(RpcStatus.Forbidden), "the kicked session resumed");
        await Eventually(() => Observed.Events.Contains("closed:595"), "the player learns the kicked session closed");
        Assert.That(Observed.Events, Does.Not.Contain("resumed:595"));
        try { await kick; } catch (RpcException) { } // its connection was closed by the kick
    }

    [Test]
    public async Task ATicketPresentedOnTwoGatewaysAtOnceResumesOnOne()
    {
        // A replayed or stolen ticket used on both gateways at once: the player and the room must not end up split
        // between two sessions. The first actor decides; the other resume stops at its Superseded.
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        for (long player = 710; player < 730; player++)
        {
            var client = await LogIn(gateways, player);
            var room = new RoomId(player);
            Assert.That(await client.Ask(new Enter(room)), Is.True);
            await Eventually(() => client.Ticket is { Length: > 0 } t && t.Length > ResumeTicket.SizeOf(1), "no ticket with the room");
            var ticket = client.Ticket!;
            var results = await Task.WhenAll(ResumeWith(gateways, ticket, 0), ResumeWith(gateways, ticket, 1));
            Assert.That(results.Count(static r => r.Status == RpcStatus.Ok), Is.EqualTo(1), $"player {player}: one resume wins");
            var winner = results.Single(static r => r.Status == RpcStatus.Ok).Client;
            Assert.That(await winner.Ask(new AddGold(1)), Is.EqualTo(1), "the winner holds the player");
            Assert.That(await winner.Ask(room, new Exit()), Is.True, "and the room");
        }
    }

    [Test]
    public async Task AResumedSessionTakesTheActorsGroupFromThePreviousOne()
    {
        // The client moves while its old session is still open (a move, before its goodbye): once the room accepted the
        // new session, the room's broadcasts reach the new session only, not both (duplicates during every move).
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 590);
        Assert.That(await client.Ask(new Enter(new RoomId(73))), Is.True);
        await Eventually(() => client.Ticket is { Length: > 0 } t && t.Length > ResumeTicket.SizeOf(1), "no ticket with the room");
        var old = new GatewayTests.Inbox();
        client.Register(old);

        var (status, resumed) = await ResumeWith(gateways, client.Ticket!);
        Assert.That(status, Is.EqualTo(RpcStatus.Ok));
        var current = new GatewayTests.Inbox();
        resumed.Register(current);
        resumed.Send(new RoomId(73), new Say("once"));
        await Eventually(() => current.Chat.Count == 1, "the new session got the broadcast");
        await Task.Delay(300);
        Assert.That(old.Chat, Is.Empty, "the previous session left the room's group when the room took the new one");
        Assert.That(current.Chat, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ATicketResumesOnce()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 507);
        var ticket = client.Ticket!;
        var reconnected = Reconnected(client);
        client.DropConnection();
        await reconnected;

        Assert.That(await ResumeAs(gateways, ticket), Is.EqualTo(RpcStatus.Superseded), "a replayed ticket resumed again");
        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "the resumed client kept its session");
    }

    [Test]
    public async Task AWithdrawnGrantDoesNotComeBackWithAnOlderTicket()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 508);
        Assert.That(await client.Ask(new Enter(new RoomId(72))), Is.True);
        await Eventually(() => client.Ticket is { Length: > 0 } t && t.Length > ResumeTicket.SizeOf(1), "no ticket with the room");
        var older = client.Ticket!;
        Assert.That(await client.Ask(new RoomId(72), new Exit()), Is.True); // the room revokes the grant
        client.AutoReconnect = false;
        client.DropConnection();

        var (status, resumed) = await ResumeWith(gateways, older);
        Assert.That(status, Is.EqualTo(RpcStatus.Ok), "the player binding is still held");
        var exit = Assert.ThrowsAsync<RpcException>(async () => await resumed.Ask(new RoomId(72), new Exit()));
        Assert.That(exit!.Status, Is.EqualTo(RpcStatus.Forbidden), "the revoked grant came back");
        Assert.That(Observed.Events, Does.Not.Contain("room-resumed:72"), "the room took a session it had revoked");
    }

    [Test]
    public async Task NothingResumesAfterTheGrace()
    {
        var gateways = await StartCluster(TimeSpan.FromMilliseconds(300));
        var client = await LogIn(gateways, 509);
        var ticket = client.Ticket!;
        client.AutoReconnect = false;
        client.DropConnection();
        await Eventually(() => Observed.Events.Contains("closed:509"), "the close was not reported");

        Assert.That(await ResumeAs(gateways, ticket), Is.EqualTo(RpcStatus.Forbidden), "a closed session resumed");
    }

    /// <summary>A user's filter that refuses every <see cref="SessionClosed"/> (counting them).</summary>
    private sealed class RefuseCloses : IMessageFilter
    {
        public int Seen;

        public RpcStatus Admit<T>(object target, in T message, in CallContext context) where T : struct
        {
            if (typeof(T) != typeof(SessionClosed)) return RpcStatus.Ok;
            Interlocked.Increment(ref Seen);
            return RpcStatus.Forbidden;
        }
    }

    [Test]
    public async Task AFilterThatRefusesClosesDoesNotKeepAClosedSessionResumable()
    {
        // The runtime forgets a closed session before any filter runs: a filter only decides whether the handler sees it.
        var refuse = new RefuseCloses();
        using var registration = MessageFilters.Register(refuse);
        var gateways = await StartCluster(TimeSpan.FromMilliseconds(300));
        var client = await LogIn(gateways, 577);
        var ticket = client.Ticket!;
        client.AutoReconnect = false;
        client.DropConnection();
        await Eventually(() => Volatile.Read(ref refuse.Seen) > 0, "the close reached the player");

        Assert.That(await ResumeAs(gateways, ticket), Is.EqualTo(RpcStatus.Forbidden), "a closed session resumed");
        Assert.That(Observed.Events, Does.Not.Contain("closed:577"), "the filter kept the close from the handler");
    }

    [Test]
    public async Task AnActorThatHandlesNoCloseStillForgetsAClosedSession()
    {
        // The lounge grants the session but has no SessionClosed handler: the runtime forgets the session all the same,
        // instead of holding it (and a ticket's way back in) for the actor's life.
        var gateways = await StartCluster(TimeSpan.FromMilliseconds(300));
        var client = await LogIn(gateways, 581);
        Assert.That(await client.Ask(new SitDown(new LoungeId(1))), Is.True);
        Assert.That(await gateways[0].Ask(new LoungeId(1), new Seated()), Is.Positive, "the lounge holds the session");
        client.AutoReconnect = false;
        client.DropConnection();
        await Eventually(async () => await gateways[0].Ask(new LoungeId(1), new Seated()) == 0, "the closed session was forgotten");
    }

    [Test]
    public async Task ACloseThatReachesTheNewOwnerBeforeTheRecordIsNotLost()
    {
        // The lounge moves while its previous node holds it in a turn; the closed session's close reaches the new owner
        // before the handoff record (which carries the grant): it waits behind the record instead of being dropped.
        Faults.Allow("TurnTooLong"); // the held turn is the point
        var gateways = await StartCluster(TimeSpan.FromMilliseconds(300));
        var game = Nodes.Single(n => n.Options.Name == "game");
        var next = await Start("game-2", configure: Secret(TimeSpan.FromMilliseconds(300)));
        await Converged();
        long key = game.KeyOwnedBy<LoungeId>(game);
        var client = await LogIn(gateways, 582);
        Assert.That(await client.Ask(new SitDown(new LoungeId(key))), Is.True);
        Assert.That(await game.Ask(new LoungeId(key), new Seated()), Is.Positive, "the lounge holds the session");

        TurnGate.Current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = game.Ask(new LoungeId(key), new HoldLounge()).AsTask();
        int slot = ClusterView.Slot(ActorIds<LoungeId>.HandlerTypeId, key);
        var table = new SlotTable { Version = 1000, Coordinator = game.Id, Overrides = [new SlotOverride(ActorIds<LoungeId>.HandlerTypeId, (ushort)slot, next.Id)] };
        foreach (var node in Nodes) node.Membership.Apply(table);
        client.AutoReconnect = false;
        client.DropConnection();
        await Task.Delay(1000); // past the grace: the close went to the new owner, whose record is still held
        TurnGate.Current.TrySetResult();
        try { await held; } catch (RpcException) { } // its caller may have timed out meanwhile

        await Eventually(async () => await next.Ask(new LoungeId(key), new Seated()) == 0, "the moved lounge forgot the closed session");
    }

    [Test]
    public async Task UnreadableClosesAndResumesAreRefusedAndTheActorGoesOn()
    {
        // Only a faulty peer could send them: they are refused, not thrown out of the mailbox (no failed system).
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 583);
        var game = Nodes.Single(n => n.Options.Name == "game");
        var ingress = ((DaServer.Features.INodeContext)game).Ingress;
        var origin = new Origin(game.Id, 1);
        ingress.Send(TypeId<SessionResumed>.Value, ActorIds<PlayerId>.HandlerTypeId, 583, [0xFF], origin);
        ingress.Send(TypeId<SessionClosed>.Value, ActorIds<PlayerId>.HandlerTypeId, 583, [0xFF], origin);
        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "the player goes on with its session");
        Assert.That(Observed.Events, Does.Not.Contain("closed:583").And.Not.Contain("resumed:583"));
    }

    [Test]
    public void TicketsAreUnforgeableAndExpire()
    {
        byte[] secret = Encoding.UTF8.GetBytes("secret");
        var session = new ClientSession(7, 42);
        SessionGrant[] grants = [new(1, 100, Bound: true), new(2, 200, Bound: false)];
        var ticket = new byte[ResumeTicket.SizeOf(grants.Length)];
        ResumeTicket.Write(ticket, secret, session, grants, nowMs: 1000, lifetimeMs: 500);

        Assert.That(ResumeTicket.TryRead(ticket, [secret], 1200, out var read, out var readGrants), Is.True);
        Assert.That(read, Is.EqualTo(session));
        Assert.That(ResumeTicket.GrantCount(readGrants), Is.EqualTo(2));
        Assert.That(ResumeTicket.GrantAt(readGrants, 0), Is.EqualTo(grants[0]));
        Assert.That(ResumeTicket.GrantAt(readGrants, 1), Is.EqualTo(grants[1]));

        Assert.That(ResumeTicket.TryRead(ticket, [secret], 1500, out _, out _), Is.False, "expired");
        Assert.That(ResumeTicket.TryRead(ticket, [Encoding.UTF8.GetBytes("other")], 1200, out _, out _), Is.False, "another cluster");
        Assert.That(ResumeTicket.TryRead(ticket, [Encoding.UTF8.GetBytes("next"), secret], 1200, out _, out _), Is.True,
            "signed with the secret a rotation still accepts");
        ticket[30] ^= 1; // another session
        Assert.That(ResumeTicket.TryRead(ticket, [secret], 1200, out _, out _), Is.False, "tampered");
        Assert.That(ResumeTicket.TryRead(ticket.AsSpan(0, 20), [secret], 1200, out _, out _), Is.False, "truncated");
    }
}
