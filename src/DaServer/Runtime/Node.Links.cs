namespace DaServer.Runtime;

// Node links: the authenticated handshake on a connection (Challenge, Hello, Resume) and the one link per node,
// found by its card's address or by its introduction. Routing (Node.Routing.cs) only picks a link.
sealed partial class Node
{
    private readonly ConcurrentDictionary<(string Host, int Port), PeerLink> _links = new();
    private PeerLink[] _linkList = [];
    private readonly ConcurrentDictionary<ulong, PeerLink> _introduced = new(); // node id -> link, from Hellos
    private readonly LinkAuth _linkAuth;

    /// <summary>An accepted connection that did not start with an authentic Hello of this cluster.</summary>
    private static readonly InvalidOperationException NotIntroduced = new("Not introduced by an authentic Hello");

    /// <summary>
    /// The dialing node's first frame on a node link connection, answering the accepting node's
    /// <paramref name="challenge"/>: who we are, so the peer answers through its own link to us, and which stream
    /// continues on it.
    /// </summary>
    internal byte[] HelloFrame(long streamId, ReadOnlySpan<byte> challenge)
    {
        var self = Membership.Self;
        var w = FrameWriter.Rent();
        BinaryPrimitives.WriteInt64LittleEndian(w.GetSpan(sizeof(long)), ControlOp.Link.Packed); // readable by any build
        w.Advance(sizeof(long));
        w.Write(new LinkHello
        {
            Cluster = Options.ClusterName, Host = self.Host, Port = self.Port, StreamId = streamId,
            Proof = _linkAuth.Prove(Id, streamId, self.Host, self.Port, challenge),
        });
        w.SetHeader(FrameKind.Control, 0, ControlOp.Hello, (long)Id);
        var frame = w.Frame.ToArray(); // once per connection
        w.Dispose();
        return frame;
    }

    /// <summary>A connection was accepted: its first frame is a fresh challenge, which the dialer's Hello must prove.</summary>
    private Connection Accept(System.Net.Sockets.Socket socket)
    {
        var connection = new Connection(socket, ClusterHandler, ConnectionOptions.ForNodeLinks());
        var challenge = LinkAuth.NewChallenge();
        connection.Tag = challenge; // until the Hello: then the link it belongs to
        var w = FrameWriter.Rent();
        w.WriteRaw(challenge);
        w.SendControl(connection, ControlOp.Challenge, ControlOp.Link.Packed);
        return connection;
    }

    /// <summary>A node named itself on an accepted connection: our replies to it go through our link to it.</summary>
    private void OnHello(Connection connection, ulong nodeId, ReadOnlySpan<byte> payload)
    {
        // The levels come first, as raw bytes: a build that cannot read this one's encoding is still named.
        var levels = payload.Length < sizeof(long) ? default : ProtocolLevels.Of(BinaryPrimitives.ReadInt64LittleEndian(payload));
        if (!levels.Overlaps(ControlOp.Link))
        {
            Log.LinkRefused(Logger, connection.Remote?.Address.ToString() ?? "?", connection.Remote?.Port ?? 0, nodeId,
                $"link protocol {levels}, this node speaks {ControlOp.Link}");
            connection.Close(NotIntroduced);
            return;
        }

        LinkHello hello;
        try
        {
            hello = NinoDeserializer.Deserialize<LinkHello>(payload[sizeof(long)..]);
        }
        catch (Exception)
        {
            connection.Close(NotIntroduced); // not a node of this protocol
            return;
        }

        var (cluster, host, port, streamId) = (hello.Cluster, hello.Host, hello.Port, hello.StreamId);
        string? refusal = cluster != Options.ClusterName ? $"cluster '{cluster}', this node is in '{Options.ClusterName}'"
            : _linkAuth.Verify(nodeId, streamId, host, port, connection.Tag as byte[], hello.Proof);
        if (refusal != null)
        {
            Log.LinkRefused(Logger, host, port, nodeId, refusal);
            connection.Close(NotIntroduced);
            return;
        }

        var link = GetLink(host, port);
        if (!View.TryGetMember(nodeId, out _))
        {
            _introduced[nodeId] = link; // listed: the view serves it
            // A view listing it may have been published between the check and the write (OnViewChanged then found
            // nothing to remove): the entry would outlive the member and route to whoever takes its address next.
            if (View.TryGetMember(nodeId, out _)) _introduced.TryRemove(nodeId, out _);
        }
        link.OnInboundHello(connection, streamId);
        _slots.OnLinked(link);
    }

    /// <summary>Tests: the next this many handoff records are lost on the way (as a link restarting its stream loses them).</summary>
    internal int LoseRecords;

    /// <summary>Tests: how many links reach node <paramref name="nodeId"/> (one per node).</summary>
    internal int LinksTo(ulong nodeId)
    {
        int count = 0;
        foreach (var link in Volatile.Read(ref _linkList))
            if (link.PeerId == nodeId) count++;
        return count;
    }

    /// <summary>Tests: whether this node keeps the link node <paramref name="nodeId"/> introduced itself on.</summary>
    internal bool Introduced(ulong nodeId) => _introduced.ContainsKey(nodeId);

    /// <summary>
    /// The link to node <paramref name="nodeId"/>: from the view, or, before gossip lists it, from the Hello it introduced
    /// itself with (a client's call or a handoff record can reach a node before its view lists the sender; the answers,
    /// access changes and pushes they cause must still reach the sender); null for a dead or unknown node. The view is
    /// read again after a missed introduction: a view listing the node replaces the introduction, and may have done so
    /// between the two reads (an answer dropped there left the sender waiting on its copy).
    /// </summary>
    internal PeerLink? LinkTo(ulong nodeId)
    {
        if (View.TryGetMember(nodeId, out var member)) return member.Status != MemberStatus.Dead ? GetLink(member) : null;
        if (_introduced.TryGetValue(nodeId, out var link)) return link;
        return View.TryGetMember(nodeId, out member) && member.Status != MemberStatus.Dead ? GetLink(member) : null;
    }

    /// <summary>Our Hello was answered on <paramref name="connection"/>: the link resumes its stream.</summary>
    private static void OnResume(Connection connection, long received, ReadOnlySpan<byte> payload)
    {
        if (connection.Tag is not PeerLink link) return;
        link.OnResume(connection, received, NinoDeserializer.Deserialize<ulong>(payload));
    }

    /// <summary>The link to <paramref name="member"/> (cached on the member: no lookup on the send path).</summary>
    internal PeerLink GetLink(ClusterMember member) => member.Link ??= LinkFor(member);

    /// <summary>
    /// One link per node: a link that already reaches the member under another address (a seed spelled otherwise:
    /// "localhost" for 127.0.0.1, a name for the address on its card) serves it under its card's address too. Two
    /// links would each say hello on the member's one inbound slot for this node and take it from each other.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private PeerLink LinkFor(ClusterMember member)
    {
        if (_links.TryGetValue((member.Host, member.Port), out var link)) return link;
        foreach (var other in Volatile.Read(ref _linkList))
            if (other.PeerId == member.NodeId)
                return _links.GetOrAdd((member.Host, member.Port), other);
        return AddLink(member.Host, member.Port);
    }

    internal PeerLink GetLink(string host, int port) =>
        _links.TryGetValue((host, port), out var link) ? link : AddLink(host, port);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PeerLink AddLink(string host, int port)
    {
        lock (_links)
        {
            if (_links.TryGetValue((host, port), out var link)) return link;
            link = new PeerLink(this, host, port);
            _links[(host, port)] = link;
            _linkList = [.. _linkList, link]; // copy on write: the maintenance tick walks it without allocating
            return link;
        }
    }
}
