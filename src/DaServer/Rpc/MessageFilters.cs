using System.ComponentModel;

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
    RpcStatus Admit<T>(object target, in T message, in CallContext context) where T : struct;
}

/// <summary>
/// Dispatch-stage filters, registered at startup and run by every generated dispatcher of servers: first the filters
/// of every message, then those of the message's type, in registration order. Process-wide: they are the application's
/// policy, the same code on every node of a process (several nodes share a process only in tests), and the generated
/// dispatcher reads them from a static per message type, two length checks when there are none.
/// </summary>
public static class MessageFilters
{
    private static readonly Lock Gate = new();
    internal static IMessageFilter[] All = [];

    /// <summary>
    /// Run <paramref name="filter"/> for every message, on the shard right before the handler, whatever the message's
    /// origin, until the returned registration is disposed. Per-node admission of messages from outside, before they
    /// are routed, is <c>INodeContext.AddIngressFilter</c>.
    /// </summary>
    public static IDisposable Register(IMessageFilter filter)
    {
        lock (Gate) All = [.. All, filter];
        return new Registration(() => { lock (Gate) All = Array.FindAll(All, f => f != filter); });
    }

    /// <summary>Run <paramref name="filter"/> for messages of type <typeparamref name="T"/>, until the returned registration is disposed.</summary>
    public static IDisposable Register<T>(MessageFilter<T> filter) where T : struct
    {
        lock (Gate) MessageFilters<T>.Typed = [.. MessageFilters<T>.Typed, filter];
        return new Registration(() => { lock (Gate) MessageFilters<T>.Typed = Array.FindAll(MessageFilters<T>.Typed, f => f != filter); });
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}

/// <summary>The filters of message type <typeparamref name="T"/> (used by generated dispatchers).</summary>
[EditorBrowsable(EditorBrowsableState.Never)] // generated code calls it
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
        return RunFilters(target, in message, in context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RunFilters(object target, in T message, in CallContext context)
    {
        foreach (var filter in MessageFilters.All)
        {
            var status = filter.Admit(target, in message, in context);
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
