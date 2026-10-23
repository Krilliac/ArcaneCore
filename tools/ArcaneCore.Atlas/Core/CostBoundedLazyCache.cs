using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace ArcaneCore.Atlas.Core;

/// <summary>
/// A least-recently-used cache of lazily loaded values, bounded by summed cost. Concurrent lookups of one key share one
/// load, and a load that throws is not cached (the next caller loads again). The cost of an entry is counted under the
/// gate, and only while that exact entry is still the cached one, so <see cref="Clear"/> and a racing load never make the
/// cost drift.
/// <para>
/// Two eviction timings are supported. By default an entry is counted, and the cold end evicted, once its load has
/// finished, and the most recently used entry is never evicted (so a single entry dearer than the budget is still kept).
/// With <c>evictOnInsert</c> every entry costs 1, is counted when it is inserted (before its load runs) and the cold end
/// is evicted at once, which can drop an entry whose load is still in flight; that load still completes for the callers
/// already waiting on it.
/// </para>
/// <para>
/// Thread affinity: any thread. The loader runs outside the gate. Values must be safe to read from several threads; the
/// cache never mutates them.
/// </para>
/// </summary>
/// <typeparam name="TKey">The lookup key.</typeparam>
/// <typeparam name="TValue">The cached value.</typeparam>
internal sealed class CostBoundedLazyCache<TKey, TValue>(long maxCost, bool evictOnInsert = false)
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<Entry>> _entries = new();
    private readonly LinkedList<TKey> _lru = new();
    private readonly Dictionary<TKey, Slot> _slots = new();
    private readonly Lock _gate = new();
    private long _cost;
    private long _hits;
    private long _loads;

    private sealed class Slot(LinkedListNode<TKey> node, long cost)
    {
        public LinkedListNode<TKey> Node { get; } = node;

        public long Cost { get; } = cost;
    }

    private sealed record Entry(TValue Value, long Cost);

    /// <summary>Number of cached keys, loaded or loading.</summary>
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

    /// <summary>Summed cost of the counted entries.</summary>
    public long Cost => Interlocked.Read(ref _cost);

    /// <summary>Lookups answered from the cache.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Lookups that ran their loader to completion.</summary>
    public long Loads => Interlocked.Read(ref _loads);

    /// <summary>The cached value when a load for <paramref name="key"/> has finished; never loads and does not change the recency order.</summary>
    public bool TryPeek(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_entries.TryGetValue(key, out Lazy<Entry>? lazy) && lazy.IsValueCreated)
        {
            value = lazy.Value.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The value for <paramref name="key"/>, from the cache or from <paramref name="load"/> (which returns the value and its
    /// cost; the cost is ignored with <c>evictOnInsert</c>). When <paramref name="keep"/> is given, a loaded value it rejects
    /// is returned to the callers that waited on it but is not kept. When <paramref name="cancellationToken"/> is given, the
    /// load is expected to observe that token: a load that was cancelled by another caller's token (this one not being
    /// cancelled) is forgotten and run again for this caller, and a cancelled token throws before each attempt.
    /// </summary>
    public TValue GetOrLoad(TKey key, Func<(TValue Value, long Cost)> load, Func<TValue, bool>? keep = null, CancellationToken? cancellationToken = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        while (true)
        {
            cancellationToken?.ThrowIfCancellationRequested();
            Lazy<Entry> lazy = evictOnInsert ? Admit(key, load) : _entries.GetOrAdd(key, _ => Create(load));
            bool hit = lazy.IsValueCreated;
            Entry entry;
            try
            {
                entry = lazy.Value;
            }
            catch (OperationCanceledException) when (cancellationToken is { IsCancellationRequested: false })
            {
                // Another caller's load was cancelled; it is not cached, so load again for this caller.
                Forget(key, lazy);
                continue;
            }
            catch
            {
                // A failed load is not cached; the next caller loads again.
                Forget(key, lazy);
                throw;
            }

            if (hit)
            {
                Interlocked.Increment(ref _hits);
            }

            if (keep is not null && !keep(entry.Value))
            {
                Forget(key, lazy);
            }
            else if (!evictOnInsert)
            {
                Touch(key, lazy, entry.Cost);
            }

            return entry.Value;
        }
    }

    /// <summary>
    /// Forgets the entries whose key satisfies <paramref name="match"/> and returns how many were forgotten. A load still in
    /// flight finishes for its own callers but is not cached and is not counted in the cost.
    /// </summary>
    public int Invalidate(Func<TKey, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        int removed = 0;
        foreach (TKey key in _entries.Keys)
        {
            if (!match(key))
            {
                continue;
            }

            lock (_gate)
            {
                if (!_entries.TryRemove(key, out _))
                {
                    continue;
                }

                removed++;
                if (_slots.Remove(key, out Slot? slot))
                {
                    _lru.Remove(slot.Node);
                    Interlocked.Add(ref _cost, -slot.Cost);
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Forgets every entry. A load still in flight finishes for its own callers but is not cached and is not counted in the
    /// cost, so the cost never drifts. The hit and load counters keep their values.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _slots.Clear();
            _lru.Clear();
            Interlocked.Exchange(ref _cost, 0);
        }
    }

    private Lazy<Entry> Create(Func<(TValue Value, long Cost)> load) => new(
        () =>
        {
            (TValue value, long cost) = load();
            Interlocked.Increment(ref _loads);
            return new Entry(value, cost);
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The evict-on-insert lookup: refreshes the recency of a known key, or inserts, counts and evicts for a new one.</summary>
    private Lazy<Entry> Admit(TKey key, Func<(TValue Value, long Cost)> load)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Lazy<Entry>? existing))
            {
                Slot known = _slots[key];
                _lru.Remove(known.Node);
                _lru.AddFirst(known.Node);
                return existing;
            }

            Lazy<Entry> lazy = Create(load);
            _entries[key] = lazy;
            _slots[key] = new Slot(_lru.AddFirst(key), 1);
            Interlocked.Increment(ref _cost);
            EvictWhileOverBudget();
            return lazy;
        }
    }

    /// <summary>
    /// Marks <paramref name="key"/> most recently used and evicts from the cold end while over budget. The cost of an entry
    /// is counted here, under the gate, and only while that exact entry is still the cached one.
    /// </summary>
    private void Touch(TKey key, Lazy<Entry> lazy, long cost)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Lazy<Entry>? current) || !ReferenceEquals(current, lazy))
            {
                return;
            }

            if (_slots.TryGetValue(key, out Slot? slot))
            {
                _lru.Remove(slot.Node);
                _lru.AddFirst(slot.Node);
            }
            else
            {
                _slots[key] = new Slot(_lru.AddFirst(key), cost);
                Interlocked.Add(ref _cost, cost);
            }

            EvictWhileOverBudget();
        }
    }

    /// <summary>Evicts from the cold end while over budget; must be called under the gate.</summary>
    private void EvictWhileOverBudget()
    {
        while (Interlocked.Read(ref _cost) > maxCost && _lru.Last is { } last && (evictOnInsert || last != _lru.First))
        {
            TKey victim = last.Value;
            _lru.Remove(last);
            if (_slots.Remove(victim, out Slot? gone))
            {
                Interlocked.Add(ref _cost, -gone.Cost);
            }

            _entries.TryRemove(victim, out _);
        }
    }

    private void Forget(TKey key, Lazy<Entry> lazy)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Lazy<Entry>? current) && ReferenceEquals(current, lazy))
            {
                _entries.TryRemove(key, out _);
                if (_slots.Remove(key, out Slot? slot))
                {
                    _lru.Remove(slot.Node);
                    Interlocked.Add(ref _cost, -slot.Cost);
                }
            }
        }
    }
}
