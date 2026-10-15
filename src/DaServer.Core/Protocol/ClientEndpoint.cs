namespace DaServer.Rpc;

/// <summary>Where a client should connect: a client-facing node's public host and client port (the directory's answer).</summary>
[Nino.Core.NinoType]
public readonly partial record struct ClientEndpoint(string Host, int Port)
{
    /// <summary>The answer "stay where you are" (the node itself, or nodes behind a load balancer).</summary>
    public static ClientEndpoint Stay => new("", 0);
}
