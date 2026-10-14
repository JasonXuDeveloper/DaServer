using System;
using DaServer.Protocol;

namespace DaServer.Rpc;

/// <summary>Failure of a remote call.</summary>
public sealed class RpcException : Exception
{
    public RpcException(RpcStatus status, string? message = null)
        : base(message ?? status.ToString())
    {
        Status = status;
    }

    public RpcStatus Status { get; }
}
