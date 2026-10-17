namespace DaServer.Cluster;

/// <summary>
/// The coordinator's plan (ADR-079), a pure function of the view, the current table and the nodes' reports, so every
/// edge (a coordinator that took over, a node that has not reported, a slot two nodes report, an imbalance) is a unit
/// test (<c>SlotPlannerTests</c>). Cold: once per round with a change.
/// </summary>
internal static class SlotPlanner
{
    /// <summary>A node at more than this share of the average holds too much (and two slots more than the least loaded).</summary>
    public const double Hot = 1.25;

    /// <summary>Slots moved per actor type and round: moves are handoffs, a few at a time.</summary>
    public const int MovesPerRound = 2;

    /// <summary>
    /// The overrides this round: sticky types pinned where they are loaded, moved slots kept until rendezvous agrees,
    /// and up to two new moves per type from a hot node to the coldest one.
    /// </summary>
    public static SlotOverride[] Plan(ClusterView view, IReadOnlyDictionary<ulong, SlotReport> reports)
    {
        var current = view.Table;
        var next = new Dictionary<(uint Type, ushort Slot), ulong>();
        var sticky = new HashSet<uint>();
        foreach (var report in reports.Values)
            foreach (var load in report.Types)
                if (load.Sticky) sticky.Add(load.HandlerTypeId);

        // The current table stays where this coordinator knows no better: an override whose node has not reported yet
        // (a coordinator that just took over plans before every report reached it) or whose type's stickiness no report
        // tells is kept while its node hosts the type; a move made before stays while rendezvous would put the slot
        // elsewhere. Sticky types' overrides of reporting nodes are what those nodes report loaded (below).
        foreach (var o in current.Overrides)
        {
            if (!Hosts(view, o.HandlerTypeId, o.Node)) continue; // its node is gone: rendezvous places the slot
            bool reported = reports.ContainsKey(o.Node);
            if (sticky.Contains(o.HandlerTypeId) ? !reported
                : !reported || !Known(reports, o.HandlerTypeId) || view.RendezvousOwner(o.HandlerTypeId, o.Slot)?.NodeId != o.Node)
                next[(o.HandlerTypeId, o.Slot)] = o.Node;
        }

        // Sticky types: every loaded slot stays with the node that holds it (a slot two nodes report, while it moves,
        // stays with its owner in the view).
        foreach (var (reporter, report) in reports)
            foreach (var load in report.Types)
                if (load.Sticky)
                    foreach (ushort slot in load.Slots)
                    {
                        var key = (load.HandlerTypeId, slot);
                        if (!next.ContainsKey(key) || view.SlotOwner(load.HandlerTypeId, slot)?.NodeId == reporter) next[key] = reporter;
                    }

        foreach (uint type in LoadedTypes(reports)) if (!sticky.Contains(type)) Even(view, reports, type, next);

        var overrides = new SlotOverride[next.Count];
        int i = 0;
        foreach (var ((type, slot), owner) in next) overrides[i++] = new SlotOverride(type, slot, owner);
        Array.Sort(overrides, static (a, b) => a.HandlerTypeId != b.HandlerTypeId ? a.HandlerTypeId.CompareTo(b.HandlerTypeId) : a.Slot.CompareTo(b.Slot));
        return overrides;
    }

    /// <summary>Whether a report names actor type <paramref name="type"/> (and so whether it is sticky).</summary>
    private static bool Known(IReadOnlyDictionary<ulong, SlotReport> reports, uint type)
    {
        foreach (var report in reports.Values)
            foreach (var load in report.Types)
                if (load.HandlerTypeId == type) return true;
        return false;
    }

    private static HashSet<uint> LoadedTypes(IReadOnlyDictionary<ulong, SlotReport> reports)
    {
        var types = new HashSet<uint>();
        foreach (var report in reports.Values)
            foreach (var load in report.Types) types.Add(load.HandlerTypeId);
        return types;
    }

    /// <summary>
    /// Even out actor type <paramref name="type"/>: when the most loaded Up node holds more than <see cref="Hot"/> times
    /// the average and two loaded slots more than the least loaded one, move its smallest loaded slots to the least
    /// loaded node, at most <see cref="MovesPerRound"/>, and no more actors than half the difference.
    /// </summary>
    private static void Even(ClusterView view, IReadOnlyDictionary<ulong, SlotReport> reports, uint type, Dictionary<(uint Type, ushort Slot), ulong> next)
    {
        var nodes = new List<(ulong Node, int Actors, List<(ushort Slot, int Count)> Slots)>();
        foreach (var member in view.NodesOf(type))
        {
            if (member.Status != MemberStatus.Up) continue;
            var slots = new List<(ushort, int)>();
            int actors = 0;
            if (reports.TryGetValue(member.NodeId, out var report))
                foreach (var load in report.Types)
                    if (load.HandlerTypeId == type)
                        for (int i = 0; i < load.Slots.Length; i++)
                        {
                            slots.Add((load.Slots[i], load.Counts[i]));
                            actors += load.Counts[i];
                        }
            nodes.Add((member.NodeId, actors, slots));
        }

        if (nodes.Count < 2) return;
        nodes.Sort(static (a, b) => b.Actors.CompareTo(a.Actors));
        var (hot, cold) = (nodes[0], nodes[^1]);
        double average = nodes.Sum(static n => n.Actors) / (double)nodes.Count;
        if (hot.Actors <= Hot * average || hot.Slots.Count - cold.Slots.Count < 2) return;

        int budget = (hot.Actors - cold.Actors) / 2, moved = 0, moves = 0;
        hot.Slots.Sort(static (a, b) => a.Count.CompareTo(b.Count));
        foreach (var (slot, count) in hot.Slots)
        {
            if (moves == MovesPerRound || moved + count > budget) break;
            next[(type, slot)] = cold.Node;
            moved += count;
            moves++;
        }
    }

    private static bool Hosts(ClusterView view, uint type, ulong nodeId)
    {
        foreach (var member in view.NodesOf(type))
            if (member.NodeId == nodeId) return true;
        return false;
    }
}
