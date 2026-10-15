namespace DaServer.Rpc;

/// <summary>
/// Checks a message on the shard right before its handler runs (dispatch stage; the edge's stage is
/// <c>IIngressFilter</c>): <see cref="RpcStatus.Ok"/> lets it through, any other status refuses it (a request gets the
/// status as its reply, a one-way message is dropped). <paramref name="target"/> is the actor or service instance it is for.
/// </summary>
public delegate RpcStatus MessageFilter<T>(object target, in T message, in CallContext context) where T : struct;

/// <summary>A filter for every message type (authorization, metrics): generic, so it reads messages without boxing.</summary>
public interface IMessageFilter
{
    RpcStatus Check<T>(object target, in T message, in CallContext context) where T : struct;
}

/// <summary>
/// Process-wide dispatch-stage filters, registered at startup (before nodes start) and run by every generated
/// dispatcher of servers: first the filters of every message, then those of the message's type, in registration order.
/// </summary>
public static class MessageFilters
{
    private static readonly Lock Gate = new();
    internal static IMessageFilter[] All = [];

    /// <summary>Run <paramref name="filter"/> for every message.</summary>
    public static void Register(IMessageFilter filter)
    {
        lock (Gate) All = [.. All, filter];
    }

    /// <summary>Run <paramref name="filter"/> for messages of type <typeparamref name="T"/>.</summary>
    public static void Register<T>(MessageFilter<T> filter) where T : struct
    {
        lock (Gate) MessageFilters<T>.Typed = [.. MessageFilters<T>.Typed, filter];
    }
}

/// <summary>The filters of message type <typeparamref name="T"/> (used by generated dispatchers).</summary>
public static class MessageFilters<T> where T : struct
{
    internal static MessageFilter<T>[] Typed = [];

    /// <summary>
    /// Whether the message may reach its handler; when not, a request was answered with the refusing status. Without
    /// filters this is two array length checks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Admit(object target, in T message, in CallContext context)
    {
        if (MessageFilters.All.Length == 0 && Typed.Length == 0) return true;
        return Check(target, in message, in context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Check(object target, in T message, in CallContext context)
    {
        foreach (var filter in MessageFilters.All)
        {
            var status = filter.Check(target, in message, in context);
            if (status != RpcStatus.Ok) return Refuse(status, in context);
        }

        foreach (var filter in Typed)
        {
            var status = filter(target, in message, in context);
            if (status != RpcStatus.Ok) return Refuse(status, in context);
        }

        return true;
    }

    private static bool Refuse(RpcStatus status, in CallContext context)
    {
        context.ReplyError(status, null);
        return false;
    }
}
