using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace DaServer.Generator;

/// <summary>Array with value equality, so generator models cache correctly between edits. default == empty.</summary>
internal readonly struct EquatableArray<T>(T[]? items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly T[]? _items = items;

    private T[] Items => _items ?? Array.Empty<T>();

    public int Length => Items.Length;

    public T this[int index] => Items[index];

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode() => Items.Aggregate(17, (h, item) => h * 31 + (item?.GetHashCode() ?? 0));

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
