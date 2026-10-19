namespace DaServer.Concurrency;

internal static class ListExtensions
{
    /// <summary>Remove the item at <paramref name="index"/> in O(1): the last one takes its place (order does not matter).</summary>
    public static void SwapRemoveAt<T>(this List<T> list, int index)
    {
        list[index] = list[^1];
        list.RemoveAt(list.Count - 1);
    }
}
