using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.Core.Components;

namespace DaServer.Runtime;

/// <summary>
/// Base class of stateful actor implementations. An activation is an ECS entity in its shard's world: keep state in
/// struct components (<see cref="Get{T}"/>, <see cref="Add{T}"/>) so shard systems can query it too.
/// All methods run on the shard thread, one call at a time between awaits (no locks needed).
/// </summary>
/// <remarks>
/// <c>ref</c>s returned by <see cref="Get{T}"/> are valid until the next structural change of the shard: never keep
/// them across an <c>await</c>; call <see cref="Get{T}"/> again afterwards.
/// </remarks>
public abstract class Actor
{
    /// <summary>Actor key (player id, map instance id, ...).</summary>
    public long Key { get; internal set; }

    /// <summary>Shard (shard) the activation lives in.</summary>
    public Shard Shard { get; internal set; } = null!;

    /// <summary>The activation's entity in <see cref="Shard"/>.World.</summary>
    public EntityRef Entity { get; internal set; }

    /// <summary>The shard's ECS world (shortcut for <c>Shard.World</c>).</summary>
    public World World => Shard.World;

    /// <summary>Cluster runtime: proxies to other actors/services, pushes and broadcasts.</summary>
    public Node Runtime => Shard.Node;

    /// <summary>Called once before the first message is processed (load state here).</summary>
    public virtual ValueTask OnActivateAsync() => default;

    /// <summary>Called before the activation is removed (idle, ownership moved, shutdown): persist state here.</summary>
    public virtual ValueTask OnDeactivateAsync() => default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected ref T Get<T>() where T : struct, IComponent => ref Entity.Value.GetComponent<T>().Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected bool Has<T>() where T : struct, IComponent => Entity.Value.HasComponent<T>();

    /// <summary>Add (or reset) component <typeparamref name="T"/> and return a ref to it.</summary>
    protected ref T Add<T>() where T : struct, IComponent => ref Entity.Value.AddComponent<T>().Value;

    protected void Remove<T>() where T : struct, IComponent => Entity.Value.RemoveComponent<T>();
}

/// <summary>Links an actor entity back to its activation object (so systems can reach the actor).</summary>
public struct ActorLink : IComponent
{
    public Actor Actor;
}
