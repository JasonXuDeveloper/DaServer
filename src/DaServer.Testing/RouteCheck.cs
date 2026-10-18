using System.Reflection;
using DaServer.Runtime;

namespace DaServer.Testing;

/// <summary>
/// Route completeness of a cluster (N4): given the node projects that make it up, every message their contracts declare
/// reaches a handler, and every request reaches one service type at most. Nodes route by message type at run time, so
/// a message nothing handles, or a request two service types answer, only shows when it is sent; this finds it in a
/// test, before the cluster runs (Orleans finds it at compile time through grain interfaces).
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
        var handledBy = new Dictionary<Type, List<Type>>();
        var contracts = new HashSet<Assembly>();
        foreach (var project in nodeProjects)
        {
            contracts.Add(project);
            foreach (var name in project.GetReferencedAssemblies())
                if (!IsFramework(name.Name)) contracts.Add(Assembly.Load(name));
            foreach (var type in Types(project))
            {
                if (type.IsAbstract || !(typeof(Actor).IsAssignableFrom(type) || typeof(Service).IsAssignableFrom(type))) continue;
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<HandleAttribute>() == null || method.GetParameters() is not [var parameter]) continue;
                    if (!handledBy.TryGetValue(parameter.ParameterType, out var handlers)) handledBy[parameter.ParameterType] = handlers = [];
                    if (!handlers.Contains(type)) handlers.Add(type);
                }
            }
        }

        var problems = new List<string>();
        foreach (var assembly in contracts)
            foreach (var message in Types(assembly))
            {
                if (message.GetCustomAttribute<MessageAttribute>() == null) continue;
                bool fromClient = message.GetCustomAttribute<FromClientAttribute>() != null;
                bool toClient = message.GetCustomAttribute<ToClientAttribute>() != null;
                if (toClient && !fromClient) continue; // clients handle it
                handledBy.TryGetValue(message, out var handlers);
                if (handlers == null)
                {
                    problems.Add($"{message.FullName}: no actor or service of these node projects handles it" +
                                 (fromClient ? " (a [FromClient] message: clients' sends are refused)" : ""));
                    continue;
                }

                var services = handlers.Where(static h => typeof(Service).IsAssignableFrom(h)).ToList();
                if (IsRequest(message) && services.Count > 1)
                    problems.Add($"{message.FullName}: a request answered by {services.Count} service types ({string.Join(", ", services.Select(static s => s.Name))}): it is refused as ambiguous");
            }

        return problems;
    }

    /// <summary>Fail with every routing problem of the cluster (<see cref="Problems"/>).</summary>
    public static void Check(params Assembly[] nodeProjects)
    {
        var problems = Problems(nodeProjects);
        if (problems.Count > 0)
            throw new InvalidOperationException("Messages without a route:\n  " + string.Join("\n  ", problems));
    }

    private static bool IsRequest(Type message)
    {
        foreach (var i in message.GetInterfaces())
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)) return true;
        return false;
    }

    private static bool IsFramework(string? name) =>
        name == null || name is "DaServer" or "DaServer.Abstractions" or "DaServer.Core" or "DaServer.Client" or "DaServer.Hosting"
            or "DaServer.Testing" or "EasyEcs.Core" || name.StartsWith("System", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft", StringComparison.Ordinal) || name.StartsWith("Nino", StringComparison.Ordinal) ||
        name is "netstandard" or "mscorlib";

    private static Type[] Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return [.. e.Types.Where(static t => t != null)!];
        }
    }
}
