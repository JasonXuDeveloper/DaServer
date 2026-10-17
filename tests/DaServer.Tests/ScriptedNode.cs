using System.Threading.Channels;
using DaServer.Ingress;
using DaServer.Net;

namespace DaServer.Tests;

/// <summary>
/// A scripted client-facing node for the client's edge cases (<see cref="ClientMoveTests"/>): every connection it opens
/// answers hellos, resumes and goodbyes, and answers requests with 1 unless told to hold them. Tests drive the
/// interleavings a real network leaves to chance: a move while a sender holds the old connection, a node that dies
/// with calls in flight, a resume the node refuses.
/// </summary>
internal sealed class ScriptedNode
{
    private readonly List<ScriptedChannel> _channels = new();

    /// <summary>Refuse resumes (a ticket the node does not take): a move fails.</summary>
    public volatile bool RefuseResume;

    public ScriptedChannel this[int index]
    {
        get { lock (_channels) return _channels[index]; }
    }

    public int Count
    {
        get { lock (_channels) return _channels.Count; }
    }

    /// <summary>For <c>ClusterClient.Connector</c>.</summary>
    public IFrameChannel Open(IChannelHandler client)
    {
        var channel = new ScriptedChannel(this, client);
        lock (_channels) _channels.Add(channel);
        return channel;
    }
}

internal sealed class ScriptedChannel : IFrameChannel
{
    private readonly ScriptedNode _node;
    private readonly IChannelHandler _client;
    private readonly Lock _gate = new();
    private readonly List<byte[]> _sent = new();
    private readonly List<uint> _held = new();
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private bool _sealed, _holding;
    private int _closed;
    private Action? _beforeNextSend;

    public ScriptedChannel(ScriptedNode node, IChannelHandler client)
    {
        (_node, _client) = (node, client);
        _ = Task.Run(Receive);
    }

    // The client's receive loop: frames in order, on one thread at a time.
    private async Task Receive()
    {
        await foreach (var frame in _inbound.Reader.ReadAllAsync())
            _client.OnFrame(this, FrameHeader.Read(frame), frame);
    }

    public object? Tag { get; set; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public long LastReceivedMs => Platform.Milliseconds;
    public long LastSentMs => Platform.Milliseconds;

    public bool Sealed
    {
        get { lock (_gate) return _sealed; }
    }

    /// <summary>What the client sent on this connection, in order: (kind, message id or control op).</summary>
    public (FrameKind Kind, uint Id)[] Sent
    {
        get
        {
            lock (_gate) return _sent.Select(static f => FrameHeader.Read(f)).Select(static h => (h.Kind, h.MessageId)).ToArray();
        }
    }

    public int Requests => Sent.Count(static f => f.Kind == FrameKind.Message);

    /// <summary>Run <paramref name="action"/> inside the next send, before the frame is taken: whatever it does happens
    /// after the sender picked this connection and before its frame reaches it.</summary>
    public void BeforeNextSend(Action action) => _beforeNextSend = action;

    /// <summary>Requests are taken but not answered until <see cref="Release"/>.</summary>
    public void Hold()
    {
        lock (_gate) _holding = true;
    }

    public void Release()
    {
        uint[] held;
        lock (_gate)
        {
            _holding = false;
            held = [.. _held];
            _held.Clear();
        }

        foreach (uint callId in held) Reply(callId, RpcStatus.Ok, 1);
    }

    public bool Send(ReadOnlySpan<byte> frame)
    {
        Interlocked.Exchange(ref _beforeNextSend, null)?.Invoke();
        return Take(frame, last: false);
    }

    public bool SendLast(ReadOnlySpan<byte> frame) => Take(frame, last: true);

    private bool Take(ReadOnlySpan<byte> frame, bool last)
    {
        var copy = frame.ToArray();
        var header = FrameHeader.Read(copy);
        uint callId = FrameHeader.CallIdOf(in header, copy);
        bool hold;
        lock (_gate)
        {
            if (IsClosed || _sealed) return false;
            _sealed = last;
            _sent.Add(copy);
            hold = _holding && header.Kind == FrameKind.Message && callId != 0;
            if (hold) _held.Add(callId);
        }

        if (callId == 0 || hold) return true;
        if (header.Kind == FrameKind.Message) Reply(callId, RpcStatus.Ok, 1);
        else if (header.MessageId == ControlOp.ResumeSession) Reply(callId, _node.RefuseResume ? RpcStatus.Forbidden : RpcStatus.Ok);
        else Reply(callId, RpcStatus.Ok); // hello, goodbye
        return true;
    }

    private void Reply(uint callId, RpcStatus status, int? value = null)
    {
        var w = FrameWriter.Rent();
        if (value is { } v) w.Write(v);
        w.SetCallId(callId);
        w.SetHeader(FrameKind.Reply, 0, 0, 0, status);
        Push(w.Frame.ToArray());
        w.Dispose();
    }

    private void Push(byte[] frame)
    {
        if (!IsClosed) _inbound.Writer.TryWrite(frame);
    }

    private void PushControl(uint op, ReadOnlySpan<byte> payload = default)
    {
        var w = FrameWriter.Rent();
        w.WriteRaw(payload);
        w.SetHeader(FrameKind.Control, 0, op, 0);
        Push(w.Frame.ToArray());
        w.Dispose();
    }

    /// <summary>The node gives the session a resume ticket (without one a client cannot move).</summary>
    public void GiveTicket()
    {
        var ticket = new byte[SignedToken.SizeOf(8)];
        SignedToken.Sign(ticket, new byte[32], Platform.Milliseconds, 60_000);
        PushControl(ControlOp.Ticket, ticket);
    }

    /// <summary>The node asks the client to move (<see cref="ControlOp.Retire"/>).</summary>
    public void Retire() => PushControl(ControlOp.Retire);

    public void Close(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _inbound.Writer.TryComplete();
        _client.OnClosed(this, error);
    }
}
