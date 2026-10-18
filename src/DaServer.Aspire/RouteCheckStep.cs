#pragma warning disable ASPIREPIPELINES001 // pipeline steps: experimental in Aspire 13.6
using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using DaServer.Runtime;

namespace Aspire.Hosting;

/// <summary>
/// Route completeness before a cluster ships (A11): <c>aspire publish</c> and <c>aspire deploy</c> first start each node
/// project of the cluster in check mode (<c>DaServer__ListRoutes</c>: the node lists the messages its contracts declare
/// and its actors and services handle, then exits before it starts), and stop with the problems if a message reaches no
/// handler or a request two service types (the rules of <c>DaServer.Testing.RouteCheck</c>). The listing comes from the
/// node's own build, so it checks exactly what the containers will run.
/// </summary>
internal static class RouteCheckStep
{
    public static void Add(IDistributedApplicationBuilder builder, DaServerCluster cluster)
    {
        string name = $"{cluster.Name}-routes";
        builder.Pipeline.AddStep(name, context => Check(cluster, context),
            requiredBy: new[] { WellKnownPipelineSteps.Publish, WellKnownPipelineSteps.Deploy });
        // Every resource's step (writing its Compose service or manifests, building or pushing its image, deploying it)
        // waits for the check: nothing is written or shipped for a cluster that fails it.
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            foreach (var step in context.Steps)
                if (step.Resource != null && step.Name != name) step.DependsOn(name);
            return Task.CompletedTask;
        });
    }

    private static async Task Check(DaServerCluster cluster, PipelineStepContext context)
    {
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in cluster.Nodes) projects.Add(node.Resource.GetProjectMetadata().ProjectPath);

        var task = await context.ReportingStep.CreateTaskAsync($"Checking the routes of {projects.Count} node projects", context.CancellationToken);
        var listings = await Task.WhenAll(projects.Select(project => List(project, context.CancellationToken)));
        var problems = RouteRules.Problems(listings.SelectMany(static lines => lines));
        if (problems.Count == 0)
        {
            await task.CompleteAsync("Every message has its handler", CompletionState.Completed, context.CancellationToken);
            return;
        }

        string report = "Messages without a route:\n  " + string.Join("\n  ", problems);
        await task.CompleteAsync(report, CompletionState.CompletedWithError, context.CancellationToken);
        throw new InvalidOperationException(report);
    }

    /// <summary>Build and run <paramref name="project"/> in check mode; its routes, one per line.</summary>
    private static async Task<string[]> List(string project, CancellationToken cancellation)
    {
        string file = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo("dotnet", ["run", "--project", project, "-c", "Release", "--no-launch-profile"])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                Environment = { ["DaServer__ListRoutes"] = file },
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellation);
            var errors = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Listing the routes of {Path.GetFileName(project)} failed ({process.ExitCode}):\n{await output}{await errors}");
            return await File.ReadAllLinesAsync(file, cancellation);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
