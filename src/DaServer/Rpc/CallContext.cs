using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DaServer.Buffers;
using DaServer.Protocol;
using Nino.Core;

namespace DaServer.Rpc;

/// <summary>Where the response of an incoming request goes (a connection, the local pending table, ...).</summary>
public interface IReplySink
{
    /// <summary>
    /// Send <paramref name="response"/> (payload already written) as the response to <paramref name="requestId"/>.
    /// Takes ownership of <paramref name="response"/>.
    /// </summary>
    void SendReply(uint requestId, RpcStatus status, FrameWriter response);
}

/// <summary>
/// Per-message context passed to generated dispatchers. A small struct: no allocation per call.
/// Inside a handler, <see cref="Current"/> exposes it until the handler's first <c>await</c>.
/// </summary>
public readonly struct CallContext
{
    [ThreadStatic] private static CallContext _current;

    public readonly IReplySink? Sink;
    public readonly uint RequestId;
    public readonly uint MessageId;

    /// <summary>Actor key of the target (0 for services).</summary>
    public readonly long Key;

    /// <summary>Client session that originated the call (through a gateway), or default for server-internal calls.</summary>
    public readonly SessionRef CallerSession;

    public CallContext(IReplySink? sink, uint requestId, uint messageId, long key, SessionRef caller)
    {
        Sink = sink;
        RequestId = requestId;
        MessageId = messageId;
        Key = key;
        CallerSession = caller;
    }

    /// <summary>Context of the call being dispatched on this thread (valid until the handler's first await).</summary>
    public static CallContext Current => _current;

    public bool ExpectsReply => RequestId != 0 && Sink != null;

    /// <summary>Runtime use: set the ambient context around a dispatch.</summary>
    public static CallContext Enter(in CallContext context)
    {
        var previous = _current;
        _current = context;
        return previous;
    }

    public static void Exit(in CallContext previous) => _current = previous;

    // ---------------- replies (used by generated dispatchers) ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask Reply<T>(ValueTask<T> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            ReplyValue(task.Result);
            return default;
        }

        return AwaitReply(task, this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask Reply(ValueTask task)
    {
        if (task.IsCompletedSuccessfully)
        {
            ReplyEmpty();
            return default;
        }

        return AwaitReply(task, this);
    }

    public void ReplyValue<T>(T value)
    {
        if (!ExpectsReply) return;
        var w = FrameWriter.Rent();
        try
        {
            var nino = w.Nino;
            NinoSerializer.Serialize(value, ref nino);
        }
        catch (Exception e)
        {
            w.Dispose();
            ReplyError(RpcStatus.Error, "Response serialization failed: " + e.Message);
            return;
        }

        Sink!.SendReply(RequestId, RpcStatus.Ok, w);
    }

    public void ReplyEmpty()
    {
        if (!ExpectsReply) return;
        Sink!.SendReply(RequestId, RpcStatus.Ok, FrameWriter.Rent(FrameHeader.Size));
    }

    public void ReplyError(RpcStatus status, string? message)
    {
        if (!ExpectsReply) return;
        var w = FrameWriter.Rent();
        var nino = w.Nino;
        NinoSerializer.Serialize(message, ref nino);
        Sink!.SendReply(RequestId, status, w);
    }

    public void ReplyException(Exception e)
    {
        if (e is RpcException rpc)
            ReplyError(rpc.Status, rpc.Message);
        else
            ReplyError(RpcStatus.Error, e.GetType().Name + ": " + e.Message);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask AwaitReply<T>(ValueTask<T> task, CallContext context)
    {
        T value;
        try
        {
            value = await task.ConfigureAwait(true);
        }
        catch (Exception e)
        {
            context.ReplyException(e);
            return;
        }

        context.ReplyValue(value);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask AwaitReply(ValueTask task, CallContext context)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (Exception e)
        {
            context.ReplyException(e);
            return;
        }

        context.ReplyEmpty();
    }
}
