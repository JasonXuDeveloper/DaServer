namespace DaServer.Cluster;

/// <summary>
/// Slot allocation (ADR-079), on every node: each round (<see cref="NodeOptions.SlotRound"/>) the node reports to the
/// coordinator which slots of each actor type hold loaded actors here; the coordinator (the oldest Up member) pins the
/// loaded slots of <see cref="StickyAttribute">sticky</see> types where they are, moves up to two loaded slots per type
/// from the most to the least loaded node when one holds well above the average, and sends the resulting table to every
/// member. Cold: a few thousand entries per round, on the maintenance thread and IO threads.
/// </summary>
internal sealed class SlotAllocation(Node node)
{
    /// <summary>A node at more than this share of the average holds too much (and two slots more than the least loaded).</summary>
    private const double Hot = 1.25;

    /// <summary>Slots moved per actor type and round: moves are handoffs, a few at a time.</summary>
    private const int MovesPerRound = 2;

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, SlotReport> _reports = new(); // the coordinator's: the latest report of each node
    private readonly HashSet<ulong> _sentTo = new(); // the coordinator's: members that have the current table
    private long _nextRoundMs;
    private int _reporting; // 1 while the shards count (a shard that never answers, stopping, is given up after 3 rounds)
    private long _reportStartedMs;
    private int _rounds; // the coordinator's: tables are sent to everyone again every second round

    /// <summary>The maintenance tick: once per round, report and, on the coordinator, allocate.</summary>
    public void Tick(long nowMs)
    {
        if (nowMs < _nextRoundMs) return;
        long round = (long)node.Options.SlotRound.TotalMilliseconds;
        _nextRoundMs = nowMs + round;
        if (Volatile.Read(ref _reporting) == 1 && nowMs - _reportStartedMs > 3 * round) Volatile.Write(ref _reporting, 0);
        if (Interlocked.Exchange(ref _reporting, 1) == 0)
        {
            _reportStartedMs = nowMs;
            Report();
        }
        if (IsCoordinator(node.View)) Allocate(node.View);
    }

    private bool IsCoordinator(ClusterView view) => view.Coordinator?.NodeId == node.Id;

    // ------------------------------------------------------------------ reports (every node)

    /// <summary>Count this node's loaded actors per slot on every shard, then send the report to the coordinator.</summary>
    private void Report()
    {
        var hosted = node.HostedHandlerTypes;
        var counts = new Dictionary<uint, int[]>();
        var sticky = new HashSet<uint>();
        foreach (var type in hosted)
            if (type.IsActor)
            {
                counts[type.Id] = new int[ClusterView.Slots];
                if (type.Sticky) sticky.Add(type.Id);
            }

        if (counts.Count == 0)
        {
            Volatile.Write(ref _reporting, 0);
            return;
        }

        int pending = node.ShardCount;
        node.PostToEveryShard(static (world, state) =>
        {
            var (allocation, counts, sticky, pending) = state;
            lock (counts) // shards count one after the other into the same arrays (cold: once per round)
                foreach (var address in world.GetSingleton<ActorDirectory>().Actors.Keys)
                    if (counts.TryGetValue(address.HandlerTypeId, out var slots))
                        slots[ClusterView.Slot(address.HandlerTypeId, address.Key)]++;
            if (Interlocked.Decrement(ref pending.Value) == 0) allocation.Send(counts, sticky);
        }, (this, counts, sticky, new StrongBox<int>(pending)));
    }

    private void Send(Dictionary<uint, int[]> counts, HashSet<uint> sticky)
    {
        try
        {
            var report = new SlotReport { Node = node.Id, Types = new SlotLoad[counts.Count] };
            int t = 0;
            foreach (var (type, slots) in counts)
            {
                var loaded = new List<ushort>();
                var load = new List<int>();
                for (int slot = 0; slot < slots.Length; slot++)
                    if (slots[slot] > 0)
                    {
                        loaded.Add((ushort)slot);
                        load.Add(slots[slot]);
                    }

                report.Types[t++] = new SlotLoad { HandlerTypeId = type, Sticky = sticky.Contains(type), Slots = [.. loaded], Counts = [.. load] };
            }

            if (node.View.Coordinator is not { } coordinator) return;
            if (coordinator.NodeId == node.Id) OnReport(report);
            else
            {
                var w = FrameWriter.Rent(1024);
                w.Write(report);
                w.SendControl(node.GetLink(coordinator), ControlOp.SlotReport);
            }
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }

    /// <summary>A node's report (IO thread): kept for the coordinator's next round.</summary>
    public void OnReport(SlotReport report)
    {
        lock (_gate) _reports[report.Node] = report;
    }

    /// <summary>A table from the coordinator (IO thread): applied when newer.</summary>
    public void OnTable(SlotTable table) => node.Membership.Apply(table);

    // ------------------------------------------------------------------ allocation (the coordinator)

    private void Allocate(ClusterView view)
    {
        SlotTable table;
        lock (_gate)
        {
            foreach (ulong reporter in _reports.Keys.ToArray()) // gone nodes' reports go
                if (!view.TryGetMember(reporter, out var m) || m.Status is not (MemberStatus.Up or MemberStatus.Suspect))
                    _reports.Remove(reporter);
            var current = view.Table;
            var overrides = Plan(view, current);
            if (!Same(overrides, current.Overrides))
            {
                table = new SlotTable { Version = current.Version + 1, Coordinator = node.Id, Overrides = overrides };
                _sentTo.Clear();
            }
            else table = current;
        }

        if (table != view.Table) node.Membership.Apply(table);
        Broadcast(view, table);
    }

    /// <summary>
    /// The overrides this round: sticky types pinned where they are loaded, moved slots kept until rendezvous agrees,
    /// and up to two new moves per type from a hot node to the coldest one.
    /// </summary>
    private SlotOverride[] Plan(ClusterView view, SlotTable current)
    {
        var next = new Dictionary<(uint Type, ushort Slot), ulong>();
        var sticky = new HashSet<uint>();
        foreach (var report in _reports.Values)
            foreach (var load in report.Types)
                if (load.Sticky) sticky.Add(load.HandlerTypeId);

        // Moves made before stay while their node still hosts the type and rendezvous would put the slot elsewhere.
        foreach (var o in current.Overrides)
            if (!sticky.Contains(o.HandlerTypeId) && Hosts(view, o.HandlerTypeId, o.Node) &&
                view.RendezvousOwner(o.HandlerTypeId, o.Slot)?.NodeId != o.Node)
                next[(o.HandlerTypeId, o.Slot)] = o.Node;

        // Sticky types: every loaded slot stays with the node that holds it (a slot two nodes report, while it moves,
        // stays with its owner in the view).
        foreach (var (reporter, report) in _reports)
            foreach (var load in report.Types)
                if (load.Sticky)
                    foreach (ushort slot in load.Slots)
                    {
                        var key = (load.HandlerTypeId, slot);
                        if (!next.ContainsKey(key) || view.SlotOwner(load.HandlerTypeId, slot)?.NodeId == reporter) next[key] = reporter;
                    }

        foreach (uint type in LoadedTypes()) if (!sticky.Contains(type)) Even(view, type, next);

        var overrides = new SlotOverride[next.Count];
        int i = 0;
        foreach (var ((type, slot), owner) in next) overrides[i++] = new SlotOverride(type, slot, owner);
        Array.Sort(overrides, static (a, b) => a.HandlerTypeId != b.HandlerTypeId ? a.HandlerTypeId.CompareTo(b.HandlerTypeId) : a.Slot.CompareTo(b.Slot));
        return overrides;
    }

    private HashSet<uint> LoadedTypes()
    {
        var types = new HashSet<uint>();
        foreach (var report in _reports.Values)
            foreach (var load in report.Types) types.Add(load.HandlerTypeId);
        return types;
    }

    /// <summary>
    /// Even out actor type <paramref name="type"/>: when the most loaded Up node holds more than <see cref="Hot"/> times
    /// the average and two loaded slots more than the least loaded one, move its smallest loaded slots to the least
    /// loaded node, at most <see cref="MovesPerRound"/>, and no more actors than half the difference.
    /// </summary>
    private void Even(ClusterView view, uint type, Dictionary<(uint Type, ushort Slot), ulong> next)
    {
        var nodes = new List<(ulong Node, int Actors, List<(ushort Slot, int Count)> Slots)>();
        foreach (var member in view.NodesOf(type))
        {
            if (member.Status != MemberStatus.Up) continue;
            var slots = new List<(ushort, int)>();
            int actors = 0;
            if (_reports.TryGetValue(member.NodeId, out var report))
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

    private static bool Same(SlotOverride[] a, SlotOverride[] b) => a.AsSpan().SequenceEqual(b);

    /// <summary>Send the table to the members that do not have it yet, and to everyone every few rounds.</summary>
    private void Broadcast(ClusterView view, SlotTable table)
    {
        if (table.Version == 0) return;
        if (++_rounds % 2 == 0) lock (_gate) _sentTo.Clear(); // a lost table is sent again
        foreach (var member in view.Members)
        {
            if (member.NodeId == node.Id || member.Status is not (MemberStatus.Up or MemberStatus.Suspect)) continue;
            lock (_gate) if (!_sentTo.Add(member.NodeId)) continue;
            var w = FrameWriter.Rent(1024);
            w.Write(table);
            w.SendControl(node.GetLink(member), ControlOp.SlotTable);
        }
    }
}
