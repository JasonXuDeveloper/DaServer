using DaServer.Client;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

// The client's state as data. A player entity per connected client (one in a game, many in a bot process) carries the
// components below and its own avatar's predicted Position and Velocity (the shared MovementSystem moves it); each
// avatar in view is an entity with a Track. Pushes and systems change them on the world's thread only.

/// <summary>The player's connection to the cluster.</summary>
public record struct Link(ClusterClient Client) : IComponent;

/// <summary>Who the player is once logged in, and the map it is in once entered.</summary>
public record struct Me(PlayerId Player, MapId Map) : IComponent;

/// <summary>Input: the velocity the player wants (a stick, keys, a bot).</summary>
public record struct Steering(float X, float Y) : IComponent;

/// <summary>The steering the map last heard about: input is sent only when it changes.</summary>
public record struct SentSteering(float X, float Y) : IComponent;

/// <summary>A line the player says; sent and removed on the next tick.</summary>
public record struct Speaking(string Text) : IComponent;

/// <summary>The avatars in view, by player (their entities).</summary>
public struct View() : IComponent
{
    public readonly Dictionary<PlayerId, Entity> Avatars = new();
    public readonly List<PlayerId> Leaving = new(); // scratch of ViewSystem (no garbage per pass)
}

/// <summary>
/// Another player's avatar: where it was and how it moved when the map last said so. Its position now is extrapolated
/// on demand (dead reckoning), not integrated every tick: a client sees hundreds of avatars.
/// </summary>
public record struct Track(Position At, Velocity Velocity, long AtMs) : IComponent
{
    public readonly Position Now(long nowMs)
    {
        float ticks = (nowMs - AtMs) * (Timing.TickRate / 1000f);
        return MapGeometry.Inside(new Position(At.X + Velocity.X * ticks, At.Y + Velocity.Y * ticks));
    }
}

/// <summary>The realm as last broadcast.</summary>
public record struct Realm(long Tick, int Online) : IComponent;

/// <summary>The last chat line heard in the map.</summary>
public record struct Heard(PlayerId From, string Text) : IComponent;
