namespace DaServer.Concurrency;

/// <summary>
/// Thread-safe bounded pool of <typeparamref name="T"/> (one pool per type: the thread caches belong to the type).
/// Each thread rents from and returns to two magazines of its own (<see cref="MagazineSize"/> objects each, no
/// synchronization); only a whole magazine moves through the shared depot, so the depot's cursors are touched once per
/// 32 objects instead of once per object, and threads that rent (shards) and threads that return (IO threads) pass
/// full magazines to each other. Rent and return never allocate once warm; when the depot is empty a new instance is
/// created, when it is full the returned instance is dropped. For objects rented on one thread and returned on another;
/// a thread that keeps its objects uses <c>PerThread</c>, a shard its ECS singleton.
/// </summary>
internal sealed class ObjectPool<T> where T : class
{
    private const int MagazineSize = 32;

    private sealed class Magazine
    {
        public readonly T?[] Items = new T?[MagazineSize];
        public int Count;
    }

    // Each magazine is full or empty except the loaded one, so a thread holds at most 2 x MagazineSize objects.
    private sealed class Cache
    {
        public Magazine Loaded = new(), Previous = new();
    }

    [ThreadStatic] private static Cache? t_cache;

    private readonly MpmcQueue<Magazine> _full, _empty;
    private readonly Func<T> _factory;

    /// <param name="capacity">Objects the depot holds (in full magazines), besides the threads' own.</param>
    public ObjectPool(Func<T> factory, int capacity = 1024)
    {
        _factory = factory;
        int magazines = Math.Max(2, capacity / MagazineSize);
        _full = new MpmcQueue<Magazine>(magazines);
        _empty = new MpmcQueue<Magazine>(magazines);
    }

    public T Rent()
    {
        var cache = t_cache ??= new Cache();
        var loaded = cache.Loaded;
        if (loaded.Count == 0)
        {
            if (cache.Previous.Count > 0)
                (cache.Loaded, cache.Previous) = (cache.Previous, loaded);
            else if (_full.TryDequeue(out var full))
            {
                _empty.TryEnqueue(loaded);
                cache.Loaded = full;
            }
            else
                return _factory();

            loaded = cache.Loaded;
        }

        ref var slot = ref loaded.Items[--loaded.Count];
        var item = slot!;
        slot = null;
        return item;
    }

    public void Return(T item)
    {
        var cache = t_cache ??= new Cache();
        var loaded = cache.Loaded;
        if (loaded.Count == MagazineSize)
        {
            if (cache.Previous.Count == 0)
                (cache.Loaded, cache.Previous) = (cache.Previous, loaded);
            else if (_full.TryEnqueue(cache.Previous))
            {
                cache.Previous = loaded;
                cache.Loaded = _empty.TryDequeue(out var empty) ? empty : new Magazine(); // new only while warming up
            }
            else
                return; // the depot is full: drop it

            loaded = cache.Loaded;
        }

        loaded.Items[loaded.Count++] = item;
    }
}
