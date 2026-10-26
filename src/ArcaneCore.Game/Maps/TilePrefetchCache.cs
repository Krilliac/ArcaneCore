using System.Diagnostics;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// Tile files read and parsed ahead of need on the thread pool, so that creating a grid (which loads its terrain, vmap and navmesh
/// tiles on the world thread, vmangos <c>TerrainInfo::LoadMapAndVMap</c>) installs an already parsed tile instead of reading and
/// parsing it in the middle of a tick. Only the timing moves: the parse is the same pure function of the same file, a failed or
/// unfinished prefetch is simply not used (the loader then does exactly what it did without it, logging included), and a prefetch
/// nobody takes expires.
/// <para>
/// Thread affinity: <see cref="Request"/> and <see cref="TryTake"/> run on the world thread (the owning loader's thread); only the
/// load functions run on the pool, and they must touch nothing but the files they read.
/// </para>
/// </summary>
internal sealed class TilePrefetchCache<T>
    where T : class
{
    /// <summary>How long an untaken prefetch is kept.</summary>
    internal const long ExpiryMs = 120_000;

    /// <summary>At most this many approach prefetches are kept (the oldest go first).</summary>
    internal const int MaxEntries = 64;

    /// <summary>
    /// At most this many arrival prefetches (a teleport's destination, <see cref="Request"/> with <c>arrival</c>) are kept, a budget of their own
    /// so a burst of teleports (bots travel by teleport from many places) never evicts the approach prefetches of players walking toward
    /// ungenerated grids, and walking players never evict an arrival's tiles. The oldest arrival goes first.
    /// </summary>
    internal const int MaxArrivalEntries = 32;

    private readonly Dictionary<(uint Map, int X, int Y), (Task<T?> Load, long RequestedMs, bool Arrival)> _entries = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Prefetched tiles the loader took (diagnostics and tests).</summary>
    internal int Hits { get; private set; }

    /// <summary>Prefetches started.</summary>
    internal int Requests { get; private set; }

    /// <summary>Prefetches waiting to be taken.</summary>
    internal int Pending => _entries.Count;

    /// <summary>
    /// Start loading a tile in the background unless it is already on its way. <paramref name="arrival"/>: asked for a teleport's destination,
    /// which counts against <see cref="MaxArrivalEntries"/> instead of <see cref="MaxEntries"/>.
    /// </summary>
    internal void Request(uint mapId, int tileX, int tileY, Func<T?> load, bool arrival = false)
    {
        if (_entries.ContainsKey((mapId, tileX, tileY)))
        {
            return;
        }

        Sweep(arrival);
        Requests++;
        _entries[(mapId, tileX, tileY)] = (Task.Run(() =>
        {
            try
            {
                return load();
            }
            catch (Exception)
            {
                return null; // the loader reads it again itself and reports the problem as it always did
            }
        }), _clock.ElapsedMilliseconds, arrival);
    }

    /// <summary>The prefetched tile if it finished; the entry is dropped either way (an unfinished one is not waited for).</summary>
    internal bool TryTake(uint mapId, int tileX, int tileY, out T value)
    {
        if (_entries.Remove((mapId, tileX, tileY), out (Task<T?> Load, long RequestedMs, bool Arrival) entry)
            && entry.Load.IsCompletedSuccessfully && entry.Load.Result is { } loaded)
        {
            Hits++;
            value = loaded;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>Whether a prefetch of the tile is still being read (not finished, not taken).</summary>
    internal bool IsRunning(uint mapId, int tileX, int tileY)
        => _entries.TryGetValue((mapId, tileX, tileY), out (Task<T?> Load, long RequestedMs, bool Arrival) entry) && !entry.Load.IsCompleted;

    /// <summary>Whether a prefetch of the tile was asked for and not taken yet, finished or not (diagnostics and tests).</summary>
    internal bool Contains(uint mapId, int tileX, int tileY) => _entries.ContainsKey((mapId, tileX, tileY));

    /// <summary>Forget a tile's prefetch (it was loaded some other way).</summary>
    internal void Forget(uint mapId, int tileX, int tileY) => _entries.Remove((mapId, tileX, tileY));

    private void Sweep(bool arrival)
    {
        long now = _clock.ElapsedMilliseconds;
        if (_entries.Count == 0)
        {
            return;
        }

        List<(uint, int, int)>? expired = null;
        (uint, int, int)? oldest = null;
        long oldestMs = long.MaxValue;
        int sameClass = 0;
        foreach (((uint, int, int) key, (Task<T?> _, long requested, bool entryArrival)) in _entries)
        {
            if (now - requested > ExpiryMs)
            {
                (expired ??= []).Add(key);
            }
            else if (entryArrival == arrival)
            {
                sameClass++;
                if (requested < oldestMs)
                {
                    oldestMs = requested;
                    oldest = key;
                }
            }
        }

        if (expired is not null)
        {
            foreach ((uint, int, int) key in expired)
            {
                _entries.Remove(key);
            }
        }

        if (sameClass >= (arrival ? MaxArrivalEntries : MaxEntries) && oldest is { } drop)
        {
            _entries.Remove(drop);
        }
    }
}
