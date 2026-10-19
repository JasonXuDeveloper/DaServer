namespace DaServer.Persistence;

/// <summary>
/// The client sessions an actor knows (its <see cref="KnownSession"/> children) as a section of its handoff record: an
/// actor that moves keeps the sessions it bound and granted, so its client can still resume and a session that ends
/// still reaches it (ADR-036, ADR-072). Never stored: a reload starts with no session. The old copy keeps its session
/// entities (they drive no system), so a handoff that falls back restores nothing here.
/// </summary>
internal sealed class SessionsSection() : PersistentType(typeof(KnownSession), TypeId<KnownSession>.Value, markWritten: null)
{
    public override void Write(Entity entity, FrameWriter record)
    {
        if (ActorSessions.Collect(entity.World, entity) is not { } sessions) return;
        int at = record.Length;
        var head = record.GetSpan(Records.SectionHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(head, TypeId);
        record.Advance(Records.SectionHeader);
        record.Write(sessions);
        BinaryPrimitives.WriteInt32LittleEndian(record.Written(at + sizeof(uint), sizeof(int)), record.Length - at - Records.SectionHeader);
    }

    public override void Read(Entity entity, ReadOnlySpan<byte> section) =>
        ActorSessions.Restore(entity.World, entity, NinoDeserializer.Deserialize<SessionsRecord>(section));

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
