using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Stops a cluster's nodes before the rest of the AppHost when it runs (Ctrl+C, the end of a test). Aspire stops every
/// resource at once, so a database would go before the nodes' last saves; here every node stops gracefully first
/// (leave, drain, unload and save its actors), then the AppHost's other resources go. Published deployments order this
/// themselves (Compose stops in reverse <c>depends_on</c>; Kubernetes keeps databases apart).
/// </summary>
internal sealed class NodesStopFirst(DaServerCluster cluster, ResourceNotificationService notifications,
    ResourceCommandService commands, IHostApplicationLifetime lifetime, ILogger<NodesStopFirst> logger)
    : IHostedService
{
    /// <summary>
    /// Stop the nodes when the AppHost is asked to stop, before its shutdown begins: the callback is registered last, so
    /// it runs first, and the host's stop proceeds once it returns. (Nodes that exit after the shutdown began leave the
    /// orchestrator waiting out the AppHost's whole shutdown timeout.)
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStopping.Register(static s => ((NodesStopFirst)s!).StopNodes().GetAwaiter().GetResult(), this);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Every node stops at once, within the longest stop budget of the cluster's groups.</summary>
    private async Task StopNodes()
    {
        if (cluster.Nodes.Count == 0) return;
        using var budget = new CancellationTokenSource(cluster.StopBudget);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var stops = new Task[cluster.Nodes.Count];
        for (int i = 0; i < stops.Length; i++) stops[i] = Stop(cluster.Nodes[i].Resource.Name, budget.Token);
        await Task.WhenAll(stops);
        logger.LogInformation("Stopped {Count} nodes before the other resources in {Ms} ms", stops.Length, (long)started.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Ask a node's process to stop (SIGTERM, what a platform sends) and wait for it to exit: to the orchestrator it is a
    /// process that ended on its own. (Aspire's stop command, used where there are no signals, leaves the orchestrator
    /// waiting out the AppHost's whole shutdown timeout once it runs during shutdown.)
    /// </summary>
    private async Task Stop(string node, CancellationToken cancellationToken)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && ProcessOf(node) is { } process)
            {
                using (process)
                {
                    process.Signal(System.Runtime.InteropServices.PosixSignal.SIGTERM);
                    await process.WaitForExitAsync(cancellationToken);
                }

                // A node exits with 0 only when every actor was unloaded and saved within its budget. The orchestrator
                // (the process's parent) reports the code: this process may not read another's.
                using var reported = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                reported.CancelAfter(TimeSpan.FromSeconds(2));
                var exited = await notifications.WaitForResourceAsync(node, static e => e.Snapshot.ExitCode is not null, reported.Token);
                int code = exited.Snapshot.ExitCode!.Value;
                logger.Log(code == 0 ? LogLevel.Information : LogLevel.Warning, "Node {Node} stopped with exit code {ExitCode}", node, code);
                return;
            }

            await commands.ExecuteCommandAsync(node, KnownResourceCommands.StopCommand, cancellationToken);
        }
        catch (Exception e) // exited already, never started, or out of time: the orchestrator stops the rest anyway
        {
            logger.LogWarning(e, "Stopping node {Node} before the other resources failed", node);
        }
    }

    /// <summary>The running process of resource <paramref name="node"/> (its snapshot's <c>executable.pid</c>), if any.</summary>
    private System.Diagnostics.Process? ProcessOf(string node)
    {
        if (!notifications.TryGetCurrentState(node, out var current)) return null;
        foreach (var property in current.Snapshot.Properties)
            if (property.Name == "executable.pid" && property.Value is not null &&
                int.TryParse(property.Value.ToString(), out int pid) &&
                System.Diagnostics.Process.TryGetProcessById(pid, out var process))
                return process;
        return null;
    }

}
