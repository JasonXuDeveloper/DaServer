namespace DaServer.Tests;

/// <summary>Hand-made membership cards for tests of the view's routing and of the slot planner (no node runs).</summary>
internal static class Views
{
    /// <summary>Node <paramref name="id"/>'s card, handling <paramref name="handlers"/>.</summary>
    public static ClusterMember Member(ulong id, MemberStatus status, params HostedMessage[] handlers) =>
        new() { NodeId = id, Name = "n" + id, Host = "127.0.0.1", Port = (int)id, Status = status, Handlers = handlers };

    /// <summary>An Up node's card.</summary>
    public static ClusterMember Member(ulong id, params HostedMessage[] handlers) => Member(id, MemberStatus.Up, handlers);

    /// <summary><paramref name="message"/> handled by <paramref name="handlerType"/>, an actor type or a service.</summary>
    public static HostedMessage Handles(uint handlerType, bool actor, uint message) =>
        new() { MessageId = message, HandlerTypeId = handlerType, Actor = actor };
}
