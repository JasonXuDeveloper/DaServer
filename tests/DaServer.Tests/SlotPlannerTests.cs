namespace DaServer.Tests;

/// <summary>The coordinator's plan (ADR-079) edge by edge: a pure function of the view, the table and the reports.</summary>
public class SlotPlannerTests
{
    private const uint Message = 7, Actor = 9;

    private static ClusterMember Member(ulong id, MemberStatus status = MemberStatus.Up) => new()
    {
        NodeId = id, Name = "n" + id, Status = status, Handlers = [new HostedMessage { MessageId = Message, HandlerTypeId = Actor, Actor = true }],
    };

    private static SlotTable Table(params SlotOverride[] overrides) => new() { Version = 1, Overrides = overrides };

    private static SlotReport Report(ulong node, bool sticky, params (ushort Slot, int Count)[] slots) => new()
    {
        Node = node,
        Types = [new SlotLoad { HandlerTypeId = Actor, Sticky = sticky, Slots = [.. slots.Select(static s => s.Slot)], Counts = [.. slots.Select(static s => s.Count)] }],
    };

    private static Dictionary<ulong, SlotReport> Reports(params SlotReport[] reports) => reports.ToDictionary(static r => r.Node);

    /// <summary>A slot rendezvous gives to <paramref name="node"/> in <paramref name="view"/>.</summary>
    private static ushort SlotOf(ClusterView view, ulong node, int skip = 0)
    {
        for (int slot = 0; slot < ClusterView.Slots; slot++)
            if (view.RendezvousOwner(Actor, slot)!.NodeId == node && skip-- == 0) return (ushort)slot;
        throw new InvalidOperationException();
    }

    [Test]
    public void ACoordinatorWithoutReportsKeepsTheTable()
    {
        // It just took over: pins it cannot check yet stay, whether rendezvous agrees or not.
        var view = new ClusterView([Member(1), Member(2)]);
        var table = Table(new SlotOverride(Actor, SlotOf(view, 1), 1), new SlotOverride(Actor, SlotOf(view, 1, 1), 2));
        Assert.That(SlotPlanner.Plan(new ClusterView(view.Members, table), Reports()), Is.EqualTo(table.Overrides));
    }

    [Test]
    public void AnOverrideOfANodeThatLeftIsDropped()
    {
        var view = new ClusterView([Member(1), Member(2, MemberStatus.Dead)]);
        var table = Table(new SlotOverride(Actor, 5, 2));
        Assert.That(SlotPlanner.Plan(new ClusterView(view.Members, table), Reports()), Is.Empty);
    }

    [Test]
    public void StickyPinsAreTheSlotsTheirNodesReportLoaded()
    {
        var view = new ClusterView([Member(1), Member(2)]);
        ushort kept = SlotOf(view, 1), emptied = SlotOf(view, 1, 1), loaded = SlotOf(view, 2);
        var table = Table(new SlotOverride(Actor, kept, 2), new SlotOverride(Actor, emptied, 2));
        var plan = SlotPlanner.Plan(new ClusterView(view.Members, table), Reports(Report(2, sticky: true, (kept, 3), (loaded, 1)), Report(1, sticky: true)));
        Assert.That(plan, Is.EquivalentTo(new[] { new SlotOverride(Actor, kept, 2), new SlotOverride(Actor, loaded, 2) }),
            "a slot still loaded stays pinned (even where rendezvous agrees), an emptied one is released");
    }

    [Test]
    public void ASlotTwoNodesReportWhileItMovesStaysWithItsOwner()
    {
        var view = new ClusterView([Member(1), Member(2)]);
        ushort slot = SlotOf(view, 2);
        var reports = Reports(Report(1, sticky: true, (slot, 1)), Report(2, sticky: true, (slot, 1)));
        Assert.That(SlotPlanner.Plan(new ClusterView(view.Members, Table()), reports), Is.EqualTo(new[] { new SlotOverride(Actor, slot, 2) }));
        Assert.That(SlotPlanner.Plan(new ClusterView(view.Members, Table(new SlotOverride(Actor, slot, 1))), reports), Is.EqualTo(new[] { new SlotOverride(Actor, slot, 1) }),
            "the owner in the view (the override) wins");
    }

    [Test]
    public void AMoveIsKeptUntilRendezvousAgrees()
    {
        var view = new ClusterView([Member(1), Member(2)]);
        ushort toOne = SlotOf(view, 2), agreed = SlotOf(view, 1);
        var table = Table(new SlotOverride(Actor, toOne, 1), new SlotOverride(Actor, agreed, 1));
        var reports = Reports(Report(1, sticky: false, (toOne, 1), (agreed, 1)), Report(2, sticky: false, (SlotOf(view, 2, 1), 1)));
        Assert.That(SlotPlanner.Plan(new ClusterView(view.Members, table), reports), Is.EqualTo(new[] { new SlotOverride(Actor, toOne, 1) }),
            "an override rendezvous now agrees with is dropped (the table stays small)");
    }

    [Test]
    public void AnImbalanceBelowTheThresholdMovesNothing()
    {
        var view = new ClusterView([Member(1), Member(2)]);
        // 11 against 9: the hot node holds 1.1 times the average.
        var reports = Reports(
            Report(1, sticky: false, [.. Enumerable.Range(0, 11).Select(i => (SlotOf(view, 1, i), 1))]),
            Report(2, sticky: false, [.. Enumerable.Range(0, 9).Select(i => (SlotOf(view, 2, i), 1))]));
        Assert.That(SlotPlanner.Plan(new ClusterView(view.Members, Table()), reports), Is.Empty);
    }

    [Test]
    public void AnImbalanceMovesAtMostTwoSmallSlotsToTheColdestUpNode()
    {
        var view = new ClusterView([Member(1), Member(2), Member(3, MemberStatus.Suspect)]);
        var hot = Enumerable.Range(0, 10).Select(i => (SlotOf(view, 1, i), i < 3 ? 1 : 5)).ToArray();
        var reports = Reports(Report(1, sticky: false, hot), Report(2, sticky: false));
        var plan = SlotPlanner.Plan(new ClusterView(view.Members, Table()), reports);
        Assert.That(plan, Has.Length.EqualTo(SlotPlanner.MovesPerRound));
        Assert.That(plan.Select(static o => o.Node), Is.All.EqualTo(2UL), "to the coldest Up node (a suspect is no target)");
        Assert.That(plan.Select(static o => o.Slot), Is.SubsetOf(hot.Take(3).Select(static s => s.Item1)), "the smallest slots first");
    }

    [Test]
    public void StickyTypesAreNeverEvenedOut()
    {
        var view = new ClusterView([Member(1), Member(2)]);
        var hot = Enumerable.Range(0, 10).Select(i => (SlotOf(view, 1, i), 5)).ToArray();
        var plan = SlotPlanner.Plan(new ClusterView(view.Members, Table()), Reports(Report(1, sticky: true, hot), Report(2, sticky: true)));
        Assert.That(plan.Select(static o => o.Node), Is.All.EqualTo(1UL));
    }

    [Test]
    public void TheSameInputPlansTheSameTable()
    {
        // The coordinator compares plans to decide whether to publish a new version: order must not vary.
        var view = new ClusterView([Member(1), Member(2)]);
        var reports = Reports(Report(1, sticky: true, (SlotOf(view, 2, 3), 1), (SlotOf(view, 2, 1), 1), (SlotOf(view, 2, 2), 1)));
        var plan = SlotPlanner.Plan(new ClusterView(view.Members, Table()), reports);
        Assert.That(SlotPlanner.Plan(new ClusterView(view.Members, Table(plan)), reports), Is.EqualTo(plan));
        Assert.That(plan.Select(static o => o.Slot), Is.Ordered);
    }
}
