using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DaServer.Generator;

/// <summary>
/// DAS009: code that runs on a shard thread (actors, services, systems) never waits synchronously. The shard runs
/// every actor of the shard on that thread, so a blocked thread stops them all, and a wait for a reply that the same
/// shard must deliver never ends.
/// </summary>
public sealed partial class DaServerAnalyzer
{
    private const string AwaitIt = "await it (only this actor's turn waits)";
    private const string SendLater = "send a message later with Cluster.SendAfter (the actor stays free), or give a system an UpdateInterval";
    private const string NoLocks = "shard state needs no lock: one thread runs it, and other threads reach it through messages";

    private static void CheckBlocking(OperationAnalysisContext context)
    {
        var type = context.ContainingSymbol.ContainingType;
        if (type == null) return;
        bool system = Support.DerivesFrom(type, Support.EcsSystem);
        if (!(system || Support.DerivesFrom(type, Support.ActorBase) || Support.DerivesFrom(type, Support.ServiceBase))) return;

        if (system && context.Operation is IInvocationOperation { TargetMethod.Name: "Ask" } ask && IsCluster(ask.TargetMethod.IsExtensionMethod ? Unwrap(ask.Arguments.FirstOrDefault()?.Value) : Unwrap(ask.Instance)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.AskInSystem, ask.Syntax.GetLocation()));
            return;
        }

        var (what, replacement) = context.Operation switch
        {
            IInvocationOperation invocation => Blocking(invocation.TargetMethod),
            IPropertyReferenceOperation { Property.Name: "Result" } property when IsTask(property.Property.ContainingType) =>
                (property.Property.ContainingType.Name + ".Result", AwaitIt),
            ILockOperation => ("lock", NoLocks),
            _ => (null, null),
        };
        if (what != null)
            context.ReportDiagnostic(Diagnostic.Create(Rules.BlockingCall, context.Operation.Syntax.GetLocation(), what, replacement));
    }

    private static (string? What, string? Replacement) Blocking(IMethodSymbol method)
    {
        var type = method.ContainingType;
        string name = type.Name + "." + method.Name;
        switch (method.Name)
        {
            case "Wait" or "WaitAll" or "WaitAny" when IsTask(type):
            case "GetResult" when type.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices" && type.Name.Contains("Awaiter"):
                return (name, AwaitIt);
            case "Delay" when Support.Is(type, "System.Threading.Tasks.Task"):
            case "Sleep" when Support.Is(type, "System.Threading.Thread"):
                return (name, SendLater);
            case "Enter" or "TryEnter" or "Wait" when Support.Is(type, "System.Threading.Monitor"):
            case "Wait" when Support.Is(type, "System.Threading.SemaphoreSlim") || Support.Is(type, "System.Threading.ManualResetEventSlim"):
            case "WaitOne" or "WaitAll" or "WaitAny" when Support.DerivesFrom(type, "System.Threading.WaitHandle") || Support.Is(type, "System.Threading.WaitHandle"):
                return (name, NoLocks);
            default:
                return (null, null);
        }
    }

    private static bool IsCluster(ITypeSymbol? type) => type != null && (
        Support.Is(type, "DaServer.Runtime.ICluster") || Support.Implements(type, "DaServer.Runtime.ICluster"));

    private static bool IsTask(INamedTypeSymbol type) =>
        type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks" && type.Name is "Task" or "ValueTask";
}
