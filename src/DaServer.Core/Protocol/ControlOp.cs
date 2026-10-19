namespace DaServer.Protocol;

/// <summary>Control operations carried in <see cref="FrameKind.Control"/> frames (MessageId field).</summary>
internal static class ControlOp
{
    /// <summary>The node link levels this build speaks (<see cref="ProtocolLevels"/>; 3: Nino 4.0.0-rc.6's strings).</summary>
    public static readonly ProtocolLevels Link = new(3, 3);

    /// <summary>The client protocol levels this build speaks (5: Nino 4.0.0-rc.6's strings).</summary>
    public static readonly ProtocolLevels Client = new(5, 5);

    /// <summary>Push gossip: payload = the sender's table as <c>MemberPulse</c>s; answered with <c>GossipReply</c>.</summary>
    public const uint Gossip = 1;

    /// <summary>Pull half of push-pull gossip: payload = <c>MemberPulse</c>s.</summary>
    public const uint GossipReply = 2;

    /// <summary>
    /// The dialing node's first frame, once the accepting node's <c>Challenge</c> came: Key = sender node id, payload = its
    /// <see cref="Link"/> levels (8 raw bytes, readable by any build), then its advertised host and port, the id of its
    /// stream to us and the proof. The receiver
    /// answers everything that arrives on the connection through its own link to that node (see <c>PeerLink.SendReply</c>).
    /// </summary>
    public const uint Hello = 3;

    /// <summary>
    /// Answer to a <c>Hello</c> on the same connection: Key = bytes of the sender's stream this node processed
    /// (the sender replays the rest, ADR-008), payload = this node's id (a different id means the peer restarted).
    /// </summary>
    public const uint Resume = 5;

    /// <summary>Receiver to sender on a link connection: Key = bytes of the stream processed so far.</summary>
    public const uint Acknowledge = 6;

    /// <summary>Gossip named nodes we have no card for: payload = their ids (ulong each); answered with <c>Cards</c>.</summary>
    public const uint CardRequest = 7;

    /// <summary>Full member descriptions: payload = ClusterMember[] (sent when a node meets another, not every round).</summary>
    public const uint Cards = 8;

    /// <summary>Coordinator to member: payload = the slot table (ADR-079); applied when newer. Ignored by older builds.</summary>
    public const uint SlotTable = 9;

    /// <summary>Member to coordinator: payload = the member's loaded slots per actor type (ADR-079). Ignored by older builds.</summary>
    public const uint SlotReport = 10;

    // Client sessions (client and client-facing node): resume tickets (ADR-036).

    /// <summary>
    /// Node to client: payload = the session's current resume ticket (after every grant change). Client to
    /// node (no payload): send me a fresh one.
    /// </summary>
    public const uint Ticket = 15;

    /// <summary>Client to node (a request): payload = a resume ticket; resume the session it names here.</summary>
    public const uint ResumeSession = 16;

    /// <summary>Client to node (a request): the client closes on purpose (no resume grace).</summary>
    public const uint Goodbye = 17;

    /// <summary>Either side, on a connection it has sent nothing on for a while (<see cref="Net.KeepAlive"/>): answer with <see cref="Pong"/>.</summary>
    public const uint Ping = 18;

    /// <summary>Answer to a <see cref="Ping"/> on the same connection: proof the peer still reads it.</summary>
    public const uint Pong = 19;

    /// <summary>
    /// Client to node, the first frame of every client connection (a call): Key = the client's <see cref="Client"/> levels
    /// (<see cref="ProtocolLevels.Packed"/>). Answered with an empty success, or, when the ranges do not overlap, an error
    /// naming both after which the node closes the connection.
    /// </summary>
    public const uint ClientHello = 20;

    /// <summary>
    /// Node to client: move (the connection is old, or the node is leaving). The client resumes its session on a new
    /// connection through its entry addresses while this one keeps working, then sends <see cref="Goodbye"/> as this
    /// connection's last frame and closes it once its calls are answered; the node closes it after
    /// <c>ClientConnectionOptions.RetireGrace</c> otherwise.
    /// </summary>
    public const uint Retire = 21;

    /// <summary>
    /// First frame of an accepted node link connection: Key = the accepting node's <see cref="Link"/> levels, payload = a
    /// fresh random challenge. The dialing node answers with its <c>Hello</c>, whose proof covers it (ADR-065), so a Hello
    /// is good on one connection only.
    /// </summary>
    public const uint Challenge = 22;
}
