namespace DaServer.Runtime;

/// <summary>
/// Turns the completion of a <see cref="Turn"/>'s task into data: its continuation posts the watcher to the shard,
/// whose inbox tags the entity <see cref="TurnEnded"/>. Nothing polls running turns. Pooled per shard
/// (<see cref="TurnWatchers"/>); the continuation delegate is created once per watcher, so watching allocates nothing.
/// </summary>
internal sealed class TurnWatcher
{
    private readonly Shard _shard;
    private readonly Action _completed;

    private TurnWatcher(Shard shard)
    {
        _shard = shard;
        _completed = Completed;
    }

    /// <summary>The entity whose turn is watched (default while pooled).</summary>
    public Entity Entity { get; private set; }

    /// <summary>
    /// Start <paramref name="task"/> as the turn of <paramref name="entity"/> (shard thread). The entity must not have a
    /// turn already; it gets <see cref="Turn"/> now and <see cref="TurnEnded"/> once the task completed.
    /// </summary>
    public static void Begin(World world, Entity entity, ValueTask task)
    {
        entity.Add<Turn>() = new Turn(task);
        var pool = world.GetSingleton<TurnWatchers>().Pool;
        var watcher = pool.Count > 0 ? pool.Pop() : new TurnWatcher(world.Shard());
        watcher.Entity = entity;
        // No captured context: the continuation runs where the task completes and only posts (thread-safe).
        task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(watcher._completed);
    }

    /// <summary>Inbox side (shard thread): mark the turn ended and return the watcher to the pool.</summary>
    public void Ended(World world)
    {
        var entity = Entity;
        Entity = default;
        world.GetSingleton<TurnWatchers>().Pool.Push(this);
        entity.Add<TurnEnded>();
    }

    /// <summary>
    /// The turn's result, read once after <see cref="TurnEnded"/> (what <c>await</c> does after its continuation ran):
    /// observes the exception and returns pooled state machines to their pools.
    /// </summary>
    public static Exception? Result(in Turn turn)
    {
        try
        {
            turn.Task.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private void Completed() => _shard.Post(new WorkItem(WorkKind.TurnEnded, this, null));
}
