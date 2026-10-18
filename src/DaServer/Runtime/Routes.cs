using System.Reflection;

namespace DaServer.Runtime;

/// <summary>
/// The routes of a node project, as <see cref="RouteRules"/> lines: the messages the contracts it references declare,
/// and the messages its actors and services handle (what its nodes host). Read by reflection: tests
/// (<c>DaServer.Testing.RouteCheck</c>) and a node's check mode (<c>DaServer__ListRoutes</c>), never on a running node.
/// </summary>
internal static class Routes
{
    public static List<string> Of(Assembly nodeProject)
    {
        var lines = new List<string>();
        var contracts = new HashSet<Assembly> { nodeProject };
        foreach (var name in nodeProject.GetReferencedAssemblies())
            if (!IsFramework(name.Name)) contracts.Add(Assembly.Load(name));
        foreach (var assembly in contracts)
            foreach (var message in Types(assembly))
            {
                if (message.GetCustomAttribute<MessageAttribute>() == null) continue;
                string flags = (message.GetCustomAttribute<FromClientAttribute>() != null ? "F" : "") +
                               (message.GetCustomAttribute<ToClientAttribute>() != null ? "T" : "") + (IsRequest(message) ? "R" : "");
                lines.Add($"M\t{message.FullName}\t{flags}");
            }

        foreach (var type in Types(nodeProject))
        {
            bool service = typeof(Service).IsAssignableFrom(type);
            if (type.IsAbstract || !(service || typeof(Actor).IsAssignableFrom(type))) continue;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (method.GetCustomAttribute<HandleAttribute>() != null && method.GetParameters() is [var parameter])
                    lines.Add($"H\t{parameter.ParameterType.FullName}\t{type.FullName}\t{(service ? "S" : "A")}");
        }

        return lines;
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
