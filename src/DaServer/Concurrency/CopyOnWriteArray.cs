using System;
using System.Collections.Generic;
using System.Threading;

namespace DaServer.Concurrency;

/// <summary>
/// A small set read far more often than written (a group's sessions, a session's groups and bindings). Readers take
/// <see cref="Items"/>, an immutable snapshot, without locking; writers copy the array under a lock and publish the
/// copy with <see cref="Volatile.Write{T}(ref T, T)"/>. Writes allocate (they are control operations, not hot paths).
/// </summary>
internal sealed class CopyOnWriteArray<T>
{
    private readonly Lock _gate = new();
    private T[] _items = [];

    /// <summary>Current snapshot: never mutated, safe to iterate from any thread.</summary>
    public T[] Items => Volatile.Read(ref _items);

    /// <summary>Add <paramref name="item"/> unless an equal item is present.</summary>
    public bool Add(T item) => Set(item, item, static (a, b) => EqualityComparer<T>.Default.Equals(a, b), replace: false);

    /// <summary>Remove the item equal to <paramref name="item"/>.</summary>
    public bool Remove(T item) => Remove(item, static (a, b) => EqualityComparer<T>.Default.Equals(a, b));

    /// <summary>Replace the item matching <paramref name="key"/>, or add <paramref name="item"/> when none does.</summary>
    public void Set<TKey>(TKey key, T item, Func<T, TKey, bool> match) => Set(key, item, match, replace: true);

    /// <summary>Remove the item matching <paramref name="key"/>.</summary>
    public bool Remove<TKey>(TKey key, Func<T, TKey, bool> match)
    {
        lock (_gate)
        {
            var items = _items;
            int i = IndexOf(items, key, match);
            if (i < 0) return false;
            var next = new T[items.Length - 1];
            Array.Copy(items, 0, next, 0, i);
            Array.Copy(items, i + 1, next, i, items.Length - i - 1);
            Volatile.Write(ref _items, next);
            return true;
        }
    }

    private bool Set<TKey>(TKey key, T item, Func<T, TKey, bool> match, bool replace)
    {
        lock (_gate)
        {
            var items = _items;
            int i = IndexOf(items, key, match);
            if (i >= 0 && !replace) return false;
            T[] next;
            if (i >= 0)
            {
                next = (T[])items.Clone();
                next[i] = item;
            }
            else
            {
                next = new T[items.Length + 1];
                Array.Copy(items, next, items.Length);
                next[^1] = item;
            }

            Volatile.Write(ref _items, next);
            return true;
        }
    }

    private static int IndexOf<TKey>(T[] items, TKey key, Func<T, TKey, bool> match)
    {
        for (int i = 0; i < items.Length; i++)
            if (match(items[i], key)) return i;
        return -1;
    }
}
