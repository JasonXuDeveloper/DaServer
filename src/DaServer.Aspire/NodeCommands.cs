using Aspire.Hosting.ApplicationModel;
using DaServer.Hosting;

namespace Aspire.Hosting;

/// <summary>
/// A node's dashboard commands (A5), also run by <c>aspire resource {node} {command}</c>, the Aspire MCP server and tests:
/// they call the node's admin endpoints on its HTTP endpoint with the token the cluster's secret derives.
/// </summary>
internal static class NodeCommands
{
    public static void Add(IResourceBuilder<ProjectResource> node, IResourceBuilder<ParameterResource> secret)
    {
        node.WithHttpCommand("/admin/membership", "Membership", "http", "membership", Options(secret, HttpMethod.Get,
            "The cluster as this node sees it: members, their status, build and load, the coordinator", "PeopleTeam"));
        node.WithHttpCommand("/admin/stats", "Stats", "http", "stats", Options(secret, HttpMethod.Get,
            "Loaded actors, queued and dropped messages, failed handlers, stalled shards", "DataBarVertical"));
        var drain = Options(secret, HttpMethod.Post, "Leave the cluster and stop gracefully: clients move, every actor is saved", "ArrowExit");
        drain.ConfirmationMessage = "Drain this node? It leaves the cluster and stops.";
        node.WithHttpCommand("/admin/drain", "Drain", "http", "drain", drain);
        var unload = Options(secret, HttpMethod.Post, "Unload every actor of a type on this node (saved; their next message loads them again)",
            "ArrowSync", type: true);
        node.WithHttpCommand("/admin/unload", "Unload actors", "http", "unload", unload);
    }

    private static HttpCommandOptions Options(IResourceBuilder<ParameterResource> secret, HttpMethod method, string description, string icon,
        bool type = false)
    {
        var options = new HttpCommandOptions
        {
            Method = method,
            Description = description,
            IconName = icon,
            ResultMode = HttpCommandResultMode.Auto,
            PrepareRequest = async context =>
            {
                context.Request.Headers.Authorization = new("Bearer", AdminToken.For((await secret.Resource.GetValueAsync(context.CancellationToken))!));
                if (type)
                    context.Request.RequestUri = new Uri(context.Request.RequestUri!,
                        "/admin/unload/" + Uri.EscapeDataString(context.Arguments.GetString("type") ?? ""));
            },
        };
        if (type) options.Arguments = [new InteractionInput { Name = "type", Label = "Actor type", InputType = InputType.Text, Required = true }];
        return options;
    }
}
