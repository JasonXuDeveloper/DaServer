namespace DaServer.Cluster;

/// <summary>Control operations carried in <see cref="FrameKind.Control"/> frames (MessageId field).</summary>
internal static class ControlOp
{
    /// <summary>Push gossip: payload = the sender's table as <see cref="Pulse"/>s; answered with <see cref="GossipReply"/>.</summary>
    public const uint Gossip = 1;

    /// <summary>Pull half of push-pull gossip: payload = <see cref="Pulse"/>s.</summary>
    public const uint GossipReply = 2;

    /// <summary>
    /// First frame of every node link connection: Key = sender node id, payload = its advertised host and port and the
    /// id of its stream to us. The receiver
    /// answers everything that arrives on the connection through its own link to that node (see <see cref="PeerLink.SendReply"/>).
    /// </summary>
    public const uint Hello = 3;

    /// <summary>
    /// Answer to a <see cref="Hello"/> on the same connection: Key = bytes of the sender's stream this node processed
    /// (the sender replays the rest, ADR-008), payload = this node's id (a different id means the peer restarted).
    /// </summary>
    public const uint Resume = 5;

    /// <summary>Receiver to sender on a link connection: Key = bytes of the stream processed so far.</summary>
    public const uint Acknowledge = 6;

    /// <summary>Gossip named nodes we have no card for: payload = their ids (ulong each); answered with <see cref="Cards"/>.</summary>
    public const uint CardRequest = 7;

    /// <summary>Full member descriptions: payload = ClusterMember[] (sent when a node meets another, not every round).</summary>
    public const uint Cards = 8;

    /// <summary>Client to gateway (a call): which gateway should I use? Answer: a <see cref="GatewayEndpoint"/>.</summary>
    public const uint PickGateway = 4;

    // Session operations (to the session's gateway): Key = session id, a call id when acknowledged,
    // payload = scope (uint: actor handler type id for Bind/Unbind) + value (long: actor key or group id).

    /// <summary>Gateway: bind a session to an actor (scope = handler type id, value = key).</summary>
    public const uint Bind = 10;

    /// <summary>Gateway: remove the session's binding to an actor handler type.</summary>
    public const uint Unbind = 11;

    /// <summary>Gateway: add a session to a group (payload = group id).</summary>
    public const uint JoinGroup = 12;

    /// <summary>Gateway: remove a session from a group (payload = group id).</summary>
    public const uint LeaveGroup = 13;

    /// <summary>Gateway: close a session (reported to its actors at once, no reconnect grace).</summary>
    public const uint Disconnect = 14;

    // Client sessions (client and gateway): reconnect tickets (ADR-036).

    /// <summary>
    /// Gateway to client: payload = the session's current reconnect ticket (after every binding change). Client to
    /// gateway (no payload): send me a fresh one.
    /// </summary>
    public const uint Ticket = 15;

    /// <summary>Client to gateway (a request): payload = a reconnect ticket; resume the session it names here.</summary>
    public const uint ResumeSession = 16;

    /// <summary>Client to gateway (a request): the client closes on purpose (no reconnect grace).</summary>
    public const uint Goodbye = 17;
}

/// <summary>
/// Gossip-based membership with heartbeat-staleness failure detection (no clock synchronisation needed):
/// every <see cref="NodeOptions.GossipInterval"/> a node bumps its heartbeat and exchanges its member table with up to
/// three random peers (push-pull) as fixed-size <see cref="Pulse"/>s; full cards travel only to nodes that ask for
/// them, so a steady-state round allocates nothing. A member whose heartbeat has not advanced for SuspectAfter becomes Suspect, after
/// DeadAfter Dead; a node that learns it is suspected refutes by bumping its incarnation.
/// </summary>
internal sealed class Membership : IDisposable
{
    private const int Fanout = 3;

    private readonly Node _node;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Entry> _members = new();
    private ClusterView _view;
    private ClusterMember _self = new();
    private Timer? _timer;
    private bool _changed;
    private List<(string Host, int Port)>? _seeds;
    private readonly ClusterMember[] _targets = new ClusterMember[Fanout];
    private int _inRound;
    private long _seedsResolvedAt;

    private static readonly TimeSpan SeedRefresh = TimeSpan.FromSeconds(5);

    private sealed class Entry(ClusterMember info, long lastProgress)
    {
        public ClusterMember Info = info;
        public long LastProgress = lastProgress; // Stopwatch timestamp of the last heartbeat/status change we saw
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
        lock (_gate)
        {
            _self = new ClusterMember
            {
                NodeId = _node.Id,
                Name = _node.Options.Name,
                Host = host,
                Port = port,
                ClientPort = _node.ClientPort,
                ClientHost = _node.Options.ClientAdvertisedHost ?? host,
                Balancer = _node.Options.Balancer,
                Handlers = handlers,
                Status = MemberStatus.Up,
            };
            _members[_node.Id] = new Entry(_self, Stopwatch.GetTimestamp());
            PublishView();
        }

        int interval = (int)_node.Options.GossipInterval.TotalMilliseconds;
        _timer = new Timer(static s => ((Membership)s!).Round(), this, 0, interval);
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

    public void Dispose() => _timer?.Dispose();

    /// <summary>One gossip round (timer thread).</summary>
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
                _self.Sessions = _node.SessionCount;
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
            _node.Logger.LogError(e, "Gossip round failed");
        }
        finally
        {
            Volatile.Write(ref _inRound, 0);
        }
    }

    private void DetectFailures()
    {
        long now = Stopwatch.GetTimestamp();
        long suspect = (long)(_node.Options.SuspectAfter.TotalSeconds * Stopwatch.Frequency);
        long dead = (long)(_node.Options.DeadAfter.TotalSeconds * Stopwatch.Frequency);
        List<ulong>? remove = null;

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
                _node.Logger.LogWarning("Member {Member} is dead", entry.Info.Name);
            }
            else if (silent > suspect && status != MemberStatus.Suspect)
            {
                SetStatus(entry, MemberStatus.Suspect);
                _node.Logger.LogInformation("Member {Member} is suspect", entry.Info.Name);
            }
        }

        if (remove == null) return;
        foreach (var id in remove) _members.Remove(id);
        _changed = true;
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
        Span<byte> pulse = stackalloc byte[Pulse.Size];
        lock (_gate)
        {
            w = FrameWriter.Rent(FrameHeader.Size + _members.Count * Pulse.Size);
            foreach (var entry in _members.Values)
            {
                Pulse.Of(entry.Info).Write(pulse);
                w.WriteRaw(pulse);
            }
        }

        return w;
    }

    private void SendPulses(PeerLink link)
    {
        PulseFrame().SendControl(link, ControlOp.Gossip);
    }

    /// <summary>Handle a gossip control frame (IO thread). Answers go back on the same connection.</summary>
    public void OnGossip(Connection connection, uint op, ReadOnlySpan<byte> payload)
    {
        switch (op)
        {
            case ControlOp.Gossip:
            case ControlOp.GossipReply:
                if (MergePulses(payload)) RequestCards(connection, payload);
                if (op == ControlOp.Gossip)
                {
                    PulseFrame().SendControl(connection, ControlOp.GossipReply);
                }

                break;
            case ControlOp.CardRequest:
                SendCards(connection, payload);
                break;
            case ControlOp.Cards:
                var reader = new Reader(payload);
                NinoDeserializer.Deserialize(out ClusterMember[] cards, ref reader);
                MergeCards(cards);
                break;
        }
    }

    /// <summary>Apply pulses in place; true when some name a node we have no card for.</summary>
    private bool MergePulses(ReadOnlySpan<byte> pulses)
    {
        bool unknown = false;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            for (; pulses.Length >= Pulse.Size; pulses = pulses.Slice(Pulse.Size))
            {
                var pulse = Pulse.Read(pulses);
                if (pulse.NodeId == _node.Id)
                {
                    Refute(pulse.Status, pulse.Incarnation);
                    continue;
                }

                if (!_members.TryGetValue(pulse.NodeId, out var entry))
                {
                    unknown = true;
                    continue;
                }

                var info = entry.Info;
                if (!pulse.Supersedes(info)) continue;
                if (pulse.Heartbeat != info.Heartbeat || pulse.Incarnation != info.Incarnation) entry.LastProgress = now;
                if (pulse.Incarnation != info.Incarnation || pulse.Status != info.Status)
                {
                    // Routing-relevant: publish a copy (the view is rebuilt).
                    info = info.Clone();
                    info.Incarnation = pulse.Incarnation;
                    info.Status = pulse.Status;
                    entry.Info = info;
                    _changed = true;
                }

                info.Heartbeat = pulse.Heartbeat;
                info.Load = pulse.Load;
                if (info.Sessions != pulse.Sessions)
                {
                    info.Sessions = pulse.Sessions;
                    Volatile.Write(ref info.Directed, 0); // counted by the gateway now (approximately: hints)
                }
            }

            if (_changed) PublishView();
        }

        return unknown;
    }

    /// <summary>Ask the sender of <paramref name="pulses"/> for the cards of the nodes we do not know.</summary>
    private void RequestCards(Connection connection, ReadOnlySpan<byte> pulses)
    {
        var w = FrameWriter.Rent();
        Span<byte> id = stackalloc byte[sizeof(ulong)];
        lock (_gate)
        {
            for (; pulses.Length >= Pulse.Size; pulses = pulses.Slice(Pulse.Size))
            {
                ulong nodeId = Pulse.Read(pulses).NodeId;
                if (nodeId == _node.Id || _members.ContainsKey(nodeId)) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(id, nodeId);
                w.WriteRaw(id);
            }
        }

        w.SendControl(connection, ControlOp.CardRequest);
    }

    private void SendCards(Connection connection, ReadOnlySpan<byte> ids)
    {
        var cards = new List<ClusterMember>();
        lock (_gate)
            for (; ids.Length >= sizeof(ulong); ids = ids.Slice(sizeof(ulong)))
                if (_members.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(ids), out var entry))
                    cards.Add(entry.Info);

        var w = FrameWriter.Rent(1024);
        var nino = w.Nino;
        NinoSerializer.Serialize(cards.ToArray(), ref nino);
        w.SendControl(connection, ControlOp.Cards);
    }

    private void MergeCards(ClusterMember[] incoming)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var member in incoming)
            {
                if (member.NodeId == _node.Id)
                {
                    Refute(member.Status, member.Incarnation);
                    continue;
                }

                if (!_members.TryGetValue(member.NodeId, out var entry))
                {
                    _members[member.NodeId] = new Entry(member, now);
                    _changed = true;
                    _node.Logger.LogInformation("Member {Member} joined ({Status})", member.Name, member.Status);
                    continue;
                }

                if (!member.Supersedes(entry.Info)) continue;
                entry.Info = member;
                entry.LastProgress = now;
                _changed = true;
            }

            if (_changed) PublishView();
        }
    }

    /// <summary>
    /// Someone thinks we are suspect/dead: refute with a higher incarnation. Dead means the cluster already moved our
    /// keys: remove our loaded actors first so they do not run beside the new ones. Under the gate.
    /// </summary>
    private void Refute(MemberStatus status, int incarnation)
    {
        if (status is not (MemberStatus.Suspect or MemberStatus.Dead) || incarnation < _self.Incarnation ||
            _self.Status != MemberStatus.Up)
            return;

        if (status == MemberStatus.Dead)
        {
            _node.Logger.LogWarning("Declared dead by the cluster: removed local actors before rejoining");
            foreach (var shard in _node.Shards) shard.RequestUnloadAll();
        }

        UpdateSelf(_self.Status, incarnation + 1);
    }

    // ------------------------------------------------------------------ helpers (call under _gate)

    /// <summary>Publish a copy of ourselves with a new status or incarnation (no delegate: refutation is per pulse).</summary>
    private void UpdateSelf(MemberStatus status, int incarnation)
    {
        var self = _self.Clone();
        self.Status = status;
        self.Incarnation = incarnation;
        Volatile.Write(ref _self, self);
        _members[_node.Id] = new Entry(self, Stopwatch.GetTimestamp());
        _changed = true; // the view must hold the new object (pulse fields are updated in place on it)
    }

    private void PublishView()
    {
        var members = new List<ClusterMember>(_members.Count);
        foreach (var entry in _members.Values) members.Add(entry.Info);
        var old = _view;
        var view = new ClusterView(members);
        Volatile.Write(ref _view, view);
        _changed = false;

        if (!SameRouting(old, view))
            _node.OnViewChanged();
    }

    /// <summary>
    /// Did any member's status change (placement uses Up and Suspect members, gateways' sessions end when they are Dead)?
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
        long now = Stopwatch.GetTimestamp();
        if (_seeds != null && Stopwatch.GetElapsedTime(_seedsResolvedAt, now) < SeedRefresh) return _seeds;

        var seeds = new List<(string, int)>();
        foreach (var seed in _node.Options.Seeds)
        {
            if (!TryParseEndPoint(seed, out var host, out var port)) continue;
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
                _node.Logger.LogDebug("Seed {Seed} does not resolve yet: {Error}", seed, e.SocketErrorCode);
            }
        }

        _seedsResolvedAt = now;
        return _seeds = seeds;
    }

    private bool IsSelf(string host, int port) =>
        port == _self.Port && (host == _self.Host || host == "localhost" && _self.Host == "127.0.0.1");

    internal static bool TryParseEndPoint(string text, out string host, out int port)
    {
        int colon = text.LastIndexOf(':');
        host = colon > 0 ? text[..colon] : text;
        return int.TryParse(colon > 0 ? text[(colon + 1)..] : "", out port);
    }
}
