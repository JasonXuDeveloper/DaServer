// netstandard2.1 only: the read side of System.Collections.Frozen used here, over a Dictionary (never mutated after
// creation, so reads need no lock).
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace System.Collections.Frozen;

internal sealed class FrozenDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _items;

    internal FrozenDictionary(Dictionary<TKey, TValue> items) => _items = items;

    public static FrozenDictionary<TKey, TValue> Empty { get; } = new(new Dictionary<TKey, TValue>());

    public int Count => _items.Count;

    public TValue this[TKey key] => _items[key];

    public bool ContainsKey(TKey key) => _items.ContainsKey(key);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _items.TryGetValue(key, out value);

    public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

internal static class FrozenDictionary
{
    public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> items)
        where TKey : notnull
    {
        var copy = new Dictionary<TKey, TValue>();
        foreach (var item in items) copy[item.Key] = item.Value;
        return new FrozenDictionary<TKey, TValue>(copy);
    }
}
