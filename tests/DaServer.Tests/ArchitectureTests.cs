using System.Reflection;
using System.Text.RegularExpressions;

namespace DaServer.Tests;

/// <summary>
/// Dependency rules (ADR-045, ADR-052): contracts, shared transport and the client SDK never depend on the server
/// runtime or EasyEcs (they compile into Unity clients), and the runtime never depends on its capabilities.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Abstractions = typeof(IAddress).Assembly;
    private static readonly Assembly Core = typeof(FrameWriter).Assembly;
    private static readonly Assembly Client = typeof(ClusterClient).Assembly;
    private static readonly Assembly Runtime = typeof(Node).Assembly;

    [Test]
    public void PackagesDependOnlyDownwards()
    {
        Assert.That(References(Abstractions), Does.Not.Contain("DaServer.Core").And.Not.Contain("DaServer.Client")
            .And.Not.Contain("DaServer").And.Not.Contain("EasyEcs"));
        Assert.That(References(Core), Does.Contain("DaServer.Abstractions").And.Not.Contain("DaServer.Client")
            .And.Not.Contain("DaServer").And.Not.Contain("EasyEcs"));
        Assert.That(References(Client), Does.Contain("DaServer.Core").And.Not.Contain("DaServer").And.Not.Contain("EasyEcs"));
        Assert.That(References(Runtime), Does.Contain("DaServer.Core").And.Not.Contain("DaServer.Client"));
    }

    [Test]
    public void TheRuntimeDoesNotDependOnClientConnections()
    {
        // The client connections feature's own types (not the contract types every node shares).
        var feature = Runtime.GetTypes()
            .Where(static t => t.Namespace == "DaServer.Clients" && !t.IsNested && !t.Name.StartsWith('<'))
            .Select(static t => t.Name.Split('`')[0])
            .Distinct()
            .ToArray();
        Assert.That(feature, Does.Contain(nameof(ClientConnections)), "the scan finds the feature's types");

        var uses = new Regex(@"(?<![.\w])(" + string.Join("|", feature) + @")\b"); // not member accesses (x.Session)
        var root = Path.Combine(RepositoryRoot(), "src", "DaServer");
        var offenders = new List<string>();
        foreach (var folder in new[] { "Runtime", "Cluster", "Rpc", "Features", "Ingress", "Net", "Concurrency" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
        foreach (Match match in uses.Matches(File.ReadAllText(file)))
            offenders.Add($"{Path.GetRelativePath(root, file)}: {match.Value}");
        Assert.That(offenders, Is.Empty);
    }

    private static string[] References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(static a => a.Name!).ToArray();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DaServer.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
