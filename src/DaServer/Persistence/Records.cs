namespace DaServer.Persistence;

/// <summary>
/// A persisted component type (<see cref="PersistAttribute"/>): writes and restores its section of a record. Registered
/// by the generated registration of the node (<see cref="ServerRegistry.RegisterPersistent{T}"/>), one per type.
/// </summary>
internal abstract class PersistentType(Type type, uint typeId, bool stored)
{
    public Type Type { get; } = type;

    public uint TypeId { get; } = typeId;

    /// <summary>Written to the store (<see cref="PersistAttribute"/>); otherwise only handed to a new owner (<see cref="HandoffAttribute"/>).</summary>
    public bool Stored { get; } = stored;

    /// <summary>Append this component's section to <paramref name="record"/> when <paramref name="entity"/> has it.</summary>
    public abstract void Write(Entity entity, FrameWriter record);

    /// <summary>Set the component on <paramref name="entity"/> from its section.</summary>
    public abstract void Read(Entity entity, ReadOnlySpan<byte> section);
}

internal sealed class PersistentType<T>(uint typeId, bool stored) : PersistentType(typeof(T), typeId, stored) where T : struct, IComponent
{
    public override void Write(Entity entity, FrameWriter record)
    {
        if (!entity.Has<T>()) return;
        int at = record.Length;
        var head = record.GetSpan(Records.SectionHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(head, TypeId);
        record.Advance(Records.SectionHeader);
        var nino = record.Nino;
        NinoSerializer.Serialize(entity.Get<T>(), ref nino);
        BinaryPrimitives.WriteInt32LittleEndian(record.Written(at + sizeof(uint), sizeof(int)), record.Length - at - Records.SectionHeader);
    }

    public override void Read(Entity entity, ReadOnlySpan<byte> section)
    {
        var reader = new Reader(section);
        NinoDeserializer.Deserialize(out T value, ref reader);
        if (entity.Has<T>()) entity.Get<T>() = value;
        else entity.Add<T>() = value;
    }
}

/// <summary>
/// An actor's record: a section per persisted component it has, each <c>[uint type id][int length][Nino bytes]</c>.
/// Sections of types this node does not know (a component removed from the code) are skipped on load and dropped on
/// the next save; a component added later is simply missing from older records.
/// </summary>
internal static class Records
{
    public const int SectionHeader = sizeof(uint) + sizeof(int);

    /// <summary>Write the sections of <paramref name="entity"/> into a pooled frame (its payload is the record).</summary>
    public static FrameWriter Write(Entity entity, PersistentType[] types)
    {
        var record = FrameWriter.Rent(1024);
        foreach (var type in types) type.Write(entity, record);
        return record;
    }

    /// <summary>Restore the sections of <paramref name="data"/> onto <paramref name="entity"/>.</summary>
    public static void Read(Entity entity, ReadOnlySpan<byte> data, FrozenDictionary<uint, PersistentType> types)
    {
        while (data.Length >= SectionHeader)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data);
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[sizeof(uint)..]);
            var section = data.Slice(SectionHeader, length);
            if (types.TryGetValue(id, out var type)) type.Read(entity, section);
            data = data[(SectionHeader + length)..];
        }
    }

    public static ulong Hash(ReadOnlySpan<byte> record) => System.IO.Hashing.XxHash3.HashToUInt64(record);
}
