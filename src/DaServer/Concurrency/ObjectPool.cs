using System;

namespace DaServer.Concurrency;

/// <summary>
/// Thread-safe bounded pool backed by <see cref="MpmcQueue{T}"/>. Rent/return never allocate once warm;
/// when the pool is empty a new instance is created, when full the returned instance is dropped.
/// </summary>
public sealed class ObjectPool<T> where T : class
{
    private readonly MpmcQueue<T> _items;
    private readonly Func<T> _factory;

    public ObjectPool(Func<T> factory, int capacity = 1024)
    {
        _factory = factory;
        _items = new MpmcQueue<T>(capacity);
    }

    public T Rent() => _items.TryDequeue(out var item) ? item : _factory();

    public void Return(T item) => _items.TryEnqueue(item);
}
