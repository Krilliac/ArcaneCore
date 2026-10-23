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
    private readonly CostBoundedLazyCache<TKey, ParsedFile<TValue>> _cache = new(maxCost);

    /// <summary>Counters for status reports.</summary>
    public ParseCacheStats Stats => new(_cache.Count, _cache.Cost, _cache.Hits, _cache.Loads);

    /// <summary>The cached result when a load for <paramref name="key"/> has finished; never loads.</summary>
    public bool TryPeek(TKey key, out ParsedFile<TValue> file) => _cache.TryPeek(key, out file);

    /// <summary>The result for <paramref name="key"/>, from the cache or from <paramref name="load"/> (which returns the result and its cost).</summary>
    public ParsedFile<TValue> GetOrLoad(TKey key, Func<(ParsedFile<TValue> File, long Cost)> load) => _cache.GetOrLoad(key, load);

    /// <summary>
    /// Forgets every entry. A load still in flight finishes for its own callers but is not cached and is not counted in the
    /// cost, so the cost never drifts.
    /// </summary>
    public void Clear() => _cache.Clear();
}
