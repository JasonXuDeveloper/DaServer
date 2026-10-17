using Aspire.Hosting;
using Aspire.Hosting.Kubernetes.Resources;
using Microsoft.Extensions.Configuration;

/// <summary>What the samples' AppHosts share for `aspire publish` (linked into each).</summary>
static class Publishing
{
    /// <summary>
    /// Where `aspire publish` writes the cluster: Docker Compose files (default) or a Helm chart for Kubernetes
    /// (`-- Publish=k8s`). Aspire publishes a resource to one environment, so the AppHost picks one; true for Kubernetes.
    /// </summary>
    public static bool AddPublishEnvironment(this IDistributedApplicationBuilder builder)
    {
        bool kubernetes = builder.Configuration["Publish"] == "k8s";
        if (kubernetes) builder.AddKubernetesEnvironment("k8s");
        else builder.AddDockerComposeEnvironment("compose");
        return kubernetes;
    }

    /// <summary>
    /// A vertical autoscaler of <paramref name="workload"/>'s Deployment: <c>Off</c> only recommends requests,
    /// <c>InPlaceOrRecreate</c> resizes pods in place when it can (Kubernetes 1.33+) and evicts them otherwise.
    /// </summary>
    public static Manifest VerticalAutoscaler(Aspire.Hosting.Kubernetes.KubernetesResource workload, string updateMode)
    {
        var autoscaler = new Manifest("autoscaling.k8s.io/v1", "VerticalPodAutoscaler", $"{workload.Name}-resources");
        autoscaler.Spec["targetRef"] = new Dictionary<string, object>
            { ["apiVersion"] = "apps/v1", ["kind"] = "Deployment", ["name"] = workload.Workload!.Metadata.Name };
        autoscaler.Spec["updatePolicy"] = new Dictionary<string, object> { ["updateMode"] = updateMode };
        return autoscaler;
    }
}

/// <summary>A Kubernetes object Aspire has no type for (its spec as plain YAML data).</summary>
sealed class Manifest : BaseKubernetesResource
{
    public Manifest(string apiVersion, string kind, string name) : base(apiVersion, kind) => Metadata.Name = name;

    [YamlDotNet.Serialization.YamlMember(Alias = "spec")]
    public Dictionary<string, object> Spec { get; } = new();
}
