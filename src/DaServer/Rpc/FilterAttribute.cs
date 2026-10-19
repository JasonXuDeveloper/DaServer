namespace DaServer.Rpc;

/// <summary>
/// A dispatch-stage filter, registered by the generated registration of its assembly (no startup code): on a static
/// method <c>RpcStatus Name(in Context context, in TMessage message)</c>, it runs for messages of
/// that type; on a class implementing <see cref="IMessageFilter"/> with a parameterless constructor, for every message.
/// <see cref="RpcStatus.Ok"/> lets the message through; any other status refuses it (<see cref="MessageFilters"/>).
/// A wrong shape is DAS021.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
public sealed class FilterAttribute : Attribute;
