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

    /// <summary>
    /// A vertical autoscaler that resizes the workload's pods in place when it can (Kubernetes 1.33+) and evicts them
    /// otherwise; an eviction is a graceful leave whose actors move (one pod at a time with <see cref="OneAtATime"/>). .NET
    /// reads its limits at start (shard count, GC heap limit): more CPU in place still lifts throttling, a memory change
    /// restarts the container so the heap limit follows.
    /// </summary>
    public static Manifest ResizeInPlace(KubernetesResource workload)
    {
        foreach (var container in workload.Workload!.PodTemplate.Spec.Containers)
        {
            container.ResizePolicy.Add(new() { ResourceName = "cpu", RestartPolicy = "NotRequired" });
            container.ResizePolicy.Add(new() { ResourceName = "memory", RestartPolicy = "RestartContainer" });
        }

        return Publishing.VerticalAutoscaler(workload, "InPlaceOrRecreate");
    }
}
