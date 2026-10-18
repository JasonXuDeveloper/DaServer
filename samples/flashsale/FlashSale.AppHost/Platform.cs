using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;

/// <summary>Kubernetes objects this sample asks of the platform, beside what Aspire and DaServer.Aspire publish.</summary>
static class Platform
{
    /// <summary>A horizontal autoscaler keeping the workload's average CPU near <paramref name="percent"/> of its request.</summary>
    public static Manifest ScaleOnCpu(KubernetesResource workload, int min, int max, int percent)
    {
        var autoscaler = new Manifest("autoscaling/v2", "HorizontalPodAutoscaler", $"{workload.Name}-autoscaler");
        autoscaler.Spec["scaleTargetRef"] = Publishing.DeploymentOf(workload);
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
}
