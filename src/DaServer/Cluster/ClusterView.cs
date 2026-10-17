namespace DaServer.Cluster;

/// <summary>
/// Immutable routing snapshot built from the membership table. Swapped atomically on every change, so routing reads
/// are lock-free and see a consistent view.
/// </summary>
public sealed class ClusterView
{
    private readonly FrozenDictionary<ulong, ClusterMember> _members;
    private readonly FrozenDictionary<uint, ClusterMember[]> _handlerTypeNodes;
    private readonly FrozenSet<uint> _actorTypes;
    private readonly FrozenDictionary<uint, MessageRoute[]> _routes;
    private readonly FrozenDictionary<uint, ClusterMember[]> _scopeNodes;

    public ClusterView(IEnumerable<ClusterMember> members)
    {
        var all = new Dictionary<ulong, ClusterMember>();
        var handlerTypes = new Dictionary<uint, List<ClusterMember>>();
        var handlers = new Dictionary<uint, Dictionary<uint, bool>>();
        var upMembers = new List<ClusterMember>();
        var actorTypes = new HashSet<uint>();
        var scopes = new Dictionary<uint, List<ClusterMember>>();

        foreach (var m in members)
        {
            all[m.NodeId] = m;

            // Keys stay with a Suspect member: a pause or a blip must not load its actors a second time elsewhere.
            // They move when it is Dead or Leaving. New stateless work (services) goes to Up members only.
            bool up = m.Status == MemberStatus.Up;
            if (!up && m.Status != MemberStatus.Suspect) continue;
            if (up)
            {
                upMembers.Add(m);
                foreach (uint scope in m.Scopes)
                {
                    if (!scopes.TryGetValue(scope, out var nodes)) scopes[scope] = nodes = new();
                    nodes.Add(m);
                }
            }

            foreach (var handler in m.Handlers)
            {
                if (!up && !handler.Actor) continue;
                if (!handlers.TryGetValue(handler.MessageId, out var byHandlerType))
                    handlers[handler.MessageId] = byHandlerType = new();
                byHandlerType[handler.HandlerTypeId] = handler.Actor;
                if (handler.Actor) actorTypes.Add(handler.HandlerTypeId);
                if (!handlerTypes.TryGetValue(handler.HandlerTypeId, out var hosts))
                    handlerTypes[handler.HandlerTypeId] = hosts = new();
                if (!hosts.Contains(m)) hosts.Add(m);
            }
        }

        _members = all.ToFrozenDictionary();
        UpMembers = upMembers.ToArray();
        var handlerTypeArrays = new Dictionary<uint, ClusterMember[]>();
        foreach (var (k, v) in handlerTypes) handlerTypeArrays[k] = v.ToArray();
        _handlerTypeNodes = handlerTypeArrays.ToFrozenDictionary();
        _actorTypes = actorTypes.ToFrozenSet();
        var routes = new Dictionary<uint, MessageRoute[]>();
        foreach (var (messageId, byHandlerType) in handlers)
        {
            var list = new List<MessageRoute>();
            foreach (var (handlerType, actor) in byHandlerType)
                list.Add(new MessageRoute(handlerType, actor, handlerTypeArrays[handlerType]));
            routes[messageId] = list.ToArray();
        }

        _routes = routes.ToFrozenDictionary();
        var scopeArrays = new Dictionary<uint, ClusterMember[]>();
        foreach (var (k, v) in scopes) scopeArrays[k] = v.ToArray();
        _scopeNodes = scopeArrays.ToFrozenDictionary();
    }

    /// <summary>The Up members handling address scope <paramref name="scope"/> (where its group messages go).</summary>
    public ClusterMember[] NodesWithScope(uint scope) => _scopeNodes.TryGetValue(scope, out var nodes) ? nodes : [];

    public ClusterMember[] UpMembers { get; }

    public IEnumerable<ClusterMember> Members => _members.Values;

    public bool TryGetMember(ulong nodeId, out ClusterMember member) => _members.TryGetValue(nodeId, out member!);

    /// <summary>Every handler type handling message <paramref name="messageId"/> in the cluster, with the nodes hosting it.</summary>
    public MessageRoute[] Routes(uint messageId) => _routes.TryGetValue(messageId, out var routes) ? routes : [];

    /// <summary>
    /// The one handler type of the requested kind handling <paramref name="messageId"/> (actor handlers when
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
    /// Owner of actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>) by rendezvous (highest random weight)
    /// hashing over the Up and Suspect nodes hosting its handler type. Every node with the same view picks the same
    /// owner, and adding/removing a node only moves the keys that node wins/loses.
    /// </summary>
    /// <summary>
    /// The nodes hosting handler type <paramref name="handlerTypeId"/> (an actor type is named by its id type), if any.
    /// </summary>
    public bool TryGetHandlerTypeRoute(uint handlerTypeId, out MessageRoute route)
    {
        bool found = _handlerTypeNodes.TryGetValue(handlerTypeId, out var nodes);
        route = new MessageRoute(handlerTypeId, _actorTypes.Contains(handlerTypeId), nodes ?? []);
        return found;
    }

    public ClusterMember? HandlerTypeOwner(uint handlerTypeId, long key) =>
        _handlerTypeNodes.TryGetValue(handlerTypeId, out var nodes) ? Rendezvous(nodes, handlerTypeId, key, 0) : null;

    /// <summary>
    /// The owner the key would have without node <paramref name="excluded"/>: where it was before that node joined
    /// (rendezvous hashing moves only the keys a new node wins).
    /// </summary>
    public ClusterMember? HandlerTypeOwnerWithout(uint handlerTypeId, long key, ulong excluded) =>
        _handlerTypeNodes.TryGetValue(handlerTypeId, out var nodes) ? Rendezvous(nodes, handlerTypeId, key, excluded) : null;

    private static ClusterMember? Rendezvous(ClusterMember[] nodes, uint handlerTypeId, long key, ulong excluded)
    {
        uint group = Placements.Of(handlerTypeId, ref key); // a placed id type hashes its placement (ADR-073)
        ClusterMember? best = null;
        ulong bestWeight = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].NodeId == excluded && excluded != 0) continue;
            ulong weight = Weight(nodes[i].NodeId, group, key);
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
    /// the local node when it hosts the handler, else one in turn (<see cref="Spread"/>: balances services).
    /// </summary>
    public ClusterMember? Target(in MessageRoute route, long key, ulong localNodeId)
    {
        if (route.Actor) return Rendezvous(route.Nodes, route.HandlerTypeId, key, 0);
        foreach (var node in route.Nodes)
            if (node.NodeId == localNodeId) return node;
        return route.Nodes.Length == 0 ? null : route.Nodes[(int)(Spread.Next() % (uint)route.Nodes.Length)];
    }

    /// <summary>SplitMix64-style mix of (node, handler type, key).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Weight(ulong nodeId, uint handlerTypeId, long key)
    {
        ulong x = nodeId ^ ((ulong)key * 0x9E3779B97F4A7C15UL) ^ ((ulong)handlerTypeId << 32);
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}

/// <summary>One handler type handling a message type, and the nodes hosting it.</summary>
public readonly record struct MessageRoute(uint HandlerTypeId, bool Actor, ClusterMember[] Nodes);
