namespace DaServer.Rpc;

/// <summary>
/// Stable wire ids: FNV-1a 32 of a message type's full name (UTF-16 code units), never 0.
/// The analyzer computes the same value to report collisions at build time; keep both implementations identical.
/// </summary>
internal static class WireIds
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    public static uint Fnv1a(string text)
    {
        uint hash = FnvOffset;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            hash = (hash ^ (byte)c) * FnvPrime;
            hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
        }

        // 0 is reserved for "no method"
        return hash == 0 ? 1u : hash;
    }
}

/// <summary>Id of type <typeparamref name="T"/>: FNV-1a of its full name, computed once (handler types, group scopes).</summary>
internal static class TypeId<T>
{
    public static readonly uint Value = WireIds.Fnv1a(typeof(T).FullName ?? typeof(T).Name);
}
