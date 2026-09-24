namespace Remex.Desktop.Services;

/// <summary>
/// A small capacity-bounded least-recently-used map (perf audit P0-20). Both
/// <see cref="TryGetValue"/> and <see cref="Set"/> count as a use.
/// </summary>
/// <remarks>
/// Written for the Remote Desktop cursor-shape cache: the host mints a new shape serial on every
/// cursor change, so a plain dictionary keyed by serial grew for the whole session. Values that
/// leave the cache - evicted, replaced by a different instance under the same key, or cleared - are
/// handed to <c>onRemoved</c> so the owner can release them (the cursor cache disposes its bitmaps).
/// Not thread-safe; the cursor cache only touches it on the UI thread.
/// </remarks>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Action<TValue>? _onRemoved;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;

    // Most recently used at the head, eviction candidate at the tail.
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

    public LruCache(int capacity, Action<TValue>? onRemoved = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _onRemoved = onRemoved;
        _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
    }

    public int Count => _map.Count;

    public int Capacity => _capacity;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            MoveToFront(node);
            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            var old = existing.Value.Value;
            existing.Value = new KeyValuePair<TKey, TValue>(key, value);
            MoveToFront(existing);
            if (!ReferenceEquals(old, value))
            {
                _onRemoved?.Invoke(old);
            }
            return;
        }

        var node = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
        _map[key] = node;

        while (_map.Count > _capacity)
        {
            var eldest = _order.Last!;
            _order.RemoveLast();
            _map.Remove(eldest.Value.Key);
            _onRemoved?.Invoke(eldest.Value.Value);
        }
    }

    /// <summary>Drops every entry, handing each value to <c>onRemoved</c>.</summary>
    public void Clear()
    {
        if (_map.Count == 0)
        {
            return;
        }

        var removed = new List<TValue>(_order.Count);
        foreach (var pair in _order)
        {
            removed.Add(pair.Value);
        }

        _order.Clear();
        _map.Clear();

        if (_onRemoved is not null)
        {
            foreach (var value in removed)
            {
                _onRemoved(value);
            }
        }
    }

    private void MoveToFront(LinkedListNode<KeyValuePair<TKey, TValue>> node)
    {
        if (!ReferenceEquals(_order.First, node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
    }
}
