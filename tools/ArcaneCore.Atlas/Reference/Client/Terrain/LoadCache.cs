namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// A small least-recently-used cache whose loads run once per key even when many threads ask at the same time. A load
/// that throws is not cached, and neither is a result the caller's predicate rejects (callers use that to keep absent or
/// refused files out, so a file that appears or is repaired later is found). All members are thread-safe; the loader
/// runs outside the cache lock.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Cached value type.</typeparam>
internal sealed class LoadCache<TKey, TValue>(int capacity)
    where TKey : notnull
    where TValue : class
{
    private readonly Dictionary<TKey, Entry> _entries = [];
    private readonly LinkedList<TKey> _recent = new();
    private readonly Lock _gate = new();

    /// <summary>Number of cached keys (loaded or loading).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// The value for <paramref name="key"/>, loading it with <paramref name="load"/> when it is not cached. Callers that
    /// were waiting on the same load all receive its value; afterwards the value stays cached only when
    /// <paramref name="keep"/> is null or returns true for it.
    /// </summary>
    public TValue GetOrLoad(TKey key, Func<TValue> load, Func<TValue, bool>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        Lazy<TValue> lazy;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? entry))
            {
                _recent.Remove(entry.Node);
                _recent.AddFirst(entry.Node);
                lazy = entry.Value;
            }
            else
            {
                lazy = new Lazy<TValue>(load, LazyThreadSafetyMode.ExecutionAndPublication);
                _entries[key] = new Entry(lazy, _recent.AddFirst(key));
                while (_entries.Count > capacity && _recent.Last is { } oldest)
                {
                    _recent.RemoveLast();
                    _entries.Remove(oldest.Value);
                }
            }
        }

        TValue value;
        try
        {
            value = lazy.Value;
        }
        catch
        {
            Forget(key, lazy);
            throw;
        }

        if (keep is not null && !keep(value))
        {
            Forget(key, lazy);
        }

        return value;
    }

    /// <summary>The value when it is already loaded; never starts a load and does not change the recency order.</summary>
    public bool TryPeek(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? entry) && entry.Value.IsValueCreated)
            {
                value = entry.Value.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Drops every cached value.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recent.Clear();
        }
    }

    private void Forget(TKey key, Lazy<TValue> lazy)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? entry) && ReferenceEquals(entry.Value, lazy))
            {
                _recent.Remove(entry.Node);
                _entries.Remove(key);
            }
        }
    }

    private sealed record Entry(Lazy<TValue> Value, LinkedListNode<TKey> Node);
}
