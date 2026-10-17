namespace DaServer.Cluster;

/// <summary>
/// Gossip-based membership with heartbeat-staleness failure detection (no clock synchronisation needed):
/// every <see cref="NodeOptions.GossipInterval"/> a node bumps its heartbeat and exchanges its member table with up to
/// three random peers (push-pull) as fixed-size <see cref="MemberPulse"/>s; full cards travel only to nodes that ask for
/// them, so a steady-state round allocates nothing. A member whose heartbeat has not advanced for SuspectAfter becomes Suspect, after
/// DeadAfter Dead; a node that learns it is suspected refutes by bumping its incarnation.
/// </summary>
internal sealed partial class Membership
{
    private const int Fanout = 3;

    private readonly Node _node;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Entry> _members = new();
    private ClusterView _view;
    private ClusterMember _self = new();
    private bool _changed;
    private List<(string Host, int Port)>? _seeds;
    private readonly ClusterMember[] _targets = new ClusterMember[Fanout];
    private int _inRound;
    private long _nextRoundMs; // the node's maintenance tick runs a round once this passed

    /// <summary>Tests: a partition. Gossip to these cluster ports is dropped (both sides set it for the other).</summary>
    internal HashSet<int>? BlockedPorts;
    private long _seedsResolvedAt;

    private static readonly TimeSpan SeedRefresh = TimeSpan.FromSeconds(5);

    private sealed class Entry(ClusterMember info, long lastProgress)
    {
        public ClusterMember Info = info;
        public long LastProgress = lastProgress; // Platform.Milliseconds of the last heartbeat/status change we saw
    }

    public Membership(Node node)
    {
        _node = node;
        _view = new ClusterView([]);
    }

    public ClusterView View => Volatile.Read(ref _view);

    public ClusterMember Self => Volatile.Read(ref _self);

    public void Start(string host, int port)
    {
        var handlers = _node.AdvertisedHandlers();
        var (pulseKeys, entries) = _node.FeatureCard();
        lock (_gate)
        {
            _self = new ClusterMember
            {
                NodeId = _node.Id,
                Name = _node.Options.Name,
                Host = host,
                Port = port,
                Scopes = _node.AdvertisedScopes(),
                PulseKeys = pulseKeys,
                Entries = entries,
                Handlers = handlers,
                Status = MemberStatus.Up,
                UpSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            _members[_node.Id] = new Entry(_self, Platform.Milliseconds);
            PublishView();
        }
    }

    /// <summary>Announce Leaving and push it to every known member immediately.</summary>
    public void Leave()
    {
        lock (_gate)
        {
            UpdateSelf(MemberStatus.Leaving, _self.Incarnation);
            PublishView();
        }

        foreach (var member in View.Members)
            if (member.NodeId != _node.Id && member.Status != MemberStatus.Dead)
                SendPulses(_node.GetLink(member));
    }

    /// <summary>
    /// The node's maintenance tick (its timer thread): a gossip round once per <see cref="NodeOptions.GossipInterval"/>.
    /// A tick that ran <paramref name="pausedMs"/> late means this process was paused (GC, a starved CPU, a stopped
    /// container): the silence of the others since is ours, not theirs. Their progress clocks move forward by the pause,
    /// so no member is suspected or declared dead for it; real failures still surface a few rounds later.
    /// </summary>
    internal void Tick(long nowMs, long pausedMs)
    {
        if (pausedMs > 0)
        {
            lock (_gate)
                foreach (var entry in _members.Values) entry.LastProgress += pausedMs;
            Log.RoundLate(_node.Logger, pausedMs);
        }

        if (nowMs < _nextRoundMs) return;
        _nextRoundMs = nowMs + Ms(_node.Options.GossipInterval);
        Round();
    }

    /// <summary>One gossip round.</summary>
    private void Round()
    {
        if (Interlocked.Exchange(ref _inRound, 1) != 0) return; // the previous round is still sending
        try
        {
            int targets;
            lock (_gate)
            {
                _self.Heartbeat++;
                _self.Load = _node.TotalQueueLength();
                _node.ReadPulses(_self.PulseValues);
                DetectFailures();
                if (_changed) PublishView();
                targets = PickTargets();
            }

            for (int i = 0; i < targets; i++)
            {
                SendPulses(_node.GetLink(_targets[i]));
                _targets[i] = null!;
            }

            // Alone (or still joining): keep contacting the seeds until someone answers.
            if (targets == 0 || View.UpMembers.Length <= 1)
                foreach (var (host, port) in SeedEndPoints())
                    SendPulses(_node.GetLink(host, port));
        }
        catch (Exception e)
        {
            Log.GossipFailed(_node.Logger, e);
        }
        finally
        {
            Volatile.Write(ref _inRound, 0);
        }
    }

    /// <summary>Tests: age every member's progress as a pause of this process of <paramref name="ms"/> would.</summary>
    internal void Age(long ms)
    {
        lock (_gate)
            foreach (var entry in _members.Values) entry.LastProgress -= ms;
    }

    /// <summary><paramref name="time"/> in milliseconds (the progress clocks' unit).</summary>
    private static long Ms(TimeSpan time) => (long)time.TotalMilliseconds;

    private void DetectFailures()
    {
        long now = Platform.Milliseconds;
        long suspect = Ms(_node.Options.SuspectAfter), dead = Ms(_node.Options.DeadAfter);
        List<ulong>? remove = null;
        bool died = false;

        foreach (var (id, entry) in _members)
        {
            if (id == _node.Id) continue;
            long silent = now - entry.LastProgress;
            var status = entry.Info.Status;

            if (status == MemberStatus.Dead || status == MemberStatus.Leaving)
            {
                if (silent > dead * 3) (remove ??= new()).Add(id); // forget after the news had time to spread
            }
            else if (silent > dead)
            {
                SetStatus(entry, MemberStatus.Dead);
                Log.MemberDead(_node.Logger, entry.Info.Name);
                died = true;
            }
            else if (silent > suspect && status != MemberStatus.Suspect)
            {
                SetStatus(entry, MemberStatus.Suspect);
                Log.MemberSuspect(_node.Logger, entry.Info.Name);
            }
        }

        if (died && _node.Options.KeepMajority) CheckMajority();
        if (remove == null) return;
        foreach (var id in remove) _members.Remove(id);
        _changed = true;
    }

    /// <summary>
    /// Keep-majority downing (ADR-064), when members just died: this side keeps running only if it reaches more of the
    /// cluster than it lost (a tie keeps the side with the lowest node id). Dead members count until they are forgotten;
    /// members that left do not count. Under the gate.
    /// </summary>
    private void CheckMajority()
    {
        int reachable = 0, lost = 0;
        ulong lowestReachable = ulong.MaxValue, lowestLost = ulong.MaxValue;
        foreach (var entry in _members.Values)
        {
            var info = entry.Info;
            if (info.Status == MemberStatus.Leaving) continue;
            if (info.Status == MemberStatus.Dead)
            {
                lost++;
                lowestLost = Math.Min(lowestLost, info.NodeId);
            }
            else
            {
                reachable++;
                lowestReachable = Math.Min(lowestReachable, info.NodeId);
            }
        }

        if (reachable + lost < 3) return; // a crash and a partition look the same: keep running
        if (reachable > lost || (reachable == lost && lowestReachable < lowestLost)) return;
        _ = Task.Run(() => _node.DownAsync(reachable, lost)); // not under the gate: stopping closes membership
    }

    private void SetStatus(Entry entry, MemberStatus status)
    {
        var info = entry.Info.Clone();
        info.Status = status;
        entry.Info = info;
        _changed = true;
    }

    /// <summary>Up to <see cref="Fanout"/> random live members into <see cref="_targets"/> (reservoir sampling).</summary>
    private int PickTargets()
    {
        int seen = 0;
        foreach (var (id, entry) in _members)
        {
            if (id == _node.Id || entry.Info.Status is not (MemberStatus.Up or MemberStatus.Suspect or MemberStatus.Joining))
                continue;
            int slot = seen < Fanout ? seen : Random.Shared.Next(seen + 1);
            if (slot < Fanout) _targets[slot] = entry.Info;
            seen++;
        }

        return Math.Min(seen, Fanout);
    }

    // ------------------------------------------------------------------ wire

    /// <summary>Our table as pulses, written straight into a pooled frame.</summary>
    private FrameWriter PulseFrame()
    {
        FrameWriter w;
        Span<byte> pulse = stackalloc byte[MemberPulse.Size];
        lock (_gate)
        {
            w = FrameWriter.Rent(FrameHeader.Size + _members.Count * MemberPulse.Size);
            foreach (var entry in _members.Values)
            {
                MemberPulse.Of(entry.Info).Write(pulse);
                w.WriteRaw(pulse);
            }
        }

        return w;
    }

    private void SendPulses(PeerLink link)
    {
        if (BlockedPorts is { } blocked && blocked.Contains(link.Port)) return;
        PulseFrame().SendControl(link, ControlOp.Gossip);
    }

    // ------------------------------------------------------------------ helpers (call under _gate)

    /// <summary>Publish a copy of ourselves with a new status or incarnation (no delegate: refutation is per pulse).</summary>
    private void UpdateSelf(MemberStatus status, int incarnation)
    {
        var self = _self.Clone();
        self.Status = status;
        self.Incarnation = incarnation;
        Volatile.Write(ref _self, self);
        _members[_node.Id] = new Entry(self, Platform.Milliseconds);
        _changed = true; // the view must hold the new object (pulse fields are updated in place on it)
    }

    private void PublishView()
    {
        var members = new List<ClusterMember>(_members.Count);
        foreach (var entry in _members.Values) members.Add(entry.Info);
        var old = _view;
        var view = new ClusterView(members, _table);
        Volatile.Write(ref _view, view);
        _changed = false;

        if (!SameRouting(old, view) || old.Table != view.Table)
            _node.OnViewChanged();
    }

    private SlotTable _table = SlotTable.Empty;

    /// <summary>
    /// Take the coordinator's slot table (ADR-079) when it is newer than the one applied: the view is rebuilt with it and
    /// the shards move what it moves. Any thread.
    /// </summary>
    internal bool Apply(SlotTable table)
    {
        lock (_gate)
        {
            if (table.Version <= _table.Version) return false;
            _table = table;
            PublishView();
            return true;
        }
    }

    /// <summary>
    /// Did any member's status change (placement uses Up and Suspect members, client-facing nodes' sessions end when they are Dead)?
    /// Heartbeats alone do not count.
    /// </summary>
    private static bool SameRouting(ClusterView a, ClusterView b)
    {
        int count = 0;
        foreach (var m in a.Members)
        {
            count++;
            if (!b.TryGetMember(m.NodeId, out var other) || other.Status != m.Status) return false;
        }

        foreach (var _ in b.Members) count--;
        return count == 0;
    }

    /// <summary>
    /// Seed endpoints with DNS names expanded to every address (a Kubernetes headless service resolves to all pods),
    /// minus this node. Cached for <see cref="SeedRefresh"/> so a node waiting alone does not hammer DNS; only used
    /// while the node is alone, never on a hot path.
    /// </summary>
    private List<(string Host, int Port)> SeedEndPoints()
    {
        long now = Platform.Milliseconds;
        if (_seeds != null && now - _seedsResolvedAt < (long)SeedRefresh.TotalMilliseconds) return _seeds;

        var seeds = new List<(string, int)>();
        foreach (var seed in _node.Options.Seeds)
        {
            if (!HostPort.TryParse(seed, out var host, out var port)) continue;
            if (IPAddress.TryParse(host, out _) || host == "localhost")
            {
                if (!IsSelf(host, port)) seeds.Add((host, port));
                continue;
            }

            try
            {
                foreach (var address in Dns.GetHostAddresses(host))
                {
                    var ip = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
                    if (!IsSelf(ip, port)) seeds.Add((ip, port));
                }
            }
            catch (SocketException e)
            {
                Log.SeedUnresolved(_node.Logger, seed, e.SocketErrorCode);
            }
        }

        _seedsResolvedAt = now;
        return _seeds = seeds;
    }

    private bool IsSelf(string host, int port) =>
        port == _self.Port && (host == _self.Host || host == "localhost" && _self.Host == "127.0.0.1");
}
