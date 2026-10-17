using System.Runtime.CompilerServices;

namespace DaServer.Rpc;

/// <summary>
/// Reads a whole payload (a message, a reply, a record section) as <typeparamref name="T"/>. Nino reads a type made
/// only of value fields as its memory (behind a 4-byte type id when it has [NinoType] interfaces) without a length
/// check, so a shorter payload (truncated or hostile) would be read past its end before Nino notices; it is refused
/// here first. Layouts are frozen (ADR-076): no valid payload is shorter. Other types are Nino's read, which may still
/// look up to 8 bytes past a truncated payload before it throws (roadmap: Nino).
/// </summary>
public static class WirePayload
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Read<T>(ReadOnlySpan<byte> payload, out T value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>() && payload.Length < Least<T>.Bytes) ThrowTruncated();
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out value, ref reader);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncated() => throw new InvalidOperationException("Payload shorter than its type");

    /// <summary>The bytes Nino writes for a value-only <typeparamref name="T"/>: its memory, and a type id before it when it has [NinoType] interfaces.</summary>
    private static class Least<T>
    {
        public static readonly int Bytes = Unsafe.SizeOf<T>() + (Typed() ? sizeof(int) : 0);

        private static bool Typed()
        {
            foreach (var i in typeof(T).GetInterfaces())
                if (i.IsDefined(typeof(NinoTypeAttribute), false)) return true;
            return false;
        }
    }
}
