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
    private static readonly MemberInfo[] NoMembers = [];

    private readonly FrozenDictionary<ulong, MemberInfo> _members;
    private readonly FrozenDictionary<uint, MemberInfo[]> _serviceNodes;
    private readonly FrozenDictionary<uint, (uint Implementation, MemberInfo[] Nodes)> _actorNodes;
    private readonly FrozenDictionary<uint, MemberInfo[]> _implementationNodes;

    public ClusterView(IEnumerable<MemberInfo> members, long version)
    {
        Version = version;
        var all = new Dictionary<ulong, MemberInfo>();
        var services = new Dictionary<uint, List<MemberInfo>>();
        var actors = new Dictionary<uint, (uint, List<MemberInfo>)>();
        var implementations = new Dictionary<uint, List<MemberInfo>>();
        var up = new List<MemberInfo>();

        foreach (var m in members)
        {
            all[m.NodeId] = m;
            if (m.Status != MemberStatus.Up) continue; // only Up members receive new work
            up.Add(m);

            foreach (var contract in m.Services)
            {
                if (!services.TryGetValue(contract, out var list)) services[contract] = list = new();
                list.Add(m);
            }

            foreach (var actor in m.Actors)
            {
                if (!actors.TryGetValue(actor.ContractId, out var entry))
                    actors[actor.ContractId] = entry = (actor.ImplementationId, new());
                entry.Item2.Add(m);

                if (!implementations.TryGetValue(actor.ImplementationId, out var hosts))
                    implementations[actor.ImplementationId] = hosts = new();
                if (!hosts.Contains(m)) hosts.Add(m);
            }
        }

        _members = all.ToFrozenDictionary();
        UpMembers = up.ToArray();
        var serviceArrays = new Dictionary<uint, MemberInfo[]>();
        foreach (var (k, v) in services) serviceArrays[k] = v.ToArray();
        _serviceNodes = serviceArrays.ToFrozenDictionary();
        var actorArrays = new Dictionary<uint, (uint, MemberInfo[])>();
        foreach (var (k, v) in actors) actorArrays[k] = (v.Item1, v.Item2.ToArray());
        _actorNodes = actorArrays.ToFrozenDictionary();
        var implementationArrays = new Dictionary<uint, MemberInfo[]>();
        foreach (var (k, v) in implementations) implementationArrays[k] = v.ToArray();
        _implementationNodes = implementationArrays.ToFrozenDictionary();
    }

    /// <summary>Monotonic version of the view (incremented on every change).</summary>
    public long Version { get; }

    public MemberInfo[] UpMembers { get; }

    public IEnumerable<MemberInfo> Members => _members.Values;

    public bool TryGetMember(ulong nodeId, out MemberInfo member) => _members.TryGetValue(nodeId, out member!);

    /// <summary>Up nodes hosting stateless service <paramref name="contractId"/>.</summary>
    public MemberInfo[] ServiceNodes(uint contractId) =>
        _serviceNodes.TryGetValue(contractId, out var nodes) ? nodes : NoMembers;

    /// <summary>
    /// Owner of actor (<paramref name="contractId"/>, <paramref name="key"/>) by rendezvous (highest random weight)
    /// hashing over the Up nodes hosting its implementation. Every node with the same view picks the same owner, and
    /// adding/removing a node only moves the keys that node wins/loses.
    /// </summary>
    public MemberInfo? ActorOwner(uint contractId, long key) =>
        _actorNodes.TryGetValue(contractId, out var entry) ? Rendezvous(entry.Nodes, entry.Implementation, key) : null;

    /// <summary>Owner of actor (<paramref name="implementationId"/>, <paramref name="key"/>); see <see cref="ActorOwner"/>.</summary>
    public MemberInfo? ImplementationOwner(uint implementationId, long key) =>
        _implementationNodes.TryGetValue(implementationId, out var nodes) ? Rendezvous(nodes, implementationId, key) : null;

    private static MemberInfo? Rendezvous(MemberInfo[] nodes, uint implementationId, long key)
    {
        MemberInfo? best = null;
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
