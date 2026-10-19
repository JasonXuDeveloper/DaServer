namespace DaServer.Protocol;

/// <summary>
/// The protocol levels a build reads and writes, as a range (ADR-077). Exchanged in the handshake outside the
/// serializer's bytes, so any build can read them, and two builds talk when their ranges overlap; a refusal names both.
/// A level changes only for what every peer must read alike: the framing, the shared encoding, or a behavior all must
/// agree on. Added control ops, fields at the end of version-tolerant types and new messages need none.
/// </summary>
internal readonly record struct ProtocolLevels(int Min, int Max)
{
    /// <summary>The range in one header key: Max in the high half, Min in the low.</summary>
    public long Packed => (long)Max << 32 | (uint)Min;

    public static ProtocolLevels Of(long packed) => new((int)packed, (int)(packed >> 32));

    public bool Overlaps(ProtocolLevels other) => Min <= other.Max && other.Min <= Max;

    public override string ToString() => Min == Max ? Min.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{Min}-{Max}";
}
