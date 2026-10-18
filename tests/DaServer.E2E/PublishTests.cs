using System.Diagnostics;
using NUnit.Framework;

namespace DaServer.E2E;

/// <summary>
/// What `aspire publish` writes for the samples (S4): Docker Compose files and a Helm chart carry what DaServer.Aspire
/// adds (health checks, grace periods, a preStop pause, peers Services for seeds, the route check passing), and the chart
/// passes `helm lint` and renders (`helm template`, through the alpine/helm image).
/// </summary>
public class PublishTests
{
    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static IEnumerable<TestCaseData> AppHosts()
    {
        yield return new TestCaseData("samples/mmo/Sample.AppHost", new[] { "login", "game", "map", "gateway" }).SetName("{m}(Mmo)");
        yield return new TestCaseData("samples/flashsale/FlashSale.AppHost", new[] { "sale" }).SetName("{m}(FlashSale)");
    }

    /// <summary>The repository's root (where DaServer.slnx is), above the test's output directory.</summary>
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DaServer.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("DaServer.slnx not found above " + TestContext.CurrentContext.TestDirectory);
        }
    }

    [TestCaseSource(nameof(AppHosts)), CancelAfter(600_000)]
    public async Task ComposeFilesCarryHealthChecksGracePeriodsAndHealthyDependencies(string appHost, string[] groups,
        CancellationToken cancellationToken)
    {
        string output = await Publish(appHost, [], cancellationToken);
        string compose = await File.ReadAllTextAsync(Path.Combine(output, "docker-compose.yaml"), cancellationToken);
        foreach (string group in groups) Assert.That(compose, Does.Contain($"\n  {group}:"), $"one service per group: {group}");
        Assert.That(Count(compose, "--DaServer:Probe=/health"), Is.EqualTo(groups.Length), "each node group's health check is the node in probe mode");
        Assert.That(Count(compose, "stop_grace_period:"), Is.GreaterThanOrEqualTo(groups.Length), "a grace period above the stop budget");
        Assert.That(compose, Does.Contain("condition: \"service_healthy\""), "services wait for nodes to be healthy, not just started");
    }

    [TestCaseSource(nameof(AppHosts)), CancelAfter(600_000)]
    public async Task TheHelmChartCarriesProbesPreStopAndPeersAndRenders(string appHost, string[] groups,
        CancellationToken cancellationToken)
    {
        string output = await Publish(appHost, ["Publish=k8s"], cancellationToken);
        foreach (string group in groups)
        {
            string deployment = await File.ReadAllTextAsync(Path.Combine(output, "templates", group, "deployment.yaml"), cancellationToken);
            Assert.That(deployment, Does.Contain("readinessProbe").And.Contain("/health"), $"{group}: readiness is joined and not leaving");
            Assert.That(deployment, Does.Contain("livenessProbe").And.Contain("/alive"), $"{group}: liveness is no stalled shard");
            Assert.That(deployment, Does.Contain("terminationGracePeriodSeconds"), $"{group}: a grace period above the stop budget");
            Assert.That(deployment, Does.Contain("preStop"), $"{group}: a pause before leaving, so Services stop routing to it");
            string peers = await File.ReadAllTextAsync(Path.Combine(output, "templates", group, "peers.yaml"), cancellationToken);
            Assert.That(peers, Does.Contain($"{group}-peers").And.Contain("publishNotReadyAddresses: true").And.Contain("clusterIP: \"None\""),
                $"{group}: a headless Service listing every pod for the seeds");
        }

        if (!await Docker("version")) Assert.Inconclusive("Docker is not available: helm lint and template skipped");
        Assert.That(await Docker($"run --rm -v {output}:/chart:ro alpine/helm:3.18.4 lint /chart"), Is.True, "helm lint");
        Assert.That(await Docker($"run --rm -v {output}:/chart:ro alpine/helm:3.18.4 template release /chart"), Is.True, "helm template");
    }

    /// <summary>`aspire publish` of <paramref name="appHost"/> (its pipeline, the route check included) into a fresh directory.</summary>
    private static async Task<string> Publish(string appHost, string[] args, CancellationToken cancellationToken)
    {
        string output = Path.Combine(Path.GetTempPath(), "daserver-publish-" + Guid.NewGuid().ToString("N"));
        var (exit, log) = await Run("dotnet",
            ["run", "--project", Path.Combine(Root, appHost), "-c", Configuration, "--no-build", "--no-launch-profile", "--",
                "--operation", "publish", "--step", "publish", "--output-path", output, .. args], cancellationToken);
        Assert.That(exit, Is.Zero, "publish failed:\n" + log);
        return output;
    }

    private static async Task<bool> Docker(string arguments)
    {
        try
        {
            var (exit, log) = await Run("docker", arguments.Split(' '), CancellationToken.None);
            if (exit != 0) TestContext.Out.WriteLine(log);
            return exit == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // no docker on this machine
        }
    }

    private static async Task<(int Exit, string Log)> Run(string file, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output + await errors);
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
