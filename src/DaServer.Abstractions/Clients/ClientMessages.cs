namespace DaServer;

/// <summary>A message type clients may send (<see cref="FromClientAttribute">[FromClient]</see>).</summary>
public readonly record struct ClientMessageInfo(uint Id, Type Type);

/// <summary>
/// The messages clients may send, registered by the registrations generated into the assemblies declaring them (ADR-012)
/// (no reflection scanning, no configuration). Client-facing nodes refuse every other message from clients.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ClientMessages
{
    private static readonly Lock Gate = new();
    private static FrozenDictionary<uint, ClientMessageInfo> _messages = FrozenDictionary<uint, ClientMessageInfo>.Empty;

    /// <summary>Register a client message type. Idempotent per type; throws on wire-id collisions.</summary>
    public static void Register<T>() where T : struct
    {
        var info = new ClientMessageInfo(MessageType<T>.WireId, typeof(T));
        lock (Gate)
        {
            if (_messages.TryGetValue(info.Id, out var existing))
            {
                if (existing.Type == info.Type) return;
                throw new InvalidOperationException(
                    $"Message id collision 0x{info.Id:x8}: {info.Type} and {existing.Type}. Use [WireId] on one of them.");
            }

            var next = new Dictionary<uint, ClientMessageInfo>(_messages) { [info.Id] = info };
            Volatile.Write(ref _messages, next.ToFrozenDictionary());
        }
    }

    /// <summary>The client message with wire id <paramref name="id"/>.</summary>
    public static bool TryGet(uint id, out ClientMessageInfo info) => Volatile.Read(ref _messages).TryGetValue(id, out info);
}
