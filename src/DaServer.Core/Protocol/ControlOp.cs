namespace DaServer.Protocol;

/// <summary>Control operations carried in <see cref="FrameKind.Control"/> frames (MessageId field).</summary>
internal static class ControlOp
{
    /// <summary>Push gossip: payload = the sender's table as <c>Pulse</c>s; answered with <c>GossipReply</c>.</summary>
    public const uint Gossip = 1;

    /// <summary>Pull half of push-pull gossip: payload = <c>Pulse</c>s.</summary>
    public const uint GossipReply = 2;

    /// <summary>
    /// First frame of every node link connection: Key = sender node id, payload = its advertised host and port and the
    /// id of its stream to us. The receiver
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

    /// <summary>Client to its node (a call): which node should I use? Answer: a <see cref="ClientEndpoint"/> (or stay).</summary>
    public const uint PickNode = 4;

    // Client sessions (client and client-facing node): reconnect tickets (ADR-036).

    /// <summary>
    /// Node to client: payload = the session's current reconnect ticket (after every grant change). Client to
    /// node (no payload): send me a fresh one.
    /// </summary>
    public const uint Ticket = 15;

    /// <summary>Client to node (a request): payload = a reconnect ticket; resume the session it names here.</summary>
    public const uint ResumeSession = 16;

    /// <summary>Client to node (a request): the client closes on purpose (no reconnect grace).</summary>
    public const uint Goodbye = 17;

    /// <summary>Either side, on a connection it has sent nothing on for a while (<see cref="Net.KeepAlive"/>): answer with <see cref="Pong"/>.</summary>
    public const uint Ping = 18;

    /// <summary>Answer to a <see cref="Ping"/> on the same connection: proof the peer still reads it.</summary>
    public const uint Pong = 19;
}
