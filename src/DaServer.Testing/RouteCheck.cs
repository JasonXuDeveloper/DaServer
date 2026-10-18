using System.Reflection;
using DaServer.Runtime;

namespace DaServer.Testing;

/// <summary>
/// Route completeness of a cluster (N4): given the node projects that make it up, every message their contracts declare
/// reaches a handler, and every request reaches one service type at most. Nodes route by message type at run time, so
/// a message nothing handles, or a request two service types answer, only shows when it is sent; this finds it in a
/// test, before the cluster runs (Orleans finds it at compile time through grain interfaces). An AppHost declaring the
/// cluster with <c>AddDaServerCluster</c> runs the same check before it publishes or deploys.
/// </summary>
public static class RouteCheck
{
    /// <summary>
    /// The routing problems of a cluster made of <paramref name="nodeProjects"/> (each node project's assembly: what
    /// its nodes host): messages declared in the contracts the projects reference that no actor or service handles
    /// (<c>[ToClient]</c>-only messages excepted: clients handle them), and requests that two or more service types
    /// handle (refused at run time as ambiguous). Empty when every message has its handler.
    /// </summary>
    public static IReadOnlyList<string> Problems(params Assembly[] nodeProjects)
    {
        var routes = new List<string>();
        foreach (var project in nodeProjects) routes.AddRange(Routes.Of(project));
        return RouteRules.Problems(routes);
    }

    /// <summary>Fail with every routing problem of the cluster (<see cref="Problems"/>).</summary>
    public static void Check(params Assembly[] nodeProjects)
    {
        var problems = Problems(nodeProjects);
        if (problems.Count > 0)
            throw new InvalidOperationException("Messages without a route:\n  " + string.Join("\n  ", problems));
    }
}
