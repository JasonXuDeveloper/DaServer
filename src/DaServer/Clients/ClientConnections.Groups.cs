namespace DaServer.Clients;

// Client groups: membership, fan-out, and the sweep of long-empty groups.
public sealed partial class ClientConnections
{
    /// <summary>A session operation sent to a group: applied to each member of this node (a snapshot: it may change them).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ApplyToGroup(long groupId, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        if (!_groups.TryGetValue(groupId, out var group)) return;
        foreach (var session in group.Snapshot()) Apply(session, in header, frame);
    }

    private void DeliverToSession(long sessionId, ReadOnlySpan<byte> frame)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.Channel.Send(frame);
    }

    /// <summary>Fan a frame out to every local session of a group (group 0 = every session). memcpy per session.</summary>
    private void DeliverToGroup(long groupId, ReadOnlySpan<byte> frame)
    {
        if (_groups.TryGetValue(groupId, out var group)) group.Send(groupId, frame);
    }

    /// <summary>Add a session to a group (once). Caller holds the gate.</summary>
    private void Join(SessionState session, long groupId)
    {
        if (session.Closed || session.Groups.ContainsKey(groupId)) return;
        if (!_groups.TryGetValue(groupId, out var group))
            _groups[groupId] = group = (_idleGroups.TryPop(out var idle) ? idle : new GroupMembers()).For(groupId);
        group.Add(session);
    }

    /// <summary>Remove a session from a group; empty groups are dropped (bounded memory). Caller holds the gate.</summary>
    /// <summary>Remove the session from every group it is in. Under the group gate.</summary>
    private void LeaveGroups(SessionState session)
    {
        while (session.Groups.Count > 0)
        {
            using var groups = session.Groups.Keys.GetEnumerator();
            groups.MoveNext();
            Leave(session, groups.Current);
        }
    }

    private void Leave(SessionState session, long groupId)
    {
        if (!session.Groups.ContainsKey(groupId) || !_groups.TryGetValue(groupId, out var group)) return;
        if (group.Remove(session) == 0) group.EmptySince = Platform.Milliseconds; // removed later if it stays empty
    }

    /// <summary>
    /// Empty groups stay in the table a while (a group emptied and joined again, as area-of-interest cells are, costs
    /// nothing then); those empty for <see cref="EmptyGroupLifetimeMs"/> leave it for the pool. Bounds memory.
    /// </summary>
    private void SweepGroups(long now)
    {
        _nextGroupSweep = now + EmptyGroupLifetimeMs;
        lock (_groupGate)
            foreach (var (id, group) in _groups)
            {
                if (group.Count != 0 || now - group.EmptySince < EmptyGroupLifetimeMs || id == ClientGroup.All.Id) continue;
                _groups.TryRemove(id, out _);
                if (_idleGroups.Count < MaxIdleGroups && group.Capacity <= GroupMembers.MaxPooledMembers) _idleGroups.Push(group);
            }
    }

    private const long EmptyGroupLifetimeMs = 10_000;
    private long _nextGroupSweep;
}

/// <summary>
/// The sessions of one client group on this node. Membership changes (under the group gate) and fan-out
/// both take the group's lock, so a fan-out reaches every session that joined before it (a Bind then a push on one
/// link: the push reaches the session). Join and leave are O(1) (slot per session, swap-remove); a broadcast copies
/// nothing and allocates nothing. Lock order: group gate, group, connection send lock; <see cref="IFrameChannel.Send"/>
/// never closes inline, so no close callback re-enters a group during a fan-out.
/// </summary>
internal sealed class GroupMembers
{
    private readonly Lock _gate = new();
    private SessionState[] _members = new SessionState[4];
    private FrameChannel[] _channels = new FrameChannel[4]; // the members' channels, slot for slot: a fan-out reads only these
    private int _count;
    private long _id;

    /// <summary>Which group this is now (instances are pooled: an empty group's object serves the next new group).</summary>
    public long Id => _id;

    /// <summary>Members now (read under the group gate).</summary>
    public int Count => _count;

    /// <summary>When the group last became empty (<see cref="Platform.Milliseconds"/>; under the group gate).</summary>
    public long EmptySince;

    /// <summary>Members arrays up to this length are pooled with their group object.</summary>
    public const int MaxPooledMembers = 64;

    public int Capacity => _members.Length;

    /// <summary>Become group <paramref name="id"/> (an empty, pooled instance; under the group gate).</summary>
    public GroupMembers For(long id)
    {
        lock (_gate) _id = id;
        return this;
    }

    public void Add(SessionState session)
    {
        lock (_gate)
        {
            if (_count == _members.Length)
            {
                Array.Resize(ref _members, _count * 2);
                Array.Resize(ref _channels, _count * 2);
            }

            _members[_count] = session;
            _channels[_count] = (FrameChannel)session.Channel;
            session.Groups[_id] = _count++;
        }
    }

    /// <summary>Remove <paramref name="session"/> (a member); returns the members left.</summary>
    public int Remove(SessionState session)
    {
        lock (_gate)
        {
            session.Groups.Remove(_id, out int slot);
            var last = _members[--_count];
            _members[_count] = null!;
            _channels[_count] = null!;
            if (slot != _count)
            {
                _members[slot] = last;
                _channels[slot] = (FrameChannel)last.Channel;
                last.Groups[_id] = slot;
            }

            return _count;
        }
    }

    /// <summary>
    /// Send <paramref name="frame"/> to every member, if this is still group <paramref name="groupId"/> (a fan-out that
    /// looked the group up just before it emptied and was reused must not reach the next group's members).
    /// </summary>
    public void Send(long groupId, ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            if (_id != groupId) return;
            var channels = _channels;
            for (int i = 0; i < _count; i++) channels[i].Send(frame);
        }
    }

    /// <summary>The members now (a copy: for operations that change memberships, a rare path).</summary>
    public SessionState[] Snapshot()
    {
        lock (_gate) return _members.AsSpan(0, _count).ToArray();
    }
}
