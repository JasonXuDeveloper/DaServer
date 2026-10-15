namespace DaServer.Runtime;

/// <summary>Removes ended asynchronous service calls (errors were already replied; this logs and recycles).</summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class ServiceCallSystem : EcsSystem
{
    public override int Priority => 2;

    [ForEach, With<TurnEnded>, With<ServiceCall>]
    private static void End(Entity call, in Turn turn, World world)
    {
        var error = TurnWatcher.Result(in turn);
        call.Destroy();
        if (error != null) world.Logger().LogError(error, "Service call failed");
    }
}
