using System.Security.Cryptography;

namespace DaServer.Cluster;

/// <summary>
/// Proof that a Hello comes from a node holding the cluster's signing secret (ADR-065): an HMAC over the cluster name,
/// the node id, the stream id and the sending time. A proof is accepted once, within a minute of its time, so a
/// recorded Hello cannot be replayed later. Without a secret every Hello of the right cluster and protocol is accepted
/// (development clusters).
/// </summary>
internal sealed class LinkAuth(string clusterName, string? secret)
{
    private const long WindowMs = 60_000;

    private readonly byte[]? _key = secret is { Length: > 0 } ? System.Text.Encoding.UTF8.GetBytes(secret) : null;
    private readonly byte[] _cluster = System.Text.Encoding.UTF8.GetBytes(clusterName);
    private readonly ConcurrentDictionary<long, long> _seen = new(); // proof prefix -> when it may be forgotten

    public byte[] Prove(ulong nodeId, long streamId, long at)
    {
        if (_key == null) return [];
        Span<byte> material = stackalloc byte[_cluster.Length + 3 * sizeof(long)];
        _cluster.CopyTo(material);
        BinaryPrimitives.WriteUInt64LittleEndian(material[_cluster.Length..], nodeId);
        BinaryPrimitives.WriteInt64LittleEndian(material[(_cluster.Length + 8)..], streamId);
        BinaryPrimitives.WriteInt64LittleEndian(material[(_cluster.Length + 16)..], at);
        return HMACSHA256.HashData(_key, material);
    }

    /// <summary>Null when the Hello is authentic, else why it is refused.</summary>
    public string? Verify(ulong nodeId, long streamId, long at, byte[] proof)
    {
        if (_key == null) return null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - at) > WindowMs) return "its time is more than a minute off (clock skew or a replay)";
        if (proof.Length != HMACSHA256.HashSizeInBytes || !CryptographicOperations.FixedTimeEquals(proof, Prove(nodeId, streamId, at)))
            return "it does not hold the cluster's signing secret";
        if (!_seen.TryAdd(BinaryPrimitives.ReadInt64LittleEndian(proof), now + 2 * WindowMs)) return "a replayed Hello";
        if (_seen.Count > 1024) Forget(now);
        return null;
    }

    private void Forget(long now)
    {
        foreach (var (tag, until) in _seen)
            if (until < now) _seen.TryRemove(tag, out _);
    }
}
