namespace DaServer.Cluster;

/// <summary>
/// Immutable routing snapshot built from the membership table. Swapped atomically on every change, so routing reads
/// are lock-free and see a consistent view.
/// </summary>
public sealed class ClusterView
{
    private readonly FrozenDictionary<ulong, ClusterMember> _members;
    private readonly FrozenDictionary<uint, MessageRoute> _handlerTypes; // its nodes, kind and slot owners: one lookup
    private readonly FrozenDictionary<uint, MessageRoute[]> _routes;
    private readonly FrozenDictionary<uint, ClusterMember[]> _scopeNodes;

    /// <summary>
    /// (node, handler type, message) where the node hosts the handler type and another node's build handles the message
    /// with it but this node's does not: a rolling upgrade added the message (V3). Empty while every node runs one build.
    /// </summary>
    private readonly FrozenSet<(ulong NodeId, uint HandlerTypeId, uint MessageId)> _lacking;

    /// <param name="slots">The coordinator's slot table (ADR-079): its overrides win over rendezvous where their node hosts the type.</param>
    internal ClusterView(IEnumerable<ClusterMember> members, SlotTable? slots = null)
    {
        Table = slots ?? SlotTable.Empty;
        var all = new Dictionary<ulong, ClusterMember>();
        var handlerTypes = new Dictionary<uint, List<ClusterMember>>();
        var handlers = new Dictionary<uint, Dictionary<uint, bool>>();
        var upMembers = new List<ClusterMember>();
        var actorTypes = new HashSet<uint>();
        var scopes = new Dictionary<uint, List<ClusterMember>>();
        var handling = new Dictionary<(uint MessageId, uint HandlerTypeId), List<ClusterMember>>();

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
                if (Coordinator == null || m.UpSince < Coordinator.UpSince || m.UpSince == Coordinator.UpSince && m.NodeId < Coordinator.NodeId)
                    Coordinator = m;
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
                if (!handling.TryGetValue((handler.MessageId, handler.HandlerTypeId), out var nodes))
                    handling[(handler.MessageId, handler.HandlerTypeId)] = nodes = new();
                nodes.Add(m);
            }
        }

        _members = all.ToFrozenDictionary();
        UpMembers = upMembers.ToArray();
        var handlerTypeRoutes = new Dictionary<uint, MessageRoute>();
        foreach (var (handlerType, hosts) in handlerTypes)
        {
            var nodes = hosts.ToArray();
            bool actor = actorTypes.Contains(handlerType);
            handlerTypeRoutes[handlerType] = new MessageRoute(handlerType, actor, nodes) { Owners = actor ? SlotOwners(nodes, handlerType) : null };
        }

        // The coordinator's overrides, where their node hosts the type (one left: rendezvous takes the slot back).
        foreach (var o in Table.Overrides)
            if (handlerTypeRoutes.TryGetValue(o.HandlerTypeId, out var route) && route.Owners is { } owners && o.Slot < Slots)
                for (int i = 0; i < route.Nodes.Length; i++)
                    if (route.Nodes[i].NodeId == o.Node)
                    {
                        owners[o.Slot] = (ushort)i;
                        break;
                    }

        _handlerTypes = handlerTypeRoutes.ToFrozenDictionary();
        // An actor's owner is its type's (one activation per key, whatever the message); a service is chosen among
        // the nodes that handle the message.
        var routes = new Dictionary<uint, MessageRoute[]>();
        var lacking = new HashSet<(ulong, uint, uint)>();
        foreach (var (messageId, byHandlerType) in handlers)
        {
            var list = new List<MessageRoute>();
            foreach (var (handlerType, actor) in byHandlerType)
            {
                var typeRoute = handlerTypeRoutes[handlerType];
                var nodes = handling[(messageId, handlerType)];
                if (nodes.Count != typeRoute.Nodes.Length)
                    foreach (var host in typeRoute.Nodes)
                        if (!nodes.Contains(host)) lacking.Add((host.NodeId, handlerType, messageId));
                list.Add(actor ? typeRoute : new MessageRoute(handlerType, false, nodes.ToArray()));
            }

            routes[messageId] = list.ToArray();
        }

        _routes = routes.ToFrozenDictionary();
        _lacking = lacking.ToFrozenSet();
        var scopeArrays = new Dictionary<uint, ClusterMember[]>();
        foreach (var (k, v) in scopes) scopeArrays[k] = v.ToArray();
        _scopeNodes = scopeArrays.ToFrozenDictionary();
    }

    /// <summary>The slot table this view applies.</summary>
    internal SlotTable Table { get; }

    /// <summary>The oldest Up member: it allocates slots (ADR-079). Null without Up members.</summary>
    public ClusterMember? Coordinator { get; }

    /// <summary>The nodes hosting actor type <paramref name="handlerTypeId"/> (Up and Suspect), or none.</summary>
    internal ClusterMember[] NodesOf(uint handlerTypeId) => _handlerTypes.TryGetValue(handlerTypeId, out var route) ? route.Nodes : [];

    /// <summary>The owner of <paramref name="slot"/> of actor type <paramref name="handlerTypeId"/> by rendezvous alone (no override), or null.</summary>
    internal ClusterMember? RendezvousOwner(uint handlerTypeId, int slot)
    {
        var nodes = NodesOf(handlerTypeId);
        int best = Rendezvous(nodes, GroupOf(handlerTypeId), slot, 0);
        return best < 0 ? null : nodes[best];
    }

    /// <summary>The owner of <paramref name="slot"/> of actor type <paramref name="handlerTypeId"/> in this view (overrides applied), or null.</summary>
    internal ClusterMember? SlotOwner(uint handlerTypeId, int slot) =>
        _handlerTypes.TryGetValue(handlerTypeId, out var route) && route.Owners is { } owners ? route.Nodes[owners[slot]] : null;

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
    /// The nodes hosting handler type <paramref name="handlerTypeId"/> (an actor type is named by its id type), if any.
    /// </summary>
    public bool TryGetHandlerTypeRoute(uint handlerTypeId, out MessageRoute route) =>
        _handlerTypes.TryGetValue(handlerTypeId, out route);

    /// <summary>
    /// Owner of actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>): the owner of its slot among the Up and
    /// Suspect nodes hosting its handler type (ADR-074). Every node with the same view picks the same owner.
    /// </summary>
    public ClusterMember? HandlerTypeOwner(uint handlerTypeId, long key) =>
        _handlerTypes.TryGetValue(handlerTypeId, out var route) && route.Owners is { } owners
            ? route.Nodes[owners[Slot(handlerTypeId, key)]]
            : null;

    /// <summary>
    /// The owner the key would have without node <paramref name="excluded"/>: where it was before that node joined
    /// (rendezvous hashing moves only the slots a new node wins).
    /// </summary>
    public ClusterMember? HandlerTypeOwnerWithout(uint handlerTypeId, long key, ulong excluded)
    {
        if (!_handlerTypes.TryGetValue(handlerTypeId, out var route)) return null;
        var nodes = route.Nodes;
        int best = Rendezvous(nodes, GroupOf(handlerTypeId), Slot(handlerTypeId, key), excluded);
        return best < 0 ? null : nodes[best];
    }

    /// <summary>
    /// Actor keys hash into this many slots (ADR-074); a view gives each slot of an actor type an owner among the
    /// type's nodes by rendezvous hashing, so finding a key's node is one table read, and a node joining or leaving
    /// moves only the slots it wins or held.
    /// </summary>
    internal const int Slots = 4096;

    /// <summary>The slot of actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>): of its placement for placed id types (ADR-073).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Slot(uint handlerTypeId, long key)
    {
        Placements.Of(handlerTypeId, ref key);
        return (int)(Weight(0, 0, key) & (Slots - 1));
    }

    /// <summary>What the slots of a type are ranked by: its own id, or 0 for placed id types, so they meet those of other types.</summary>
    private static uint GroupOf(uint handlerTypeId)
    {
        long probe = 0;
        return Placements.Of(handlerTypeId, ref probe);
    }

    private static ushort[] SlotOwners(ClusterMember[] nodes, uint handlerTypeId)
    {
        var owners = new ushort[Slots];
        uint group = GroupOf(handlerTypeId);
        for (int slot = 0; slot < Slots; slot++) owners[slot] = (ushort)Math.Max(0, Rendezvous(nodes, group, slot, 0));
        return owners;
    }

    /// <summary>The index of the node with the highest weight for (<paramref name="group"/>, <paramref name="slot"/>), or -1.</summary>
    private static int Rendezvous(ClusterMember[] nodes, uint group, int slot, ulong excluded)
    {
        int best = -1;
        ulong bestWeight = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].NodeId == excluded && excluded != 0) continue;
            ulong weight = Weight(nodes[i].NodeId, group, slot);
            if (best < 0 || weight > bestWeight)
            {
                best = i;
                bestWeight = weight;
            }
        }

        return best;
    }

    /// <summary>
    /// Node that receives message <paramref name="messageId"/> for <paramref name="route"/>: the owner of the key for
    /// actor handlers (none while the owner runs a build without the message: it is upgraded or leaves, V3), otherwise
    /// the local node when it hosts the handler, else the less loaded of two others (power of two choices over the
    /// queue lengths members gossip, <see cref="ClusterMember.Load"/>): a busy node gets less, and with equal loads the
    /// first of the two goes in turn (<see cref="Spread"/>), so services stay balanced.
    /// </summary>
    public ClusterMember? Target(in MessageRoute route, uint messageId, long key, ulong localNodeId)
    {
        if (route.Actor)
        {
            if (route.Nodes.Length == 0) return null;
            var owner = route.Nodes[route.Owners![Slot(route.HandlerTypeId, key)]];
            return _lacking.Count != 0 && _lacking.Contains((owner.NodeId, route.HandlerTypeId, messageId)) ? null : owner;
        }

        var nodes = route.Nodes;
        foreach (var node in nodes)
            if (node.NodeId == localNodeId) return node;
        if (nodes.Length <= 1) return nodes.Length == 0 ? null : nodes[0];
        uint n = (uint)nodes.Length, next = Spread.Next(), first = next % n;
        var a = nodes[first];
        var b = nodes[(first + 1 + (next * 0x9E3779B9u >> 16) % (n - 1)) % n]; // another one, spread by a mix of the turn
        return Volatile.Read(ref b.Load) < Volatile.Read(ref a.Load) ? b : a;
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
public readonly record struct MessageRoute(uint HandlerTypeId, bool Actor, ClusterMember[] Nodes)
{
    /// <summary>For actor handler types: the owner of each key slot, as an index into <see cref="Nodes"/> (ADR-074).</summary>
    internal ushort[]? Owners { get; init; }
}
