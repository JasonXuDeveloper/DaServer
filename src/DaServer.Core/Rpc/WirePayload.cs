using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DaServer.Rpc;

/// <summary>
/// Reads a whole payload (a message, a reply, a record section) as <typeparamref name="T"/>. Nino reads a type made
/// only of value fields as its memory without a length check: a payload shorter than the type (truncated, hostile)
/// would be read past its end. Here it is read within its bytes, the rest zero; allocation-free. Other types and
/// full-length payloads are Nino's read.
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
