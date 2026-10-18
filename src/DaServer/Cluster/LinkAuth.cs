using System.Security.Cryptography;

namespace DaServer.Cluster;

/// <summary>
/// Proof that a Hello comes from a node holding the cluster's signing secret (ADR-065): an HMAC over the cluster name,
/// the node id, its stream id, the endpoint it names and the challenge the accepting node sent first on that connection.
/// A fresh challenge per connection binds the proof to that connection and node: a recorded Hello is good nowhere else,
/// and the endpoint replies go to is the sender's own. Without a secret every Hello of the right cluster and protocol
/// is accepted (development clusters). A proof made with the accepted secret is good too
/// (<see cref="NodeOptions.AcceptedSigningSecret"/>: a rotation in progress).
/// </summary>
internal sealed class LinkAuth(string clusterName, string? secret, string? accepted = null)
{
    public const int ChallengeSize = 16;

    private readonly byte[]? _key = secret is { Length: > 0 } ? System.Text.Encoding.UTF8.GetBytes(secret) : null;
    private readonly byte[]? _accepted = accepted is { Length: > 0 } ? System.Text.Encoding.UTF8.GetBytes(accepted) : null;
    private readonly byte[] _cluster = System.Text.Encoding.UTF8.GetBytes(clusterName);

    /// <summary>A fresh challenge for an accepted connection.</summary>
    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(ChallengeSize);

    public byte[] Prove(ulong nodeId, long streamId, string host, int port, ReadOnlySpan<byte> challenge) =>
        _key == null ? [] : Prove(_key, nodeId, streamId, host, port, challenge);

    private byte[] Prove(byte[] key, ulong nodeId, long streamId, string host, int port, ReadOnlySpan<byte> challenge)
    {
        int hostBytes = System.Text.Encoding.UTF8.GetByteCount(host);
        var material = new byte[_cluster.Length + 2 * sizeof(long) + sizeof(int) + hostBytes + challenge.Length]; // once per connection
        var span = material.AsSpan();
        _cluster.CopyTo(span);
        span = span[_cluster.Length..];
        BinaryPrimitives.WriteUInt64LittleEndian(span, nodeId);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], streamId);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], port);
        System.Text.Encoding.UTF8.GetBytes(host, span[20..]);
        challenge.CopyTo(span[(20 + hostBytes)..]);
        return HMACSHA256.HashData(key, material);
    }

    /// <summary>Null when the Hello is authentic, else why it is refused.</summary>
    public string? Verify(ulong nodeId, long streamId, string host, int port, ReadOnlySpan<byte> challenge, byte[] proof)
    {
        if (_key == null) return null;
        if (challenge.Length != ChallengeSize) return "it came before this node's challenge";
        return proof.Length == HMACSHA256.HashSizeInBytes &&
               (CryptographicOperations.FixedTimeEquals(proof, Prove(_key, nodeId, streamId, host, port, challenge)) ||
                _accepted != null && CryptographicOperations.FixedTimeEquals(proof, Prove(_accepted, nodeId, streamId, host, port, challenge)))
            ? null
            : "it does not hold the cluster's signing secret, or answers another challenge";
    }
}
