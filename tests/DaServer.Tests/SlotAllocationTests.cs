namespace DaServer.Tests;

/// <summary>Slot allocation by the coordinator (ADR-079): sticky types stay, others are evened out by load.</summary>
public class SlotAllocationTests : ClusterTest
{
    private static void FastRounds(NodeOptions o) => o.SlotRound = TimeSpan.FromMilliseconds(200);

    /// <summary>The distinct slots of <paramref name="keys"/> (keys may share one).</summary>
    private static int SlotsOf<TId>(IEnumerable<long> keys) where TId : struct, IActorId =>
        keys.Select(static k => ClusterView.Slot(ActorIds<TId>.HandlerTypeId, k)).Distinct().Count();

    [Test]
    public async Task LoadedStickyActorsStayWhenANodeJoinsAndNewKeysGoToIt()
    {
        var a = await Start("a", configure: FastRounds);
        for (long key = 1; key <= 40; key++) Assert.That(await a.Ask(new PinId(key), new Visit()), Is.EqualTo(1));
        int slots = SlotsOf<PinId>(Enumerable.Range(1, 40).Select(static k => (long)k));
        await Eventually(() => a.View.Table.Overrides.Length == slots, "the coordinator pinned the loaded slots");

        var b = await Start("b", configure: FastRounds);
        await Converged();
        await Eventually(() => b.View.Table.Version == a.View.Table.Version, "the new node has the table");
        for (long key = 1; key <= 40; key++)
        {
            Assert.That(b.OwnerOf<PinId>(key), Is.EqualTo(a.Id), "a loaded sticky actor did not move");
            Assert.That(await b.Ask(new PinId(key), new Visit()), Is.EqualTo(2), "and kept its state");
        }

        int toB = 0;
        for (long key = 1000; key < 1100; key++)
            if (b.OwnerOf<PinId>(key) == b.Id) toB++;
        Assert.That(toB, Is.GreaterThan(20), "new keys go to the new node too");
    }

    [Test]
    public async Task AnImbalanceIsEvenedOutAFewSlotsAtATime()
    {
        var a = await Start("a", configure: FastRounds);
        var b = await Start("b", configure: FastRounds);
        await Converged();
        var keys = new List<long>();
        for (long key = 1; keys.Count < 40; key++)
            if (a.OwnerOf<LoadId>(key) == a.Id) keys.Add(key); // every one on a: b holds none
        foreach (long key in keys) Assert.That(await a.Ask(new LoadId(key), new Visit()), Is.EqualTo(1));

        await Eventually(() => b.ActorCount >= 12, $"b took some of a's actors (a {a.ActorCount}, b {b.ActorCount})", 15_000);
        foreach (long key in keys) Assert.That(await a.Ask(new LoadId(key), new Visit()), Is.EqualTo(2), "each moved with its state");
        await Task.Delay(1000);
        Assert.That(a.ActorCount, Is.GreaterThanOrEqualTo(b.ActorCount), "it stops once even (no swing to the other side)");
    }

    [Test]
    public async Task ANewCoordinatorKeepsThePins()
    {
        var a = await Start("a", configure: FastRounds);
        var b = await Start("b", configure: FastRounds);
        await Converged();
        var keys = new List<long>();
        for (long key = 1; keys.Count < 20; key++)
            if (a.OwnerOf<PinId>(key) == b.Id) keys.Add(key);
        foreach (long key in keys) await a.Ask(new PinId(key), new Visit());
        int slots = SlotsOf<PinId>(keys);
        await Eventually(() => b.View.Table.Overrides.Length == slots, "a, the coordinator, pinned b's loaded slots");

        await Stop(a); // b becomes the coordinator, with the table it received
        var c = await Start("c", configure: FastRounds);
        await Converged();
        await Task.Delay(1000); // a few rounds of the new coordinator
        foreach (long key in keys) Assert.That(c.OwnerOf<PinId>(key), Is.EqualTo(b.Id), "the pins survived the coordinator");
    }
}
