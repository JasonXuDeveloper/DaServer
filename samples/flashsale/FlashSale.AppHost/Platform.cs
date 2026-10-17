using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;

/// <summary>Kubernetes objects this sample asks of the platform, beside what Aspire and DaServer.Aspire publish.</summary>
static class Platform
{
    /// <summary>A horizontal autoscaler keeping the workload's average CPU near <paramref name="percent"/> of its request.</summary>
    public static Manifest ScaleOnCpu(KubernetesResource workload, int min, int max, int percent)
    {
        var autoscaler = new Manifest("autoscaling/v2", "HorizontalPodAutoscaler", $"{workload.Name}-autoscaler");
        autoscaler.Spec["scaleTargetRef"] = new Dictionary<string, object>
            { ["apiVersion"] = "apps/v1", ["kind"] = "Deployment", ["name"] = workload.Workload!.Metadata.Name };
        autoscaler.Spec["minReplicas"] = min;
        autoscaler.Spec["maxReplicas"] = max;
        autoscaler.Spec["metrics"] = new[]
        {
            new Dictionary<string, object>
            {
                ["type"] = "Resource",
                ["resource"] = new Dictionary<string, object>
                {
                    ["name"] = "cpu",
                    ["target"] = new Dictionary<string, object> { ["type"] = "Utilization", ["averageUtilization"] = percent },
                },
            },
        };
        return autoscaler;
    }

    /// <summary>A vertical autoscaler in recommendation mode: it suggests the workload's requests and never evicts a pod.</summary>
    public static Manifest RecommendResources(KubernetesResource workload)
    {
        string name = workload.Workload!.Metadata.Name;
        var autoscaler = new Manifest("autoscaling.k8s.io/v1", "VerticalPodAutoscaler", $"{workload.Name}-resources");
        autoscaler.Spec["targetRef"] = new Dictionary<string, object> { ["apiVersion"] = "apps/v1", ["kind"] = "Deployment", ["name"] = name };
        autoscaler.Spec["updatePolicy"] = new Dictionary<string, object> { ["updateMode"] = "Off" };
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
