namespace DaServer.Rpc;

/// <summary>
/// Stable wire ids. Method id = FNV-1a 32 of "Namespace.Interface.Method(ParamType,...)".
/// The source generator computes the same value at compile time; keep both implementations identical.
/// </summary>
public static class RpcIds
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
