namespace DaServer.Runtime;

// What membership sees of the node (IMembershipHost): the rest of the node stays internal to it.
internal sealed partial class Node : IMembershipHost
{
    PeerLink IMembershipHost.GetLink(ClusterMember member) => GetLink(member);
    PeerLink IMembershipHost.GetLink(string host, int port) => GetLink(host, port);
    HostedMessage[] IMembershipHost.AdvertisedHandlers() => AdvertisedHandlers();
    uint[] IMembershipHost.AdvertisedScopes() => AdvertisedScopes();
    (uint[] PulseKeys, CardEntry[] Entries) IMembershipHost.FeatureCard() => FeatureCard();
    void IMembershipHost.ReadPulses(int[] values) => ReadPulses(values);
    void IMembershipHost.OnViewChanged() => OnViewChanged();
    Task IMembershipHost.DownAsync(int reachable, int lost) => DownAsync(reachable, lost);

    void IMembershipHost.UnloadAllUnsaved()
    {
        foreach (var shard in _shards) shard.RequestUnloadAll();
    }
}
