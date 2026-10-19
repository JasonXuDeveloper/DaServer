namespace DaServer.Runtime;

/// <summary>
/// Turns the completion of a <see cref="Turn"/>'s task into data: its continuation posts the watcher to the shard,
/// whose dispatch queue tags the entity <see cref="TurnEnded"/>. Nothing polls running turns. Pooled per shard
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
        entity.Set<Turn>(new Turn(task, world.GetSingleton<ShardActivity>().NowMs, Tracing.TakePending()));
        if (task.IsCompleted)
        {
            // A hook that finished synchronously (the usual OnLoad/OnUnload): registering a continuation on a completed
            // task would allocate one and run it through the thread pool.
            entity.Set(new TurnEnded());
            return;
        }

        var pool = world.GetSingleton<TurnWatchers>().Pool;
        var watcher = pool.Count > 0 ? pool.Pop() : new TurnWatcher(world.Shard);
        watcher.Entity = entity;
        // No captured context: the continuation runs where the task completes and only posts (thread-safe).
        task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(watcher._completed);
    }

    /// <summary>Dispatch side (shard thread): mark the turn ended and return the watcher to the pool.</summary>
    public void Ended(World world)
    {
        var entity = Entity;
        Entity = default;
        world.GetSingleton<TurnWatchers>().Pool.Push(this);
        entity.Set(new TurnEnded());
    }

    /// <summary>
    /// The turn's result, read once after <see cref="TurnEnded"/> (what <c>await</c> does after its continuation ran):
    /// observes the exception and returns pooled state machines to their pools; a traced turn's span ends.
    /// </summary>
    public static Exception? Result(in Turn turn)
    {
        Exception? error = null;
        try
        {
            turn.Task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            error = e;
        }

        if (turn.Span != null) Tracing.Ended(turn.Span, error);
        return error;
    }

    private void Completed() => _shard.Post(new WorkItem(WorkKind.TurnEnded, this, null));
}
