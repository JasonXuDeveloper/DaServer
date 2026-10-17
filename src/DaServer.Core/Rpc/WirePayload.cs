using System.Runtime.CompilerServices;

namespace DaServer.Rpc;

/// <summary>
/// Reads a whole payload (a message, a reply, a record section) as <typeparamref name="T"/>. Nino reads a type made
/// only of value fields as its memory without a length check, so a payload shorter than the type (truncated or
/// hostile) would be read past its end before Nino notices; it is refused here first. Layouts are frozen (ADR-076):
/// no valid payload is shorter. Otherwise this is Nino's read.
/// </summary>
public static class WirePayload
{
    private static readonly InvalidOperationException Truncated = new("Payload shorter than its type");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Read<T>(ReadOnlySpan<byte> payload, out T value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>() && payload.Length < Unsafe.SizeOf<T>()) ThrowTruncated();
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out value, ref reader);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncated() => throw Truncated;
}
