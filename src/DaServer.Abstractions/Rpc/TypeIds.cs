using System.Reflection;

namespace DaServer;

/// <summary>
/// Type ids: the stable identity of a type on the wire and in stores (messages, actor id types, services, persisted
/// components). <see cref="TypeIdAttribute">[TypeId]</see> or FNV-1a 32 of the full name (UTF-16 code units), never 0.
/// The analyzer computes the same value to report collisions at build time; keep both implementations identical.
/// </summary>
internal static class TypeIds
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    public static uint Of(Type type) => type.GetCustomAttribute<TypeIdAttribute>()?.Id ?? Fnv1a(type.FullName ?? type.Name);

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

/// <summary>Type id of <typeparamref name="T"/> (<see cref="TypeIdAttribute">[TypeId]</see> or FNV-1a of its full name), computed once.</summary>
public static class TypeId<T>
{
    public static readonly uint Value = TypeIds.Of(typeof(T));
}
