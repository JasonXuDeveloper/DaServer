namespace DaServer;

/// <summary>
/// Type id of <typeparamref name="T"/>: the stable identity of a type on the wire and in stores (messages, actor id
/// types, services, persisted components). It is Nino's type id (<see cref="NinoTypeId"/>): FNV-1a of the full name,
/// or of the name in <see cref="NinoFormerNameAttribute">[NinoFormerName]</see>, which keeps a renamed type's identity
/// and stored records or resolves a collision. The analyzer computes the same value to report collisions at build time.
/// </summary>
public static class TypeId<T>
{
    public static readonly uint Value = (uint)NinoTypeId.Of(typeof(T));
}
