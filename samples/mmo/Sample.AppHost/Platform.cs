using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;

/// <summary>Kubernetes objects this sample asks of the platform, beside what Aspire and DaServer.Aspire publish.</summary>
static class Platform
{
    /// <summary>A load balancer in front of the workload's <paramref name="endpoint"/> port only (not its cluster or HTTP ports).</summary>
    public static Service LoadBalancer(KubernetesResource workload, string endpoint)
    {
        var service = new Service();
        service.Metadata.Name = $"{workload.Name}-{endpoint}s";
        service.Spec.Type = "LoadBalancer";
        foreach (var (key, value) in workload.Service!.Spec.Selector) service.Spec.Selector[key] = value;
        foreach (var port in workload.Service.Spec.Ports)
            if (port.Name == endpoint) service.Spec.Ports.Add(port);
        return service;
    }

    /// <summary>Voluntary disruptions (a node drain, a cluster upgrade) take at most one pod of the workload at a time.</summary>
    public static Manifest OneAtATime(KubernetesResource workload)
    {
        var budget = new Manifest("policy/v1", "PodDisruptionBudget", $"{workload.Name}-disruption");
        budget.Spec["maxUnavailable"] = 1;
        budget.Spec["selector"] = new Dictionary<string, object> { ["matchLabels"] = workload.Service!.Spec.Selector };
        return budget;
    }
}

/// <summary>A Kubernetes object Aspire has no type for (its spec as plain YAML data).</summary>
sealed class Manifest : BaseKubernetesResource
{
    public Manifest(string apiVersion, string kind, string name) : base(apiVersion, kind) => Metadata.Name = name;

    [YamlDotNet.Serialization.YamlMember(Alias = "spec")]
    public Dictionary<string, object> Spec { get; } = new();
}
