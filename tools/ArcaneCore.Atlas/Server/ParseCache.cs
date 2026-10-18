using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// The outcome of reading one data file with the server's own reader: the parsed value, or why there is none. A missing file
/// and a rejected file are different states and are never both reported as "empty".
/// </summary>
/// <param name="State"><see cref="TileState.Ok"/>, <see cref="TileState.Missing"/> or <see cref="TileState.Rejected"/>.</param>
/// <param name="Value">The parsed value when <see cref="State"/> is Ok; null otherwise.</param>
/// <param name="Detail">The file name for Ok, otherwise why there is no value.</param>
public readonly record struct ParsedFile<T>(TileState State, T? Value, string? Detail)
    where T : class
{
    /// <summary>True when the file was read and parsed.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool IsOk => State == TileState.Ok;

    /// <summary>A parsed file.</summary>
    public static ParsedFile<T> Ok(T value, string file) => new(TileState.Ok, value, file);

    /// <summary>No file.</summary>
    public static ParsedFile<T> Missing(string detail) => new(TileState.Missing, null, detail);

    /// <summary>A file the reader refused (or that could not be read).</summary>
    public static ParsedFile<T> Rejected(string detail) => new(TileState.Rejected, null, detail);
}

/// <summary>Hit, miss and size counters of a <see cref="ParseCache{TKey, TValue}"/>.</summary>
/// <param name="Entries">Parsed files held.</param>
/// <param name="Cost">Their summed cost (file bytes).</param>
/// <param name="Hits">Lookups answered from the cache.</param>
/// <param name="Loads">Lookups that read and parsed a file.</param>
public readonly record struct ParseCacheStats(int Entries, long Cost, long Hits, long Loads);

/// <summary>
/// Parsed files kept while hot, bounded by cost (file bytes), least recently used first out. Concurrent lookups of one key
/// share one load. A load that throws is not cached. A missing or rejected file is cached as such (it is a result, and
/// re-reading a corrupt file for every request would hide nothing and cost a disk read).
/// <para>
/// Thread affinity: any thread. Values must be immutable or safe to read from several threads; the cache never mutates them.
/// </para>
/// </summary>
/// <typeparam name="TKey">The lookup key.</typeparam>
/// <typeparam name="TValue">The parsed type.</typeparam>
public sealed class ParseCache<TKey, TValue>(long maxCost)
    where TKey : notnull
    where TValue : class
{
    private readonly ConcurrentDictionary<TKey, Lazy<Entry>> _entries = new();
    private readonly LinkedList<TKey> _lru = new();
    private readonly Dictionary<TKey, Slot> _slots = new();
    private readonly Lock _lruGate = new();
    private long _cost;
    private long _hits;
    private long _loads;

    private sealed class Slot(LinkedListNode<TKey> node, long cost)
    {
        public LinkedListNode<TKey> Node { get; } = node;

        public long Cost { get; } = cost;
    }

    private sealed record Entry(ParsedFile<TValue> File, long Cost);

    /// <summary>Counters for status reports.</summary>
    public ParseCacheStats Stats => new(_entries.Count, Interlocked.Read(ref _cost), Interlocked.Read(ref _hits), Interlocked.Read(ref _loads));

    /// <summary>The cached result when a load for <paramref name="key"/> has finished; never loads.</summary>
    public bool TryPeek(TKey key, out ParsedFile<TValue> file)
    {
        if (_entries.TryGetValue(key, out Lazy<Entry>? lazy) && lazy.IsValueCreated)
        {
            file = lazy.Value.File;
            return true;
        }

        file = default;
        return false;
    }

    /// <summary>The result for <paramref name="key"/>, from the cache or from <paramref name="load"/> (which returns the result and its cost).</summary>
    public ParsedFile<TValue> GetOrLoad(TKey key, Func<(ParsedFile<TValue> File, long Cost)> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        Lazy<Entry> lazy = _entries.GetOrAdd(key, _ => new Lazy<Entry>(
            () =>
            {
                (ParsedFile<TValue> file, long cost) = load();
                Interlocked.Increment(ref _loads);
                return new Entry(file, cost);
            },
            LazyThreadSafetyMode.ExecutionAndPublication));
        bool hit = lazy.IsValueCreated;
        try
        {
            Entry entry = lazy.Value;
            if (hit)
            {
                Interlocked.Increment(ref _hits);
            }

            Touch(key, lazy, entry.Cost);
            return entry.File;
        }
        catch
        {
            // A failed load is not cached; the next caller loads again.
            _entries.TryRemove(new KeyValuePair<TKey, Lazy<Entry>>(key, lazy));
            throw;
        }
    }

    /// <summary>
    /// Forgets every entry. A load still in flight finishes for its own callers but is not cached and is not counted in the
    /// cost, so the cost never drifts.
    /// </summary>
    public void Clear()
    {
        lock (_lruGate)
        {
            _entries.Clear();
            _slots.Clear();
            _lru.Clear();
            Interlocked.Exchange(ref _cost, 0);
        }
    }

    /// <summary>
    /// Marks <paramref name="key"/> most recently used and evicts from the cold end while over budget. The cost of an entry
    /// is counted here, under the gate, and only while that exact entry is still the cached one.
    /// </summary>
    private void Touch(TKey key, Lazy<Entry> lazy, long cost)
    {
        lock (_lruGate)
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

            while (Interlocked.Read(ref _cost) > maxCost && _lru.Last is { } last && last != _lru.First)
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
    }
}
