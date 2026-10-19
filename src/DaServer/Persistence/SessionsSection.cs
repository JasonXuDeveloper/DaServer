namespace DaServer.Persistence;

/// <summary>
/// The client sessions an actor knows (<see cref="ClientSessions"/>) as a section of its handoff record: an actor that
/// moves keeps the sessions it bound and granted, so its client can still resume and a session that ends still reaches
/// it (ADR-036, ADR-072). Never stored: a reload starts with no session, as before. The old copy keeps its sets (they
/// drive no system), so a handoff that falls back restores nothing here.
/// </summary>
internal sealed class SessionsSection() : PersistentType(typeof(ClientSessions), TypeId<ClientSessions>.Value, stored: false)
{
    public override void Write(Entity entity, FrameWriter record)
    {
        if (!entity.TryGet<ClientSessions>(out var sessions) || sessions.Sessions.Count + sessions.Granted.Count + sessions.Superseded.Count == 0)
            return;
        int at = record.Length;
        var head = record.GetSpan(Records.SectionHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(head, TypeId);
        record.Advance(Records.SectionHeader);
        record.Write(new SessionsRecord { Sessions = [.. sessions.Sessions], Granted = [.. sessions.Granted], Superseded = [.. sessions.Superseded] });
        BinaryPrimitives.WriteInt32LittleEndian(record.Written(at + sizeof(uint), sizeof(int)), record.Length - at - Records.SectionHeader);
    }

    public override void Read(Entity entity, ReadOnlySpan<byte> section)
    {
        var moved = NinoDeserializer.Deserialize<SessionsRecord>(section);
        if (!entity.TryGet<ClientSessions>(out var sessions))
            entity.Set(sessions = entity.World.GetSingleton<SessionPool>().Rent());
        sessions.Sessions.UnionWith(moved.Sessions);
        sessions.Granted.UnionWith(moved.Granted);
        sessions.Superseded.UnionWith(moved.Superseded);
    }

    public override void Remove(Entity entity)
    {
    }
}

/// <summary>The handoff record's sessions section (cold: once per move).</summary>
[NinoType(format: NinoFormat.VersionTolerant)]
internal sealed class SessionsRecord
{
    public ClientSession[] Sessions = [];
    public ClientSession[] Granted = [];
    public ClientSession[] Superseded = [];
}
