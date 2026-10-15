using DaServer.Client;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Client;

// The client's state as data: a player entity per connected client (one in a game, many in a bot process) carries the
// components below plus its predicted Position and Velocity (Sample.Shared); every avatar in view is an entity with
// Position, Velocity and Remote. Pushes and systems change them on the world's thread only.

/// <summary>The player's connection to the cluster.</summary>
public record struct Link(ClusterClient Client) : IComponent;

/// <summary>Who the player is once logged in, and the map it is in once entered.</summary>
public record struct Me(PlayerId Player, MapId Map) : IComponent;

/// <summary>Input: the velocity the player wants (a stick, keys, a bot's brain).</summary>
public record struct Steering(float X, float Y) : IComponent;

/// <summary>The steering the server last heard about (compared every tick: input is sent only when it changes).</summary>
public record struct SentSteering(float X, float Y) : IComponent;

/// <summary>A line the player says; sent and removed on the next tick.</summary>
public record struct Speaking(string Text) : IComponent;

/// <summary>The avatars in view, by player (their entities), and how many updates about them arrived.</summary>
public struct View() : IComponent
{
    public readonly Dictionary<PlayerId, Entity> Avatars = new();
    public long Updates;
}

/// <summary>Another player's avatar in <see cref="Viewer"/>'s view, extrapolated between updates (dead reckoning).</summary>
public record struct Remote(Entity Viewer, PlayerId Player) : IComponent;

/// <summary>The realm as last broadcast, and how many broadcasts arrived since <see cref="FirstTick"/>.</summary>
public record struct Realm(long Tick, int Online, long ServerTimeTicks, long FirstTick, long Received) : IComponent;

/// <summary>Chat lines heard.</summary>
public record struct Heard(long Lines) : IComponent;
