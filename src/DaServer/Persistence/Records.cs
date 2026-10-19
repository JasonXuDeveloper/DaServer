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

    /// <summary>Remove the component from <paramref name="entity"/> (its record went to another node).</summary>
    public abstract void Remove(Entity entity);
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
        record.Write(in entity.Get<T>()); // the component in place: no copy of a large one
        BinaryPrimitives.WriteInt32LittleEndian(record.Written(at + sizeof(uint), sizeof(int)), record.Length - at - Records.SectionHeader);
    }

    public override void Read(Entity entity, ReadOnlySpan<byte> section)
    {
        var value = NinoDeserializer.Deserialize<T>(section);
        if (entity.Has<T>()) entity.Get<T>() = value;
        else entity.Set<T>(value);
    }

    public override void Remove(Entity entity)
    {
        if (entity.Has<T>()) entity.Remove<T>();
    }
}

/// <summary>
/// An actor's record: a 4-byte format header (<c>"DR"</c> and <see cref="Version"/>), then a section per persisted
/// component it has, each <c>[uint type id][int length][Nino bytes]</c>. Nino's bytes carry no format version of their
/// own, so a record written in another format (an older Nino) is refused instead of misread. Sections of types this
/// node does not know (a component removed from the code) are skipped on load and dropped on the next save; a component
/// added later is simply missing from older records.
/// </summary>
internal static class Records
{
    public const int SectionHeader = sizeof(uint) + sizeof(int);

    /// <summary>"DR" (DaServer record), then the format version: 1 is Nino 4's format.</summary>
    private const ushort Magic = 0x5244, Version = 1;

    /// <summary>Write the sections of <paramref name="entity"/> into a pooled frame (its payload is the record).</summary>
    public static FrameWriter Write(Entity entity, PersistentType[] types)
    {
        var record = FrameWriter.Rent(1024);
        try
        {
            WriteSections(entity, types, record);
        }
        catch
        {
            record.Dispose(); // the state does not serialize (a cycle, an unregistered subtype): the caller decides
            throw;
        }

        return record;
    }

    /// <summary>Append the format header and the sections of <paramref name="entity"/>'s <paramref name="types"/> to <paramref name="record"/>.</summary>
    public static void WriteSections(Entity entity, PersistentType[] types, FrameWriter record)
    {
        WriteHeader(record.GetSpan(Header));
        record.Advance(Header);
        foreach (var type in types) type.Write(entity, record);
    }

    /// <summary>The format header's size: a record starts with it.</summary>
    public const int Header = sizeof(uint);

    /// <summary>Write the format header (<see cref="Header"/> bytes) at the start of <paramref name="record"/>.</summary>
    public static void WriteHeader(Span<byte> record)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(record, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(record[sizeof(ushort)..], Version);
    }

    /// <summary>Restore the sections of <paramref name="data"/> onto <paramref name="entity"/>.</summary>
    public static void Read(Entity entity, ReadOnlySpan<byte> data, FrozenDictionary<uint, PersistentType> types)
    {
        if (data.IsEmpty) return; // no record yet
        if (data.Length < Header || BinaryPrimitives.ReadUInt16LittleEndian(data) != Magic)
            throw new InvalidDataException("Not an actor record of this format (no DaServer record header)");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data[sizeof(ushort)..]);
        if (version != Version) throw new InvalidDataException($"Actor record format {version}, this node reads {Version}");
        data = data[Header..];
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
