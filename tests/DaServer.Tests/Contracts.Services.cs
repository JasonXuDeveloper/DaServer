using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- services

[NinoType, Message] public partial record struct EchoText(string Text) : IRequest<string>;
[NinoType, Message] public partial record struct WhichNode : IRequest<ulong>;
[NinoType, Message] public partial record struct WhoAsks : IRequest<long>;                 // the caller's origin id
[NinoType, Message] public partial record struct Guarded(int N) : IRequest<int>;           // a dispatch filter refuses N < 0
[NinoType, Message] public partial record struct Screened(int N) : IRequest<int>;          // ScreenRules refuses N < 0
[NinoType, Message] public partial record struct HowLong : IRequest<long>;                  // the caller's TimeLeft, in ms

/// <summary>A [Filter]: registered by the generated registration, no startup code.</summary>
public static class ScreenRules
{
    [Filter] public static RpcStatus Screen(object target, in Screened message, in CallContext context) =>
        message.N < 0 ? RpcStatus.Forbidden : RpcStatus.Ok;
}

/// <summary>Event: delivered once to each service type handling it.</summary>
[NinoType, Message] public partial record struct Ping(int Id);

public sealed class EchoService : Service
{
    [Handle] public string On(EchoText m) => m.Text;

    [Handle] public ulong On(WhichNode _) => Cluster.NodeId;

    [Handle] public long On(WhoAsks _) => Caller.Id;

    [Handle] public int On(Guarded m) => m.N;

    [Handle] public int On(Screened m) => m.N;

    [Handle] public long On(HowLong _) => (long)TimeLeft.TotalMilliseconds;

    [Handle] public void On(Ping e) => Observed.Events.Enqueue($"echo:{e.Id}:{Cluster.NodeId:x}");
}

/// <summary>A second service handling Ping: every handler type receives each event once.</summary>
public sealed class AuditService : Service
{
    [Handle] public void On(Ping e) => Observed.Events.Enqueue($"audit:{e.Id}");
}

/// <summary>To the toy feature's addresses (FeatureTests).</summary>
[NinoType, Message] public partial record struct Probe(int N);

/// <summary>Service traffic without payload allocations (allocation tests): one-way, request and published event.</summary>
[NinoType, Message] public partial record struct Tally(int By);
[NinoType, Message] public partial record struct TallyAsk(int By) : IRequest<int>;
[NinoType, Message] public partial record struct Tallied(int By);

/// <summary>Drives <see cref="TallyService"/> from an actor: bytes allocated meanwhile.</summary>
[NinoType, Message] public partial record struct ServiceTraffic(int Count) : IRequest<long>;

public sealed class TallyService : Service
{
    [Handle] public void On(Tally m) => Interlocked.Add(ref Observed.Tallies, m.By);

    [Handle] public int On(TallyAsk m) => (int)Interlocked.Add(ref Observed.Tallies, m.By);

    [Handle] public void On(Tallied m) => Interlocked.Add(ref Observed.Tallies, m.By);
}

/// <summary>A second service handling <see cref="Tallied"/>: a publish reaches both.</summary>
public sealed class TallyAuditService : Service
{
    [Handle] public void On(Tallied m) => Interlocked.Add(ref Observed.Tallies, m.By);
}
