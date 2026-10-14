using System.Threading.Tasks;
using DaServer.Buffers;

namespace DaServer.Rpc;

/// <summary>Who a call is addressed to.</summary>
public enum RpcTargetKind : byte
{
    /// <summary>Stateless service: any hosting node.</summary>
    Service = 1,

    /// <summary>Keyed actor: the owner node of the key.</summary>
    Actor = 2,

    /// <summary>One client session (push). Key = session id, gateway = <see cref="SessionRef.GatewayNodeId"/>.</summary>
    Session = 3,

    /// <summary>Every session in a group on every gateway (broadcast). Key = group id.</summary>
    Group = 4,
}

/// <summary>
/// Transport used by generated proxies. Implemented by the server node runtime and by the game client.
/// All methods take ownership of <c>request</c> (a frame whose payload holds the serialized arguments).
/// </summary>
public interface IRpcInvoker
{
    ValueTask<T> Call<T>(FrameWriter request, uint methodId, RpcTargetKind kind, long key);

    ValueTask CallVoid(FrameWriter request, uint methodId, RpcTargetKind kind, long key);

    /// <summary>Fire-and-forget (void methods, pushes, broadcasts).</summary>
    void Send(FrameWriter request, uint methodId, RpcTargetKind kind, long key, ulong gatewayNodeId = 0);
}
