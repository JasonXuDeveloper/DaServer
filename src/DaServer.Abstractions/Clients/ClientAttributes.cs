namespace DaServer;

/// <summary>
/// Clients may send this message (<c>[NinoType, Message, FromClient]</c>), through a client-facing node whose session is
/// its origin (<c>Caller</c>). Handled by a service: any node hosting it. Handled by an actor: the actor bound to the
/// session (<c>BindSession</c>), or the actor the client names (<c>client.Send(roomId, new Say(..))</c>) when that
/// actor granted the session (<c>GrantSession</c>): a client never reaches an actor that did not admit it.
/// Client-facing nodes accept no other message from clients.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class FromClientAttribute : Attribute;

/// <summary>
/// Servers send this message to client sessions and groups (<c>Cluster.Send(session, m)</c>, <c>Send(group, m)</c>),
/// handled on clients by <c>[Handle]</c> methods. Only clients handle a message that is only <c>[ToClient]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class ToClientAttribute : Attribute;
