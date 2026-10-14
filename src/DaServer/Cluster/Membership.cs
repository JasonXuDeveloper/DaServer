using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DaServer.Buffers;
using DaServer.Protocol;
using DaServer.Runtime;
using Microsoft.Extensions.Logging;
using Nino.Core;

namespace DaServer.Cluster;

/// <summary>Control operations carried in <see cref="FrameKind.Control"/> frames (MethodId field).</summary>
internal static class ControlOp
{
    /// <summary>Push gossip: payload = MemberInfo[]; the receiver answers with <see cref="GossipReply"/>.</summary>
    public const uint Gossip = 1;

    /// <summary>Pull half of push-pull gossip: payload = MemberInfo[].</summary>
    public const uint GossipReply = 2;

    /// <summary>
    /// First frame of every node link: Key = sender node id, payload = its advertised host and port. The receiver
    /// answers everything that arrives on the connection through its own link to that node (see <see cref="PeerLink.ReplySink"/>).
    /// </summary>
    public const uint Hello = 3;

    // Gateway operations: Key = session id, RequestId = contract id (Bind/Unbind), payload = one long value.

    /// <summary>Gateway: bind a session to an actor key of a contract (payload = actor key).</summary>
    public const uint Bind = 10;

    /// <summary>Gateway: remove the session's binding of a contract.</summary>
    public const uint Unbind = 11;

    /// <summary>Gateway: add a session to a group (payload = group id).</summary>
    public const uint JoinGroup = 12;

    /// <summary>Gateway: remove a session from a group (payload = group id).</summary>
    public const uint LeaveGroup = 13;

    /// <summary>Gateway: close a session.</summary>
    public const uint Kick = 14;
}

/// <summary>
/// Gossip-based membership with heartbeat-staleness failure detection (no clock synchronisation needed):
/// every <see cref="NodeOptions.GossipInterval"/> a node bumps its heartbeat and exchanges its member table with up to
/// three random peers (push-pull). A member whose heartbeat has not advanced for SuspectAfter becomes Suspect, after
/// DeadAfter Dead; a node that learns it is suspected refutes by bumping its incarnation.
/// </summary>
internal sealed class Membership : IDisposable
{
    private const int Fanout = 3;

    private readonly Node _node;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Entry> _members = new();
    private ClusterView _view;
    private MemberInfo _self = new();
    private long _version;
    private Timer? _timer;
    private bool _changed;
    private List<(string Host, int Port)>? _seeds;
    private long _seedsResolvedAt;

    private static readonly TimeSpan SeedRefresh = TimeSpan.FromSeconds(5);

    private sealed class Entry(MemberInfo info, long lastProgress)
    {
        public MemberInfo Info = info;
        public long LastProgress = lastProgress; // Stopwatch timestamp of the last heartbeat/status change we saw
    }

    public Membership(Node node)
    {
        _node = node;
        _view = new ClusterView([], 0);
    }

    public ClusterView View => Volatile.Read(ref _view);

    public MemberInfo Self => Volatile.Read(ref _self);

    public bool SelfIsUp => Self.Status == MemberStatus.Up;

    public void Start(string host, int port)
    {
        var (services, actors, handlers) = _node.AdvertisedContracts();
        lock (_gate)
        {
            _self = new MemberInfo
            {
                NodeId = _node.Id,
                Name = _node.Options.Name,
                Host = host,
                Port = port,
                ClientPort = _node.ClientPort,
                Services = services,
                Actors = actors,
                Handlers = handlers,
                Status = MemberStatus.Up,
            };
            _members[_node.Id] = new Entry(_self, Stopwatch.GetTimestamp());
            Publish();
        }

        int interval = (int)_node.Options.GossipInterval.TotalMilliseconds;
        _timer = new Timer(static s => ((Membership)s!).Round(), this, 0, interval);
    }

    /// <summary>Announce Leaving and push it to every known member immediately.</summary>
    public void Leave()
    {
        lock (_gate)
        {
            UpdateSelf(m => m.Status = MemberStatus.Leaving);
            Publish();
        }

        foreach (var member in View.Members)
            if (member.NodeId != _node.Id && member.Status != MemberStatus.Dead)
                SendGossip(_node.GetLink(member), ControlOp.Gossip);
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>One gossip round (timer thread).</summary>
    private void Round()
    {
        List<MemberInfo>? targets = null;
        try
        {
            lock (_gate)
            {
                UpdateSelf(m =>
                {
                    m.Heartbeat++;
                    m.Load = _node.TotalQueueLength();
                });
                DetectFailures();
                if (_changed) Publish();
                targets = PickTargets();
            }

            foreach (var member in targets)
                SendGossip(_node.GetLink(member), ControlOp.Gossip);

            // Alone (or still joining): keep contacting the seeds until someone answers.
            if (targets.Count == 0 || View.UpMembers.Length <= 1)
                foreach (var (host, port) in SeedEndPoints())
                    SendGossip(_node.GetLink(host, port), ControlOp.Gossip);
        }
        catch (Exception e)
        {
            _node.Logger.LogError(e, "Gossip round failed");
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

    private List<MemberInfo> PickTargets()
    {
        var candidates = new List<MemberInfo>();
        foreach (var (id, entry) in _members)
            if (id != _node.Id && entry.Info.Status is MemberStatus.Up or MemberStatus.Suspect or MemberStatus.Joining)
                candidates.Add(entry.Info);

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        if (candidates.Count > Fanout) candidates.RemoveRange(Fanout, candidates.Count - Fanout);
        return candidates;
    }

    // ------------------------------------------------------------------ wire

    private void SendGossip(PeerLink link, uint op)
    {
        MemberInfo[] table;
        lock (_gate)
        {
            table = new MemberInfo[_members.Count];
            int i = 0;
            foreach (var entry in _members.Values) table[i++] = entry.Info;
        }

        var w = FrameWriter.Rent(1024);
        var nino = w.Nino;
        NinoSerializer.Serialize(table, ref nino);
        w.SetHeader(FrameKind.Control, 0, op, 0);
        link.Send(w.Frame);
        w.Dispose();
    }

    /// <summary>Handle a gossip control frame (IO thread).</summary>
    public void OnGossip(Net.Connection connection, uint op, ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out MemberInfo[] incoming, ref reader);
        Merge(incoming);

        if (op != ControlOp.Gossip) return;

        // Pull half: answer with our table on the same connection.
        MemberInfo[] table;
        lock (_gate)
        {
            table = new MemberInfo[_members.Count];
            int i = 0;
            foreach (var entry in _members.Values) table[i++] = entry.Info;
        }

        var w = FrameWriter.Rent(1024);
        var nino = w.Nino;
        NinoSerializer.Serialize(table, ref nino);
        w.SetHeader(FrameKind.Control, 0, ControlOp.GossipReply, 0);
        connection.Send(w.Frame);
        w.Dispose();
    }

    private void Merge(MemberInfo[] incoming)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var member in incoming)
            {
                if (member.NodeId == _node.Id)
                {
                    // Someone thinks we are suspect/dead: refute with a higher incarnation. Dead means the cluster already
                    // moved our keys: retire our activations first so they do not run beside the new ones.
                    if (member.Status is MemberStatus.Suspect or MemberStatus.Dead && member.Incarnation >= _self.Incarnation &&
                        _self.Status == MemberStatus.Up)
                    {
                        if (member.Status == MemberStatus.Dead)
                        {
                            _node.Logger.LogWarning("Declared dead by the cluster: retiring local actors before rejoining");
                            foreach (var shard in _node.Shards) shard.RequestRetireAll();
                        }

                        int incarnation = member.Incarnation + 1;
                        UpdateSelf(m => m.Incarnation = incarnation);
                    }

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

                bool progressed = member.Heartbeat != entry.Info.Heartbeat || member.Incarnation != entry.Info.Incarnation;
                if (member.Status != entry.Info.Status) _changed = true; // routing-relevant change: republish the view
                entry.Info = member;
                if (progressed) entry.LastProgress = now;
            }

            if (_changed) Publish();
        }
    }

    // ------------------------------------------------------------------ helpers (call under _gate)

    private void UpdateSelf(Action<MemberInfo> change)
    {
        var self = _self.Clone();
        change(self);
        Volatile.Write(ref _self, self);
        _members[_node.Id] = new Entry(self, Stopwatch.GetTimestamp());
        if (self.Status != _view.TryGetSelfStatus(_node.Id)) _changed = true;
    }

    private void Publish()
    {
        var members = new List<MemberInfo>(_members.Count);
        foreach (var entry in _members.Values) members.Add(entry.Info);
        var old = _view;
        var view = new ClusterView(members, ++_version);
        Volatile.Write(ref _view, view);
        _changed = false;

        if (!SameRouting(old, view))
            _node.OnViewChanged();
    }

    /// <summary>Did the set of Up members (which drives placement) change?</summary>
    private static bool SameRouting(ClusterView a, ClusterView b)
    {
        if (a.UpMembers.Length != b.UpMembers.Length) return false;
        foreach (var m in a.UpMembers)
            if (!b.TryGetMember(m.NodeId, out var other) || other.Status != MemberStatus.Up)
                return false;
        return true;
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

internal static class ClusterViewExtensions
{
    public static MemberStatus? TryGetSelfStatus(this ClusterView view, ulong nodeId) =>
        view.TryGetMember(nodeId, out var m) ? m.Status : null;
}
