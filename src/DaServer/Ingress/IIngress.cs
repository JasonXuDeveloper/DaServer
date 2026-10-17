namespace DaServer.Ingress;

/// <summary>
/// Lets messages from outside the cluster in (client connections, HTTP endpoints): with the origin the ingress
/// assigned (<c>Caller</c> in handlers; a client's own bytes never become it), a deadline capped at the node's
/// <see cref="NodeOptions.CallTimeout"/>, and the reply to the ingress's own sink. One hop: this node picks the owner
/// from its view and sends the message there. Raw messages are never deserialized on the way in.
/// </summary>
public interface IIngress
{
    /// <summary>
    /// A one-way message from outside, as received (<paramref name="payload"/>: the Nino-serialized message), to actor
    /// (<paramref name="target"/>, <paramref name="key"/>) or, with target 0, the service handling
    /// <paramref name="messageId"/>. Dropped when nothing hosts it (one-way delivery is at most once).
    /// </summary>
    void Send(uint messageId, uint target, long key, ReadOnlySpan<byte> payload, Origin origin);

    /// <summary>
    /// A request from outside, as received: the reply (status, payload) goes to <paramref name="replyTo"/> under
    /// <paramref name="callId"/>. Refused with <see cref="RpcStatus.Overloaded"/> before anything is sent when the
    /// target node's gossiped load is at <see cref="NodeOptions.IngressLoadLimit"/>.
    /// </summary>
    void Ask(uint messageId, uint target, long key, ReadOnlySpan<byte> payload, Origin origin, IReplySink replyTo,
        uint callId, TimeSpan budget);

    /// <summary>A typed request from outside to the actor at <paramref name="address"/> (endpoints that build the message).</summary>
    ValueTask<TReply> Ask<TAddr, TRequest, TReply>(TAddr address, in TRequest request, Origin origin, TimeSpan budget)
        where TAddr : struct, IAddress where TRequest : struct, IRequest<TReply>;

    /// <summary>A typed request from outside to the service handling <typeparamref name="TRequest"/>.</summary>
    ValueTask<TReply> Ask<TRequest, TReply>(in TRequest request, Origin origin, TimeSpan budget) where TRequest : struct, IRequest<TReply>;
}
