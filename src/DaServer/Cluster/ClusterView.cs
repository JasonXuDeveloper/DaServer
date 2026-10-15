namespace DaServer.Cluster;

/// <summary>
/// Immutable routing snapshot built from the membership table. Swapped atomically on every change, so routing reads
/// are lock-free and see a consistent view.
/// </summary>
public sealed class ClusterView
{
    private readonly FrozenDictionary<ulong, ClusterMember> _members;
    private readonly FrozenDictionary<uint, ClusterMember[]> _implementationNodes;
    private readonly FrozenDictionary<uint, MessageRoute[]> _routes;

    public ClusterView(IEnumerable<ClusterMember> members)
    {
        var all = new Dictionary<ulong, ClusterMember>();
        var implementations = new Dictionary<uint, List<ClusterMember>>();
        var handlers = new Dictionary<uint, Dictionary<uint, bool>>();
        var upMembers = new List<ClusterMember>();

        foreach (var m in members)
        {
            all[m.NodeId] = m;

            // Keys stay with a Suspect member: a pause or a blip must not activate its actors a second time elsewhere.
            // They move when it is Dead or Leaving. New stateless work (services) goes to Up members only.
            bool up = m.Status == MemberStatus.Up;
            if (!up && m.Status != MemberStatus.Suspect) continue;
            if (up) upMembers.Add(m);

            foreach (var handler in m.Handlers)
            {
                if (!up && !handler.Actor) continue;
                if (!handlers.TryGetValue(handler.MessageId, out var byImplementation))
                    handlers[handler.MessageId] = byImplementation = new();
                byImplementation[handler.ImplementationId] = handler.Actor;
                if (!implementations.TryGetValue(handler.ImplementationId, out var hosts))
                    implementations[handler.ImplementationId] = hosts = new();
                if (!hosts.Contains(m)) hosts.Add(m);
            }
        }

        _members = all.ToFrozenDictionary();
        UpMembers = upMembers.ToArray();
        var implementationArrays = new Dictionary<uint, ClusterMember[]>();
        foreach (var (k, v) in implementations) implementationArrays[k] = v.ToArray();
        _implementationNodes = implementationArrays.ToFrozenDictionary();
        var routes = new Dictionary<uint, MessageRoute[]>();
        foreach (var (messageId, byImplementation) in handlers)
        {
            var list = new List<MessageRoute>();
            foreach (var (implementation, actor) in byImplementation)
                list.Add(new MessageRoute(implementation, actor, implementationArrays[implementation]));
            routes[messageId] = list.ToArray();
        }

        _routes = routes.ToFrozenDictionary();
    }

    public ClusterMember[] UpMembers { get; }

    public IEnumerable<ClusterMember> Members => _members.Values;

    public bool TryGetMember(ulong nodeId, out ClusterMember member) => _members.TryGetValue(nodeId, out member!);

    /// <summary>Every implementation handling message <paramref name="messageId"/> in the cluster, with the nodes hosting it.</summary>
    public MessageRoute[] Routes(uint messageId) => _routes.TryGetValue(messageId, out var routes) ? routes : [];

    /// <summary>
    /// The one implementation of the requested kind handling <paramref name="messageId"/> (actor handlers when
    /// <paramref name="actor"/>, else services). False when none does; <paramref name="ambiguous"/> when several do
    /// (point-to-point messages have one handler, <c>Publish</c> reaches them all).
    /// </summary>
    public bool TryGetRoute(uint messageId, bool actor, out MessageRoute route, out bool ambiguous)
    {
        route = default;
        ambiguous = false;
        bool found = false;
        var routes = Routes(messageId);
        for (int i = 0; i < routes.Length; i++)
        {
            if (routes[i].Actor != actor) continue;
            if (found) ambiguous = true;
            route = routes[i];
            found = true;
        }

        return found && !ambiguous;
    }

    /// <summary>
    /// Owner of actor (<paramref name="implementationId"/>, <paramref name="key"/>) by rendezvous (highest random weight)
    /// hashing over the Up and Suspect nodes hosting its implementation. Every node with the same view picks the same
    /// owner, and adding/removing a node only moves the keys that node wins/loses.
    /// </summary>
    public ClusterMember? ImplementationOwner(uint implementationId, long key) =>
        _implementationNodes.TryGetValue(implementationId, out var nodes) ? Rendezvous(nodes, implementationId, key) : null;

    private static ClusterMember? Rendezvous(ClusterMember[] nodes, uint implementationId, long key)
    {
        ClusterMember? best = null;
        ulong bestWeight = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            ulong weight = Weight(nodes[i].NodeId, implementationId, key);
            if (best == null || weight > bestWeight)
            {
                best = nodes[i];
                bestWeight = weight;
            }
        }

        return best;
    }

    /// <summary>
    /// Node that receives a message for <paramref name="route"/>: the owner of the key for actor handlers, otherwise
    /// the local node when it hosts the handler, else the one <paramref name="spread"/> picks (balances services).
    /// </summary>
    public ClusterMember? Target(in MessageRoute route, long key, ulong localNodeId, uint spread)
    {
        if (route.Actor) return Rendezvous(route.Nodes, route.ImplementationId, key);
        foreach (var node in route.Nodes)
            if (node.NodeId == localNodeId) return node;
        return route.Nodes.Length == 0 ? null : route.Nodes[(int)(spread % (uint)route.Nodes.Length)];
    }

    /// <summary>SplitMix64-style mix of (node, implementation, key).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Weight(ulong nodeId, uint implementationId, long key)
    {
        ulong x = nodeId ^ ((ulong)key * 0x9E3779B97F4A7C15UL) ^ ((ulong)implementationId << 32);
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}

/// <summary>One implementation handling a message type, and the nodes hosting it.</summary>
public readonly record struct MessageRoute(uint ImplementationId, bool Actor, ClusterMember[] Nodes);
