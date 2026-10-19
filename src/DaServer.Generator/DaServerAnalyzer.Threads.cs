using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DaServer.Generator;

/// <summary>
/// DAS015: code of actors, services and systems runs on its shard thread, one message at a time; it never starts work on
/// other threads. An actor's state and a shard's world need no locks because one thread owns them, and the rule is simple
/// enough to never think about: asking another actor or service is fine (its answer comes back as mail), handing code to
/// another thread is not. Concurrency comes from many actors on many shards.
/// </summary>
public sealed partial class DaServerAnalyzer
{
    private const string StayOnShard = "code of actors, services and systems runs on its shard thread only (no locks needed there): await asynchronous IO directly, spread heavy work over actors or services on other shards, and use Cluster.SendAfter for timers";

    private static void CheckOffShard(OperationAnalysisContext context)
    {
        if (!OnShard(context.ContainingSymbol.ContainingType, out _)) return;
        string? what = context.Operation switch
        {
            IInvocationOperation { TargetMethod.Name: "ConfigureAwait" } call
                when call.TargetMethod.ContainingType is { } task && IsTask(task) && LeavesContext(call) => task.Name + ".ConfigureAwait",
            IInvocationOperation call when StartsWork(call.TargetMethod) => call.TargetMethod.ContainingType.Name + "." + call.TargetMethod.Name,
            IObjectCreationOperation { Constructor.ContainingType: { } type }
                when Support.Is(type, "System.Threading.Thread") || Support.Is(type, "System.Threading.Timer") ||
                     Support.Is(type, "System.Timers.Timer") => "new " + type.Name,
            _ => null,
        };
        if (what != null)
            context.ReportDiagnostic(Diagnostic.Create(Rules.OffShard, context.Operation.Syntax.GetLocation(), what, StayOnShard));
    }

    /// <summary>ConfigureAwait(false), or ConfigureAwaitOptions without ContinueOnCapturedContext: the rest runs elsewhere.</summary>
    private static bool LeavesContext(IInvocationOperation call)
    {
        if (call.Arguments.FirstOrDefault()?.Value.ConstantValue is not { HasValue: true } value) return true;
        return value.Value switch
        {
            bool continueOnContext => !continueOnContext,
            int options => (options & 1) == 0, // ConfigureAwaitOptions.ContinueOnCapturedContext
            _ => true,
        };
    }

    private static bool StartsWork(IMethodSymbol method)
    {
        var type = method.ContainingType;
        return method.Name switch
        {
            "Run" => Support.Is(type, "System.Threading.Tasks.Task"),
            "StartNew" => type.Name == "TaskFactory",
            "ContinueWith" => IsTask(type), // runs on the default scheduler, not the shard
            "QueueUserWorkItem" or "UnsafeQueueUserWorkItem" or "RegisterWaitForSingleObject" => Support.Is(type, "System.Threading.ThreadPool"),
            "For" or "ForEach" or "Invoke" or "ForAsync" or "ForEachAsync" => Support.Is(type, "System.Threading.Tasks.Parallel"),
            _ => false,
        };
    }
}
