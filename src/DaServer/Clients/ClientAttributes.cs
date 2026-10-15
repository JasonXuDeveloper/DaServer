namespace DaServer.Clients;

/// <summary>
/// Clients may send this message (<c>[NinoType, Message, FromClient]</c>), through a client-facing node whose session is
/// its origin (<c>Caller</c>). Handled by a service: any node hosting it. Handled by an actor: the actor bound to the
/// session (<c>BindSession</c>), so a client never reaches another player's actor, unless <see cref="AnyId"/> is set.
/// Client-facing nodes accept no other message from clients.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class FromClientAttribute : Attribute
{
    /// <summary>
    /// To actors only: the client names the actor (<c>client.Send(roomId, new Say(..))</c>; public actors: rooms,
    /// leaderboards).
    /// </summary>
    public bool AnyId { get; set; }
}

/// <summary>
/// Servers send this message to client sessions and groups (<c>Cluster.Send(session, m)</c>, <c>Send(group, m)</c>),
/// handled on clients by <c>[Handle]</c> methods. Only clients handle a message that is only <c>[ToClient]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class ToClientAttribute : Attribute;
