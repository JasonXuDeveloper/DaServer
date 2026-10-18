using System.Runtime.InteropServices;

namespace DaServer.Cluster;

/// <summary>
/// Slot allocation (ADR-079), on every node: each round (<see cref="NodeOptions.SlotRound"/>) the node reports to the
/// coordinator which slots of each actor type hold loaded actors here; the coordinator (the oldest Up member) pins the
/// loaded slots of <see cref="StickyAttribute">sticky</see> types where they are, moves up to two loaded slots per type
/// from the most to the least loaded node when one holds well above the average, and sends the resulting table to every
/// member. A round where nothing changed costs a count over the shards and nothing else: a node reports only a changed
/// load (or to a new coordinator), the coordinator plans only after a new report or a membership change, so steady state
/// allocates nothing. Cold: a few thousand entries per round, on the maintenance thread and IO threads.
/// </summary>
internal sealed class SlotAllocation(Node node)
{
    /// <summary>Rounds after which an unchanged report and the current table are sent again (a frame lost with a link).</summary>
    private const int ResendRounds = 12;

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, SlotReport> _reports = new(); // the coordinator's: the latest report of each node
    private readonly HashSet<ulong> _sentTo = new(); // the coordinator's: members that have the current table
    private long _nextRoundMs;
    private int _reporting; // 1 while the shards count (a shard that never answers, stopping, is given up after 3 rounds)
    private long _reportStartedMs;
    private int _rounds; // the coordinator's: tables are sent to everyone again every ResendRounds rounds

    // The node's counts, reused every round: loaded actors per (actor type, slot) that holds any (sparse: a node hosting
    // many actor types keeps no table per type), and its hosted actor types.
    private readonly Dictionary<ulong, int> _counts = new(); // under its own lock, with _round and _pending
    private int _round; // the report the shards count for: a shard that answers a given-up round counts nothing
    private HashSet<uint>? _hosted;
    private readonly HashSet<uint> _sticky = new();
    private readonly StrongBox<int> _pending = new();
    private long _sentFingerprint;
    private ulong _sentCoordinator;
    private int _unsentRounds;

    // The coordinator's: whether a report or the membership changed since the last plan; rounds since it took over.
    private bool _reportsChanged;
    private ClusterView? _plannedView;
    private bool _coordinating;
    private int _roundsCoordinating;

    /// <summary>Rounds a coordinator that just took over waits for a member that has not reported (stopping, slow).</summary>
    private const int TakeOverRounds = 3;

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
        bool coordinating = IsCoordinator(node.View);
        if (coordinating != _coordinating) TakeOver(coordinating);
        if (coordinating) Allocate(node.View);
    }

    /// <summary>
    /// This node became (or stopped being) the coordinator: reports it kept from an earlier term are stale (a node's
    /// load then, its own before it met the cluster), so it starts from none and plans once every member reported.
    /// </summary>
    private void TakeOver(bool coordinating)
    {
        lock (_gate)
        {
            _coordinating = coordinating;
            _reports.Clear();
            _sentTo.Clear();
            (_roundsCoordinating, _plannedView) = (0, null);
        }
    }

    private bool IsCoordinator(ClusterView view) => view.Coordinator?.NodeId == node.Id;

    // ------------------------------------------------------------------ reports (every node)

    /// <summary>Count this node's loaded actors per slot on every shard, then send the report to the coordinator.</summary>
    private void Report()
    {
        var hosted = _hosted ??= Hosted();
        if (hosted.Count == 0)
        {
            Volatile.Write(ref _reporting, 0);
            return;
        }

        int round;
        lock (_counts)
        {
            _counts.Clear(); // keeps its capacity
            _pending.Value = node.ShardCount;
            round = ++_round;
        }

        node.PostToEveryShard(static (world, state) =>
        {
            var (allocation, round) = state;
            var counts = allocation._counts;
            bool last;
            lock (counts) // shards count one after the other (cold: once per round)
            {
                if (round != allocation._round) return; // a round given up (this shard was stalled): the next counts anew
                foreach (var address in world.GetSingleton<ActorDirectory>().Actors.Keys)
                    if (allocation._hosted!.Contains(address.HandlerTypeId))
                        CollectionsMarshal.GetValueRefOrAddDefault(counts, CountKey(address.HandlerTypeId, ClusterView.Slot(address.HandlerTypeId, address.Key)), out _)++;
                last = --allocation._pending.Value == 0;
            }

            if (last) allocation.Send(round);
        }, (this, round));
    }

    private static ulong CountKey(uint type, int slot) => (ulong)type << 16 | (uint)slot;

    private HashSet<uint> Hosted()
    {
        var hosted = new HashSet<uint>();
        foreach (var type in node.HostedHandlerTypes)
            if (type.IsActor)
            {
                hosted.Add(type.Id);
                if (type.Sticky) _sticky.Add(type.Id);
            }

        return hosted;
    }

    /// <summary>Send the counts to the coordinator when they changed, the coordinator did, or a while passed.</summary>
    private void Send(int round)
    {
        try
        {
            var counts = _counts;
            var sticky = _sticky;
            if (node.View.Coordinator is not { } coordinator) return;
            SlotReport report;
            lock (counts) // a round that was given up meanwhile clears them
            {
                if (round != _round) return;
                long fingerprint = Fingerprint(counts);
                if (fingerprint == _sentFingerprint && coordinator.NodeId == _sentCoordinator && ++_unsentRounds < ResendRounds) return;
                (_sentFingerprint, _sentCoordinator, _unsentRounds) = (fingerprint, coordinator.NodeId, 0);

                report = new SlotReport { Node = node.Id, Types = new SlotLoad[_hosted!.Count] };
                int t = 0;
                foreach (uint type in _hosted) // every hosted type, loaded or not: the coordinator learns what is sticky
                {
                    var loaded = new List<(ushort Slot, int Count)>();
                    foreach (var (key, count) in counts)
                        if ((uint)(key >> 16) == type) loaded.Add(((ushort)key, count));
                    loaded.Sort();
                    report.Types[t++] = new SlotLoad
                    {
                        HandlerTypeId = type, Sticky = sticky.Contains(type),
                        Slots = [.. loaded.Select(static l => l.Slot)], Counts = [.. loaded.Select(static l => l.Count)],
                    };
                }
            }

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
            if (round == Volatile.Read(ref _round)) Volatile.Write(ref _reporting, 0); // a newer round may be counting
        }
    }

    /// <summary>The counts' fingerprint, whatever order the shards added them in (a sum of mixed entries).</summary>
    private static long Fingerprint(Dictionary<ulong, int> counts)
    {
        ulong sum = 0;
        foreach (var (key, count) in counts)
        {
            ulong x = (key ^ (ulong)count << 48) * 0x9E3779B97F4A7C15UL;
            sum += x ^ (x >> 29);
        }

        return (long)sum;
    }

    /// <summary>A node's report (IO thread): kept for the coordinator's next round.</summary>
    public void OnReport(SlotReport report)
    {
        lock (_gate)
        {
            _reports[report.Node] = report;
            _reportsChanged = true;
        }
    }

    /// <summary>A table from the coordinator (IO thread): applied when newer.</summary>
    public void OnTable(SlotTable table) => node.Membership.Apply(table);

    /// <summary>
    /// A node linked to this one (IO thread): it gets the table this node has at once, so a joining node routes by the
    /// coordinator's table from its first link on, not by rendezvous alone until a round reaches it.
    /// </summary>
    public void OnLinked(PeerLink link)
    {
        var table = node.View.Table;
        if (table.Version == 0) return;
        var w = FrameWriter.Rent(1024);
        w.Write(table);
        w.SendControl(link, ControlOp.SlotTable);
    }

    // ------------------------------------------------------------------ allocation (the coordinator)

    private void Allocate(ClusterView view)
    {
        SlotTable table;
        lock (_gate)
        {
            if (!_reportsChanged && view == _plannedView || !AllReported(view) && ++_roundsCoordinating <= TakeOverRounds)
            {
                table = view.Table;
                goto broadcast;
            }

            (_reportsChanged, _plannedView) = (false, view);
            foreach (ulong reporter in _reports.Keys.ToArray()) // gone nodes' reports go
                if (!view.TryGetMember(reporter, out var m) || m.Status is not (MemberStatus.Up or MemberStatus.Suspect))
                    _reports.Remove(reporter);
            var current = view.Table;
            var overrides = SlotPlanner.Plan(view, _reports);
            // A table another coordinator wrote is replaced by this one's even when the plan is the same: its version
            // goes above every version seen, so no member keeps another coordinator's table at an equal version.
            if (!Same(overrides, current.Overrides) || current.Coordinator != node.Id && current.Version > 0)
            {
                table = new SlotTable { Version = node.Membership.HighestTableVersion + 1, Coordinator = node.Id, Overrides = overrides };
                _sentTo.Clear();
            }
            else table = current;
        }

        if (table != view.Table) node.Membership.Apply(table);
        broadcast:
        Broadcast(view, table);
    }

    /// <summary>Whether every Up member that hosts actor types reported to this coordinator.</summary>
    private bool AllReported(ClusterView view)
    {
        foreach (var member in view.Members)
            if (member.Status == MemberStatus.Up && !_reports.ContainsKey(member.NodeId) && HostsActors(member)) return false;
        return true;
    }

    private static bool HostsActors(ClusterMember member)
    {
        foreach (var handler in member.Handlers)
            if (handler.Actor) return true;
        return false;
    }

    private static bool Same(SlotOverride[] a, SlotOverride[] b) => a.AsSpan().SequenceEqual(b);

    /// <summary>Send the table to the members that do not have it yet, and to everyone every few rounds.</summary>
    private void Broadcast(ClusterView view, SlotTable table)
    {
        if (table.Version == 0) return;
        if (++_rounds % ResendRounds == 0) lock (_gate) _sentTo.Clear(); // a table lost with a link is sent again
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
