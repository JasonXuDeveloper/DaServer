namespace DaServer.Protocol;

/// <summary>
/// The extension block of a frame (<see cref="FrameFlags.HasExtensions"/>): optional metadata between the payload and the
/// fixed trailers, as entries of [id byte][length byte][data], closed by the block's size (uint16, entries and itself).
/// A reader looks entries up by id and skips the others, so metadata added later reaches nodes that ignore it instead of
/// needing a new link protocol (what protobuf's unknown fields do). Hops relay the block untouched.
/// </summary>
internal static class FrameExtensions
{
    /// <summary>W3C trace context: trace id (16 bytes), parent span id (8), trace flags (1).</summary>
    public const byte Trace = 1;

    /// <summary>Data bytes of a <see cref="Trace"/> entry.</summary>
    public const int TraceSize = 25;

    /// <summary>Bytes of the extension block of a frame of <paramref name="length"/> bytes with <paramref name="flags"/> (0: none).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SizeOf(byte flags, ReadOnlySpan<byte> frame, int length) =>
        (flags & FrameFlags.HasExtensions) == 0 ? 0 : SizeAt(frame, length - FrameFlags.TrailerSize(flags));

    /// <summary>The block's size, read from its end (<paramref name="end"/>: where the fixed trailers start).</summary>
    internal static int SizeAt(ReadOnlySpan<byte> frame, int end) => BinaryPrimitives.ReadUInt16LittleEndian(frame[(end - sizeof(ushort))..]);

    /// <summary>The data of entry <paramref name="id"/> of a frame; false when it has none.</summary>
    public static bool TryGet(in FrameHeader header, ReadOnlySpan<byte> frame, byte id, out ReadOnlySpan<byte> data) =>
        TryGet(frame, header.Flags, header.Length, id, out data);

    /// <summary>The data of entry <paramref name="id"/> of a frame of <paramref name="length"/> bytes with <paramref name="flags"/>.</summary>
    public static bool TryGet(ReadOnlySpan<byte> frame, byte flags, int length, byte id, out ReadOnlySpan<byte> data)
    {
        data = default;
        if ((flags & FrameFlags.HasExtensions) == 0) return false;
        int end = length - FrameFlags.TrailerSize(flags) - sizeof(ushort);
        for (int at = end + sizeof(ushort) - SizeAt(frame, end + sizeof(ushort)); at + 2 <= end;)
        {
            int size = frame[at + 1];
            if (at + 2 + size > end) return false; // malformed: entries run past the block
            if (frame[at] == id)
            {
                data = frame.Slice(at + 2, size);
                return true;
            }

            at += 2 + size;
        }

        return false;
    }
}
