namespace Koios.Core;

/// <summary>
/// Bounded LRU cache for relational query results. Keys embed the snapshot_id,
/// so a watcher snapshot swap invalidates naturally — entries for old snapshots
/// simply stop being looked up and age out. In-memory only, thread-safe.
/// </summary>
public sealed class QueryCache
{
    private readonly object gate = new();
    private readonly int capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, object Value)>> map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, object Value)> order = new(); // most recently used first

    public QueryCache(int capacity) => this.capacity = capacity;

    public bool TryGet(string key, out object value)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                order.Remove(node);
                order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null!;
        return false;
    }

    public void Set(string key, object value)
    {
        lock (gate)
        {
            if (map.Remove(key, out var existing))
                order.Remove(existing);
            map[key] = order.AddFirst((key, value));
            if (map.Count > capacity)
            {
                var oldest = order.Last!;
                order.RemoveLast();
                map.Remove(oldest.Value.Key);
            }
        }
    }
}
