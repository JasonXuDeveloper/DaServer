using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DaServer.Rpc;

/// <summary>
/// Reads a whole payload (a message, a reply, a record section) as <typeparamref name="T"/> (ADR-076). Nino writes a
/// type made only of value fields as its memory, so a newer build of such a type (fields appended) reads an older,
/// shorter payload by taking its bytes and leaving the rest zero: the defaults of the new fields. Allocation-free; for
/// other types and full-length payloads it is Nino's read.
/// </summary>
public static class WirePayload
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Read<T>(ReadOnlySpan<byte> payload, out T value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>() && payload.Length < Unsafe.SizeOf<T>())
        {
            ReadShorter(payload, out value);
            return;
        }

        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out value, ref reader);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadShorter<T>(ReadOnlySpan<byte> payload, out T value)
    {
        value = default!;
        payload.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref value), Unsafe.SizeOf<T>()));
    }
}
