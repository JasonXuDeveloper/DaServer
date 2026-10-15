using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>Pushes: run as soon as they arrive (every turn of the client loop), not at the simulation's tick rate.</summary>
public struct ClientPushes : ISystemGroup;

/// <summary>Runs each player's queued pushes on the world's thread (<see cref="Pushes"/>).</summary>
[UpdateInGroup<ClientPushes>]
public sealed partial class PushSystem : EcsSystem
{
    [ForEach]
    private static void Run(in Link link) => link.Client.ProcessCallbacks();
}

/// <summary>Input: the player's steering moves its avatar at once (prediction) and goes to the map when it changes.</summary>
public sealed partial class SteeringSystem : EcsSystem
{
    public override int Priority => -10; // before movement: the avatar moves on the tick its input changed

    [ForEach]
    private static void Steer(in Link link, in Me me, in Steering steering, ref SentSteering sent, ref Velocity velocity)
    {
        if (me.Map.Value == 0 || (steering.X == sent.X && steering.Y == sent.Y)) return; // input goes to the map it is in
        sent = new SentSteering(steering.X, steering.Y);
        velocity = MapGeometry.Clamp(steering.X, steering.Y); // the server clamps the same way
        link.Client.Send(new Move(steering.X, steering.Y));
    }
}

/// <summary>Says what the player wants to say, once.</summary>
public sealed partial class SpeechSystem : EcsSystem
{
    [ForEach]
    private static void Say(Entity player, in Link link, in Speaking speaking)
    {
        link.Client.Send(new Say(speaking.Text));
        player.Remove<Speaking>();
    }
}

/// <summary>
/// Avatars that left the 3x3 cells around the viewer's own avatar are out of view: the server no longer reports them,
/// so the client forgets them (the same grid rule the map uses).
/// </summary>
public sealed partial class ViewSystem : EcsSystem
{
    public override int Priority => 10; // after movement

    [ForEach]
    private static void Forget(Entity avatar, in Remote remote, in Position position)
    {
        if (MapGeometry.Near(MapGeometry.CellOf(position), MapGeometry.CellOf(remote.Viewer.Get<Position>()))) return;
        remote.Viewer.Get<View>().Avatars.Remove(remote.Player);
        avatar.Destroy();
    }
}
