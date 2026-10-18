namespace DaServer.Cluster;

/// <summary>
/// What membership needs of its node, and all it knows of it: identity and options, links to gossip on, what to
/// advertise on the card and in the pulse, and what to do when the view changes, when the cluster declared this node
/// dead, or when it is on the minority side. The node implements it.
/// </summary>
internal interface IMembershipHost
{
    ulong Id { get; }
    NodeOptions Options { get; }
    ILogger Logger { get; }

    PeerLink GetLink(ClusterMember member);

    /// <summary>The link to a seed (not a member yet).</summary>
    PeerLink GetLink(string host, int port);

    /// <summary>The messages this node's handler types handle (on its card).</summary>
    HostedMessage[] AdvertisedHandlers();

    /// <summary>The address scopes this node's features handle (on its card).</summary>
    uint[] AdvertisedScopes();

    /// <summary>What the features publish on the card and gossip in the pulse.</summary>
    (uint[] PulseKeys, CardEntry[] Entries) FeatureCard();

    /// <summary>The features' counters for this round's pulse.</summary>
    void ReadPulses(int[] values);

    /// <summary>Messages waiting in the shards (the load gossiped).</summary>
    int TotalQueueLength();

    /// <summary>A new view was published.</summary>
    void OnViewChanged();

    /// <summary>The cluster declared this node dead: its loaded actors go without a write (their keys have new owners).</summary>
    void UnloadAllUnsaved();

    /// <summary>This node is on the minority side: stop at once, without leaving or saving.</summary>
    Task DownAsync(int reachable, int lost);
}
