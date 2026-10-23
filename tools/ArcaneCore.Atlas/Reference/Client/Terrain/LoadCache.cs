using ArcaneCore.Atlas.Core;

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
    private readonly CostBoundedLazyCache<TKey, TValue> _cache = new(capacity, evictOnInsert: true);

    /// <summary>Number of cached keys (loaded or loading).</summary>
    public int Count => _cache.Count;

    /// <summary>
    /// The value for <paramref name="key"/>, loading it with <paramref name="load"/> when it is not cached. Callers that
    /// were waiting on the same load all receive its value; afterwards the value stays cached only when
    /// <paramref name="keep"/> is null or returns true for it.
    /// </summary>
    public TValue GetOrLoad(TKey key, Func<TValue> load, Func<TValue, bool>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        return _cache.GetOrLoad(key, () => (load(), 1), keep);
    }

    /// <summary>The value when it is already loaded; never starts a load and does not change the recency order.</summary>
    public bool TryPeek(TKey key, out TValue? value) => _cache.TryPeek(key, out value);

    /// <summary>Drops every cached value.</summary>
    public void Clear() => _cache.Clear();
}
