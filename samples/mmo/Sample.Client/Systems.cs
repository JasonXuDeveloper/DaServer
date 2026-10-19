using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>The client's systems, for the world that holds its players (a game's, or a swarm's loop).</summary>
public static class ClientSystems
{
    public static void AddTo(World world)
    {
        world.AddSystem<PushSystem>();
        world.AddSystem<SteeringSystem>();
        world.AddSystem<MovementSystem>();
        world.AddSystem<SpeakingSystem>();
        world.AddSystem<ViewSystem>();
    }

    /// <summary>What the loop runs on every pass, between ticks: pushes as they arrive.</summary>
    public static void OnPass(World world) => world.Update<ClientPushes>();
}

/// <summary>Pushes: handled as soon as they arrive (every pass of the client's loop), not at the simulation's tick rate.</summary>
public struct ClientPushes : ISystemGroup;

/// <summary>Runs each player's queued pushes on the world's thread (<see cref="Pushes"/>).</summary>
[UpdateInGroup<ClientPushes>]
public sealed partial class PushSystem : EcsSystem
{
    [ForEach]
    private static void Run(in Link link) => link.Client.ProcessCallbacks();
}

/// <summary>Input: the player's steering moves its avatar at once (prediction) and goes to the map when it changes.</summary>
[UpdateBefore<MovementSystem>]
public sealed partial class SteeringSystem : EcsSystem
{
    [ForEach]
    private static void Steer(in Link link, in Me me, in Steering steering, ref SentSteering sent, ref Velocity velocity)
    {
        if (me.Map.Value == 0 || (steering.X == sent.X && steering.Y == sent.Y)) return; // input goes to the map it is in
        sent = new SentSteering(steering.X, steering.Y);
        velocity = MapGeometry.Clamp(steering.X, steering.Y); // the map clamps the same way
        link.Client.Send(new Move(steering.X, steering.Y));
    }
}

/// <summary>Says what the player wants to say, once.</summary>
public sealed partial class SpeakingSystem : EcsSystem
{
    [ForEach]
    private static void Say(Entity player, in Link link, in Speaking speaking)
    {
        link.Client.Send(new Say(speaking.Text));
        player.Remove<Speaking>();
    }
}

/// <summary>
/// Forgets the avatars that left the 3x3 cells around the player's own avatar: the map no longer reports them (the
/// same grid rule). Twice a second is plenty for what is out of sight.
/// </summary>
public sealed partial class ViewSystem : EcsSystem
{
    public override int UpdateInterval => Timing.TickRate / 2;

    [ForEach]
    private static void Forget(in Position position, ref View view)
    {
        int cell = MapGeometry.CellOf(position);
        long now = Environment.TickCount64;
        foreach (var (player, track) in view.Avatars)
            if (!MapGeometry.Near(MapGeometry.CellOf(track.Now(now)), cell)) view.Leaving.Add(player);
        foreach (var player in view.Leaving) view.Avatars.Remove(player);

        view.Leaving.Clear();
    }
}
