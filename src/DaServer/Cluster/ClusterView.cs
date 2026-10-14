using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DaServer.Cluster;

/// <summary>
/// Immutable routing snapshot built from the membership table. Swapped atomically on every change, so routing reads
/// are lock-free and see a consistent view.
/// </summary>
public sealed class ClusterView
{
    private static readonly ClusterMember[] NoMembers = [];

    private readonly FrozenDictionary<ulong, ClusterMember> _members;
    private readonly FrozenDictionary<uint, ClusterMember[]> _serviceNodes;
    private readonly FrozenDictionary<uint, (uint Implementation, ClusterMember[] Nodes)> _actorNodes;
    private readonly FrozenDictionary<uint, ClusterMember[]> _implementationNodes;
    private readonly FrozenDictionary<uint, EventRoute[]> _eventRoutes;

    public ClusterView(IEnumerable<ClusterMember> members, long version)
    {
        Version = version;
        var all = new Dictionary<ulong, ClusterMember>();
        var services = new Dictionary<uint, List<ClusterMember>>();
        var actors = new Dictionary<uint, (uint, List<ClusterMember>)>();
        var implementations = new Dictionary<uint, List<ClusterMember>>();
        var handlers = new Dictionary<uint, Dictionary<uint, (bool Actor, List<ClusterMember> Nodes)>>();
        var upMembers = new List<ClusterMember>();

        foreach (var m in members)
        {
            all[m.NodeId] = m;

            // Keys stay with a Suspect member: a pause or a blip must not activate its actors a second time elsewhere.
            // They move when it is Dead or Leaving. New stateless work (services, service events) goes to Up members only.
            bool up = m.Status == MemberStatus.Up;
            if (!up && m.Status != MemberStatus.Suspect) continue;
            if (up) upMembers.Add(m);

            foreach (var contract in m.Services)
            {
                if (!up) break;
                if (!services.TryGetValue(contract, out var list)) services[contract] = list = new();
                list.Add(m);
            }

            foreach (var actor in m.Actors)
            {
                if (actor.ContractId != 0)
                {
                    if (!actors.TryGetValue(actor.ContractId, out var entry))
                        actors[actor.ContractId] = entry = (actor.ImplementationId, new());
                    entry.Item2.Add(m);
                }

                if (!implementations.TryGetValue(actor.ImplementationId, out var hosts))
                    implementations[actor.ImplementationId] = hosts = new();
                if (!hosts.Contains(m)) hosts.Add(m);
            }

            foreach (var handler in m.Handlers)
            {
                if (!up && !handler.Actor) continue;
                if (!handlers.TryGetValue(handler.EventId, out var byImplementation))
                    handlers[handler.EventId] = byImplementation = new();
                if (!byImplementation.TryGetValue(handler.ImplementationId, out var route))
                    byImplementation[handler.ImplementationId] = route = (handler.Actor, new());
                route.Nodes.Add(m);
            }
        }

        _members = all.ToFrozenDictionary();
        UpMembers = upMembers.ToArray();
        var serviceArrays = new Dictionary<uint, ClusterMember[]>();
        foreach (var (k, v) in services) serviceArrays[k] = v.ToArray();
        _serviceNodes = serviceArrays.ToFrozenDictionary();
        var actorArrays = new Dictionary<uint, (uint, ClusterMember[])>();
        foreach (var (k, v) in actors) actorArrays[k] = (v.Item1, v.Item2.ToArray());
        _actorNodes = actorArrays.ToFrozenDictionary();
        var implementationArrays = new Dictionary<uint, ClusterMember[]>();
        foreach (var (k, v) in implementations) implementationArrays[k] = v.ToArray();
        _implementationNodes = implementationArrays.ToFrozenDictionary();
        var routes = new Dictionary<uint, EventRoute[]>();
        foreach (var (eventId, byImplementation) in handlers)
        {
            var list = new List<EventRoute>();
            foreach (var (implementation, route) in byImplementation)
                list.Add(new EventRoute(implementation, route.Actor, route.Nodes.ToArray()));
            routes[eventId] = list.ToArray();
        }

        _eventRoutes = routes.ToFrozenDictionary();
    }

    /// <summary>Monotonic version of the view (incremented on every change).</summary>
    public long Version { get; }

    public ClusterMember[] UpMembers { get; }

    public IEnumerable<ClusterMember> Members => _members.Values;

    public bool TryGetMember(ulong nodeId, out ClusterMember member) => _members.TryGetValue(nodeId, out member!);

    /// <summary>Up nodes hosting stateless service <paramref name="contractId"/>.</summary>
    public ClusterMember[] ServiceNodes(uint contractId) =>
        _serviceNodes.TryGetValue(contractId, out var nodes) ? nodes : NoMembers;

    /// <summary>
    /// Owner of actor (<paramref name="contractId"/>, <paramref name="key"/>) by rendezvous (highest random weight)
    /// hashing over the Up and Suspect nodes hosting its implementation. Every node with the same view picks the same owner, and
    /// adding/removing a node only moves the keys that node wins/loses.
    /// </summary>
    public ClusterMember? ActorOwner(uint contractId, long key) =>
        _actorNodes.TryGetValue(contractId, out var entry) ? Rendezvous(entry.Nodes, entry.Implementation, key) : null;

    /// <summary>Owner of actor (<paramref name="implementationId"/>, <paramref name="key"/>); see <see cref="ActorOwner"/>.</summary>
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

    /// <summary>Every implementation handling event <paramref name="eventId"/> in the cluster, with the Up nodes hosting it.</summary>
    public EventRoute[] EventRoutes(uint eventId) => _eventRoutes.TryGetValue(eventId, out var routes) ? routes : [];

    /// <summary>
    /// Node that receives an event for <paramref name="route"/>: the owner of the key for actor handlers, otherwise the
    /// local node when it hosts the handler, else the one <paramref name="spread"/> picks (balances services).
    /// </summary>
    public ClusterMember? EventTarget(in EventRoute route, long key, ulong localNodeId, uint spread)
    {
        if (route.Actor) return Rendezvous(route.Nodes, route.ImplementationId, key);
        foreach (var node in route.Nodes)
            if (node.NodeId == localNodeId) return node;
        return route.Nodes.Length == 0 ? null : route.Nodes[(int)(spread % (uint)route.Nodes.Length)];
    }

    /// <summary>Implementation id serving actor contract <paramref name="contractId"/> (placement identity).</summary>
    public bool TryGetActorImplementation(uint contractId, out uint implementationId)
    {
        if (_actorNodes.TryGetValue(contractId, out var entry))
        {
            implementationId = entry.Implementation;
            return true;
        }

        implementationId = 0;
        return false;
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

/// <summary>One implementation handling an event type, and the Up nodes hosting it.</summary>
public readonly record struct EventRoute(uint ImplementationId, bool Actor, ClusterMember[] Nodes);
