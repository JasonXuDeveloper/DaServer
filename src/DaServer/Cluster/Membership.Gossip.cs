namespace DaServer.Cluster;

// Incoming gossip: pulses merged in place, cards asked for and merged, suspicion refuted.
internal sealed partial class Membership
{
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
                MergeCards(NinoDeserializer.Deserialize<ClusterMember[]>(payload));
                break;
        }
    }

    /// <summary>Apply pulses in place; true when some name a node we have no card for.</summary>
    private bool MergePulses(ReadOnlySpan<byte> pulses)
    {
        bool unknown = false;
        lock (_gate)
        {
            long now = Platform.Milliseconds;
            for (; pulses.Length >= MemberPulse.Size; pulses = pulses.Slice(MemberPulse.Size))
            {
                var pulse = MemberPulse.Read(pulses);
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
                pulse.CopySlots(info.PulseValues);
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
            for (; pulses.Length >= MemberPulse.Size; pulses = pulses.Slice(MemberPulse.Size))
            {
                ulong nodeId = MemberPulse.Read(pulses).NodeId;
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
        w.Write(cards.ToArray());
        w.SendControl(connection, ControlOp.Cards);
    }

    private void MergeCards(ClusterMember[] incoming)
    {
        lock (_gate)
        {
            long now = Platform.Milliseconds;
            foreach (var member in incoming)
            {
                if (member.NodeId == _node.Id)
                {
                    Refute(member.Status, member.Incarnation);
                    continue;
                }

                if (!_members.TryGetValue(member.NodeId, out var entry))
                {
                    _members[member.NodeId] = entry = new Entry(member, now);
                    _changed = true;
                    Log.MemberJoined(_node.Logger, member.Name, member.Status);
                }
                else
                {
                    if (!member.Supersedes(entry.Info)) continue;
                    entry.Info = member;
                    entry.LastProgress = now;
                    _changed = true;
                }

                Replaced(entry);
            }

            if (_changed) PublishView();
        }
    }

    /// <summary>
    /// One address holds one process: of two live members at one address the one that started earlier is gone (it
    /// crashed and restarted there with a new id), so it is dead at once instead of keeping its keys while suspected,
    /// and a card at this node's own address is dead. Links are per address: the older id must not route keys to the
    /// new process nor take its stream with it. Under the gate.
    /// </summary>
    private void Replaced(Entry entry)
    {
        var card = entry.Info;
        if (card.Status == MemberStatus.Dead) return;
        if (card.Host == _self.Host && card.Port == _self.Port)
        {
            SetStatus(entry, MemberStatus.Dead);
            return;
        }

        foreach (var other in _members.Values)
        {
            var info = other.Info;
            if (info.NodeId == card.NodeId || info.NodeId == _node.Id || info.Status == MemberStatus.Dead ||
                info.Host != card.Host || info.Port != card.Port)
                continue;
            var older = info.UpSince < card.UpSince ? other : entry;
            SetStatus(older, MemberStatus.Dead);
            Log.MemberDead(_node.Logger, older.Info.Name);
            if (older == entry) return;
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
            Log.DeclaredDead(_node.Logger);
            foreach (var shard in _node.Shards) shard.RequestUnloadAll();
        }

        UpdateSelf(_self.Status, incarnation + 1);
    }
}
