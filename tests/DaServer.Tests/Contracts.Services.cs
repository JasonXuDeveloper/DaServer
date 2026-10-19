using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- services

[NinoType, Message] public partial record struct EchoText(string Text) : IRequest<string>;
[NinoType, Message] public partial record struct WhichNode : IRequest<ulong>;
[NinoType, Message] public partial record struct WhoAsks : IRequest<long>;                 // the caller's origin id
[NinoType, Message] public partial record struct Guarded(int N) : IRequest<int>;           // a dispatch filter refuses N < 0
[NinoType, Message] public partial record struct Screened(int N) : IRequest<int>;          // ScreenRules refuses N < 0
[NinoType, Message] public partial record struct HowLong : IRequest<long>;                  // the time the caller still waits, in ms

/// <summary>A [Filter]: registered by the generated registration, no startup code.</summary>
public static class ScreenRules
{
    [Filter] public static RpcStatus Screen(in Context context, in Screened message) =>
        message.N < 0 ? RpcStatus.Forbidden : RpcStatus.Ok;
}

/// <summary>Event: delivered once to each service type handling it.</summary>
[NinoType, Message] public partial record struct Ping(int Id);

[Service]
public static class EchoService
{
    [Handle] internal static string On(in EchoText m) => m.Text;

    [Handle] internal static ulong On(in WhichNode _, in Context ctx) => ctx.Cluster.NodeId;

    [Handle] internal static long On(in WhoAsks _, in Context ctx) => ctx.Caller.Id;

    [Handle] internal static int On(in Guarded m) => m.N;

    [Handle] internal static int On(in Screened m) => m.N;

    [Handle] internal static long On(in HowLong _, in Context ctx) => ctx.Deadline - Environment.TickCount64;

    [Handle] internal static void On(in Ping e, in Context ctx) => Observed.Events.Enqueue($"echo:{e.Id}:{ctx.Cluster.NodeId:x}");
}

/// <summary>A second service handling Ping: every handler type receives each event once.</summary>
[Service]
public static class AuditService
{
    [Handle] internal static void On(in Ping e) => Observed.Events.Enqueue($"audit:{e.Id}");
}

/// <summary>To the toy feature's addresses (FeatureTests).</summary>
[NinoType, Message] public partial record struct Probe(int N);

/// <summary>Service traffic without payload allocations (allocation tests): one-way, request and published event.</summary>
[NinoType, Message] public partial record struct Tally(int By);
[NinoType, Message] public partial record struct TallyAsk(int By) : IRequest<int>;
[NinoType, Message] public partial record struct Tallied(int By);

[Service]
public static class TallyService
{
    [Handle] internal static void On(in Tally m) => Interlocked.Add(ref Observed.Tallies, m.By);

    [Handle] internal static int On(in TallyAsk m) => (int)Interlocked.Add(ref Observed.Tallies, m.By);

    [Handle] internal static void On(in Tallied m) => Interlocked.Add(ref Observed.Tallies, m.By);
}

/// <summary>A second service handling <see cref="Tallied"/>: a publish reaches both.</summary>
[Service]
public static class TallyAuditService
{
    [Handle] internal static void On(in Tallied m) => Interlocked.Add(ref Observed.Tallies, m.By);
}
