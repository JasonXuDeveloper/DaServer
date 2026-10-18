namespace DaServer.Clients;

// Lost connections and resumes: the resume grace, tickets, and moving a session onto a new connection.
public sealed partial class ClientConnections
{
    /// <summary>Drop the session from its groups and tell every actor it was bound to (<see cref="SessionClosed"/>).</summary>
    private void OnSessionClosed(SessionState session)
    {
        _sessions.TryRemove(session.Id, out _);
        if (_crashed) return; // a crashed node tells nobody
        SessionGrant[] grants;
        lock (_groupGate)
        {
            session.Closed = true;
            grants = session.Grants; // final: grants change under the gate, never on a closed session
            while (session.Groups.Count > 0)
            {
                using var groups = session.Groups.Keys.GetEnumerator();
                groups.MoveNext();
                Leave(session, groups.Current);
            }
        }

        var sessionRef = new ClientSession(_node.Id, session.Id);
        if (grants.Length == 0) return;
        if (session.Explicit || ResumeGrace == TimeSpan.Zero) ReportClosed(sessionRef, grants);
        else
            lock (_graces) _graces.Schedule(ResumeGrace, new PendingClose(sessionRef, grants));
    }

    private readonly struct GraceExpired(ClientConnections clients) : ITimerHandler<PendingClose>
    {
        public void OnTimer(in PendingClose close)
        {
            if (!clients._stopped) clients.ReportClosed(close.Session, close.Grants);
        }
    }

    private void ReportClosed(ClientSession session, SessionGrant[] grants)
    {
        foreach (var grant in grants)
            _node.SendTo(grant.HandlerTypeId, grant.Key, new SessionClosed(session));
    }

    private TimeSpan ResumeGrace => _node.Options.EffectiveResumeGrace;

    /// <summary>Send the session's client a fresh resume ticket (after grant changes, and when the client asks).</summary>
    private void IssueTicket(SessionState session)
    {
        var grants = session.Grants;
        if (_ticketSecret == null) return;
        int size = grants.Length == 0 ? 0 : ResumeTicket.SizeOf(grants.Length); // empty: nothing to resume
        Span<byte> ticket = stackalloc byte[size];
        if (size > 0)
            ResumeTicket.Write(ticket, _ticketSecret, new ClientSession(_node.Id, session.Id), grants,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (long)options.TicketLifetime.TotalMilliseconds);
        var w = FrameWriter.Rent(FrameHeader.Size + size);
        w.WriteRaw(ticket);
        w.SendControl((IFrameSender)session.Sink, ControlOp.Ticket);
    }

    /// <summary>
    /// A client that lost its connection presents its ticket on a new one. The ticket only names what the session held:
    /// each actor in it is asked (<see cref="SessionResumed"/>) and accepts only while it still holds the previous session
    /// granted, so a withdrawn grant, a closed session or a ticket used once already resumes nothing. The new session
    /// takes over what was accepted (and the actors' groups), then the client gets its reply: its next messages reach
    /// actors that already know the new session. Cold path (a resume): it allocates.
    /// </summary>
    private void ResumeSession(SessionState session, uint callId, ReadOnlySpan<byte> ticket)
    {
        if (_ticketSecret == null || session.Grants.Length != 0 ||
            !ResumeTicket.TryRead(ticket, _ticketSecret, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var previous, out var granted) ||
            Interlocked.Exchange(ref session.Resuming, 1) != 0)
        {
            session.Sink.SendError(callId, RpcStatus.Forbidden);
            return;
        }

        var grants = new SessionGrant[ResumeTicket.GrantCount(granted)];
        for (int i = 0; i < grants.Length; i++) grants[i] = ResumeTicket.GrantAt(granted, i);
        _ = ResumeAsync(session, callId, previous, grants);
    }

    private async Task ResumeAsync(SessionState session, uint callId, ClientSession previous, SessionGrant[] grants)
    {
        var current = new ClientSession(_node.Id, session.Id);
        var resumed = new SessionResumed(current, previous);
        var accepted = new bool[grants.Length];

        // The first actor that takes the session decides, asked alone: a ticket presented on two connections at once
        // resumes on the one it accepted, and the other stops at its Superseded instead of taking the other actors.
        int first = 0;
        for (; first < grants.Length; first++)
        {
            var status = await Accepts(grants[first], resumed).ConfigureAwait(false);
            if (status == RpcStatus.Superseded)
            {
                session.Sink.SendError(callId, RpcStatus.Forbidden);
                return;
            }

            if (accepted[first] = status == RpcStatus.Ok) break;
        }

        if (first < grants.Length - 1)
        {
            var answers = new Task<RpcStatus>[grants.Length - first - 1];
            for (int i = 0; i < answers.Length; i++) answers[i] = Accepts(grants[first + 1 + i], resumed);
            var rest = await Task.WhenAll(answers).ConfigureAwait(false);
            for (int i = 0; i < rest.Length; i++) accepted[first + 1 + i] = rest[i] == RpcStatus.Ok;
        }

        int taken = 0;
        bool closed;
        lock (_groupGate)
        {
            closed = session.Closed;
            for (int i = 0; i < grants.Length && !closed; i++)
            {
                if (!accepted[i]) continue;
                session.Grant(grants[i].HandlerTypeId, grants[i].Key, grants[i].Bound);
                Join(session, ClientGroup.Of(grants[i].HandlerTypeId, grants[i].Key).Id);
                taken++;
            }
        }

        if (closed)
        {
            // Closed while the actors were asked: those that took the new session learn it ended.
            for (int i = 0; i < grants.Length; i++)
                if (accepted[i]) _node.SendTo(grants[i].HandlerTypeId, grants[i].Key, new SessionClosed(current));
            return;
        }

        if (taken == 0)
        {
            session.Sink.SendError(callId, RpcStatus.Forbidden);
            return;
        }

        IssueTicket(session);
        session.Sink.SendOk(callId);
    }

    private async Task<RpcStatus> Accepts(SessionGrant grant, SessionResumed resumed)
    {
        try
        {
            await _node.Resume(grant.HandlerTypeId, grant.Key, in resumed).ConfigureAwait(false);
            return RpcStatus.Ok;
        }
        catch (RpcException e)
        {
            return e.Status; // refused, or the actor's node is gone: the client asks it again (logs in, enters again)
        }
    }
}
