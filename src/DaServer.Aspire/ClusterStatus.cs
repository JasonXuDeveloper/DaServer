using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

/// <summary>A DaServer cluster in the dashboard (run mode): the parent of its nodes.</summary>
public sealed class DaServerClusterResource(string name) : Resource(name);

/// <summary>
/// Keeps the cluster resource's state at "n/m joined" (A5): a node counts once its readiness passes, that is once it
/// joined and is not leaving (<c>/health</c>). Fed by the resource notifications the dashboard itself shows.
/// </summary>
internal sealed class ClusterStatus(DaServerCluster cluster, DaServerClusterResource resource, ResourceNotificationService notifications)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var joined = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            await foreach (var update in notifications.WatchAsync(stoppingToken))
            {
                if (!IsNode(update.Resource)) continue;
                joined[update.Resource.Name] = update.Snapshot.HealthStatus == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy &&
                                               update.Snapshot.State?.Text == KnownResourceStates.Running;
                int count = joined.Values.Count(static j => j), total = cluster.Nodes.Count;
                await notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = new ResourceStateSnapshot($"{count}/{total} joined", count == total ? KnownResourceStateStyles.Success : KnownResourceStateStyles.Info),
                });
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private bool IsNode(IResource resource)
    {
        foreach (var node in cluster.Nodes)
            if (ReferenceEquals(node.Resource, resource)) return true;
        return false;
    }
}
