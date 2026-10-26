using System.Collections.Concurrent;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>What the reader found for one tile file.</summary>
public enum NavTileState
{
    /// <summary>The file was read and DotRecast holds the tile.</summary>
    Ok,

    /// <summary>No file for this tile.</summary>
    Missing,

    /// <summary>The file exists but was refused (parse error, inconsistent counts, or DotRecast would not take it).</summary>
    Rejected,
}

/// <summary>The state of one tile and, when it is not <see cref="NavTileState.Ok"/>, why.</summary>
public sealed record NavTileOutcome(NavTileState State, string? Detail);

/// <summary>
/// A tile held by a <see cref="DtNavMesh"/>: the parsed file, the mesh's tile object and the terrain tile it came from.
/// Valid only inside the <see cref="ReferenceNavMap.Read{T}"/> callback that returned it (the tile can be evicted afterwards).
/// </summary>
public sealed class ResidentNavTile
{
    // Written with Interlocked by readers holding the read lock, read by the evicting writer; a plain field so it can be passed by ref.
    internal long LastUse;

    /// <summary>Terrain tile indices (the file name's), see <see cref="WorldGrid"/>.</summary>
    public required TileKey Key { get; init; }

    /// <summary>The parsed file. <see cref="ParsedNavTile.Data"/> belongs to the mesh while the tile is resident.</summary>
    public required ParsedNavTile File { get; init; }

    /// <summary>The mesh's tile object (links included).</summary>
    public DtMeshTile Tile { get; internal set; } = null!;

    /// <summary>The mesh's reference of the tile (changes if the mesh is rebuilt).</summary>
    public long TileRef { get; internal set; }
}

/// <summary>
/// A consistent look at one map's DotRecast mesh: valid only while the <see cref="ReferenceNavMap.Read{T}"/> callback runs, during
/// which no tile is added or removed. Never keep it, its <see cref="Mesh"/> or any <see cref="DtMeshTile"/> after the callback.
/// </summary>
public sealed class NavMapView
{
    private readonly ReferenceNavMap _map;
    private readonly IReadOnlyDictionary<TileKey, NavTileOutcome> _outcomes;

    internal NavMapView(ReferenceNavMap map, DtNavMesh mesh, IReadOnlyDictionary<TileKey, NavTileOutcome> outcomes)
    {
        _map = map;
        Mesh = mesh;
        _outcomes = outcomes;
    }

    /// <summary>The map id.</summary>
    public uint MapId => _map.MapId;

    /// <summary>The <c>.mmap</c> parameters as the server's reader parses them.</summary>
    public NavMeshParams Parameters => _map.Parameters;

    /// <summary>The DotRecast mesh holding the requested tiles (and any others still resident).</summary>
    public DtNavMesh Mesh { get; }

    /// <summary>The outcome of each tile the caller asked for.</summary>
    public IReadOnlyDictionary<TileKey, NavTileOutcome> Tiles => _outcomes;

    /// <summary>The resident tile for a terrain tile, or null (not requested, missing, rejected or evicted).</summary>
    public ResidentNavTile? Resident(TileKey key) => _map.ResidentUnlocked(key);

    /// <summary>The terrain tile a mesh tile was read from (via its Detour coordinates), or null when it is not resident.</summary>
    public TileKey? TerrainTileOf(DtMeshTile tile)
    {
        DtMeshHeader h = tile.data.header;
        return _map.TerrainTileOfDetourUnlocked(h.x, h.y, h.layer);
    }
}

/// <summary>
/// One map's navmesh as DotRecast loads it: the <c>.mmap</c> parameters, a <see cref="DtNavMesh"/> and the tiles added to it on
/// demand from <c>NNNXXYY.mmtile</c>. At most <c>maxResident</c> tiles stay in memory; the least recently used tile that no running
/// <see cref="Read{T}"/> asked for is removed first (DotRecast unlinks its neighbours).
/// </summary>
/// <remarks>
/// Thread safety: any thread. Files are read and parsed without a lock; adding or removing a tile takes the write lock and every
/// <see cref="Read{T}"/> body runs under the read lock, so a query never sees a tile half linked. Bodies must not call
/// <see cref="Read{T}"/> again. DotRecast's <c>DtNavMesh</c> and the <see cref="DtMeshData"/> it holds are not thread safe by themselves.
/// </remarks>
public sealed class ReferenceNavMap : IDisposable
{
    private const int MaxReadAttempts = 8;

    private readonly string _directory;
    private readonly int _maxResident;
    private readonly ILogger _log;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<TileKey, ResidentNavTile> _resident = [];
    private readonly Dictionary<(int X, int Y, int Layer), TileKey> _byDetour = [];
    private readonly Dictionary<TileKey, Absent> _absent = [];
    private readonly ConcurrentDictionary<TileKey, Lazy<TileLoad>> _inflight = new();
    private readonly DtNavMeshParams _dtParams;
    private DtNavMesh _mesh;
    private long _useClock;

    private sealed record Absent(NavTileState State, string Detail, long Length, long WriteTicks);

    private sealed record TileLoad(NavTileState State, string? Detail, ParsedNavTile? File, long Length, long WriteTicks);

    internal ReferenceNavMap(uint mapId, string directory, NavMeshParams parameters, int maxResident, ILogger log)
    {
        MapId = mapId;
        _directory = directory;
        Parameters = parameters;
        _maxResident = maxResident;
        _log = log;
        _dtParams = new DtNavMeshParams
        {
            orig = new RcVec3f(parameters.Origin.X, parameters.Origin.Y, parameters.Origin.Z),
            tileWidth = parameters.TileWidth,
            tileHeight = parameters.TileHeight,
            maxTiles = parameters.MaxTiles,

            // vmangos writes maxPolys = 0 because its Detour uses 64-bit polygon references.
            // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:462 (maxPolys = 0; // Unused if DT_POLYREF64 set.)
            // DotRecast's Init stores the value without reading it, so any positive number serves.
            // ref: tests/ThirdParty/DotRecast/DotRecast.Detour/DtNavMesh.cs:50-77 (Init copies the parameters; maxPolys is never used).
            maxPolys = parameters.MaxPolys > 0 ? parameters.MaxPolys : 1 << 16,
        };
        _mesh = NewMesh();
    }

    /// <summary>The map id.</summary>
    public uint MapId { get; }

    /// <summary>The <c>.mmap</c> parameters.</summary>
    public NavMeshParams Parameters { get; }

    /// <summary>Tiles currently held by the mesh.</summary>
    public int ResidentCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _resident.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Full path of the tile's file (it may not exist).</summary>
    public string FilePath(TileKey tile) => Path.Combine(_directory, WorldGrid.NavTileFileName(tile));

    /// <summary>
    /// The tile's state when that is known without reading it: resident, known refused, or no file. Null when the file exists
    /// and has not been read (or has changed since it was refused).
    /// </summary>
    public NavTileOutcome? Peek(TileKey tile)
    {
        var info = new FileInfo(FilePath(tile));
        _lock.EnterReadLock();
        try
        {
            if (_resident.ContainsKey(tile))
            {
                return new NavTileOutcome(NavTileState.Ok, null);
            }

            if (!info.Exists)
            {
                return new NavTileOutcome(NavTileState.Missing, $"No {info.Name} in {_directory}.");
            }

            return _absent.TryGetValue(tile, out Absent? a) && a.State == NavTileState.Rejected
                && a.Length == info.Length && a.WriteTicks == info.LastWriteTimeUtc.Ticks
                ? new NavTileOutcome(NavTileState.Rejected, a.Detail)
                : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Makes <paramref name="tiles"/> resident (reading the files that are not), then runs <paramref name="body"/> with the mesh
    /// held steady. A tile with no file or a refused file is not an error: its outcome is in <see cref="NavMapView.Tiles"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A tile belongs to another map or lies outside the 64 x 64 grid.</exception>
    /// <exception cref="InvalidOperationException">Concurrent queries evicted the tiles again on every attempt (the resident limit is smaller than the working sets in flight).</exception>
    public T Read<T>(IReadOnlyCollection<TileKey> tiles, Func<NavMapView, T> body)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(body);
        foreach (TileKey tile in tiles)
        {
            if (tile.MapId != MapId || !tile.IsInGrid)
            {
                throw new ArgumentException($"Tile {tile} is not a grid tile of map {MapId}.", nameof(tiles));
            }
        }

        for (int attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            Ensure(tiles);
            _lock.EnterReadLock();
            try
            {
                if (TryOutcomes(tiles, out Dictionary<TileKey, NavTileOutcome>? outcomes))
                {
                    long stamp = Interlocked.Increment(ref _useClock);
                    foreach (TileKey tile in tiles)
                    {
                        if (_resident.TryGetValue(tile, out ResidentNavTile? resident))
                        {
                            Interlocked.Exchange(ref resident.LastUse, stamp);
                        }
                    }

                    return body(new NavMapView(this, _mesh, outcomes));
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        throw new InvalidOperationException(
            $"Navmesh tiles of map {MapId} were evicted by concurrent queries {MaxReadAttempts} times in a row; the resident tile limit ({_maxResident}) is below the working set.");
    }

    /// <summary>Frees the lock. The map must not be used afterwards.</summary>
    public void Dispose() => _lock.Dispose();

    // Called by NavMapView inside a Read callback, which holds the read lock.
    internal ResidentNavTile? ResidentUnlocked(TileKey key) => _resident.GetValueOrDefault(key);

    internal TileKey? TerrainTileOfDetourUnlocked(int x, int y, int layer)
        => _byDetour.TryGetValue((x, y, layer), out TileKey key) ? key : null;

    private DtNavMesh NewMesh()
    {
        var mesh = new DtNavMesh();
        DtStatus status = mesh.Init(_dtParams, NavMeshFormat.MaxVertsPerPoly);
        return status.Failed() ? throw new InvalidDataException($"DotRecast refused the navmesh parameters of map {MapId} (status 0x{status.Value:x}).") : mesh;
    }

    private bool TryOutcomes(IReadOnlyCollection<TileKey> tiles, out Dictionary<TileKey, NavTileOutcome> outcomes)
    {
        outcomes = new Dictionary<TileKey, NavTileOutcome>(tiles.Count);
        foreach (TileKey tile in tiles)
        {
            if (_resident.ContainsKey(tile))
            {
                outcomes[tile] = new NavTileOutcome(NavTileState.Ok, null);
            }
            else if (_absent.TryGetValue(tile, out Absent? a))
            {
                outcomes[tile] = new NavTileOutcome(a.State, a.Detail);
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private void Ensure(IReadOnlyCollection<TileKey> required)
    {
        List<TileKey>? toLoad = null;
        _lock.EnterReadLock();
        try
        {
            foreach (TileKey tile in required)
            {
                if (_resident.ContainsKey(tile))
                {
                    continue;
                }

                if (_absent.TryGetValue(tile, out Absent? a) && StillAbsent(tile, a))
                {
                    continue;
                }

                (toLoad ??= []).Add(tile);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (toLoad is null)
        {
            return;
        }

        var loaded = new List<(TileKey Tile, TileLoad Load)>(toLoad.Count);
        foreach (TileKey tile in toLoad)
        {
            loaded.Add((tile, LoadFile(tile)));
        }

        _lock.EnterWriteLock();
        try
        {
            foreach ((TileKey tile, TileLoad load) in loaded)
            {
                if (_resident.ContainsKey(tile))
                {
                    continue;
                }

                if (load.File is null)
                {
                    _absent[tile] = new Absent(load.State, load.Detail ?? string.Empty, load.Length, load.WriteTicks);
                    continue;
                }

                Install(tile, load);
            }

            Evict([.. required]);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private bool StillAbsent(TileKey tile, Absent absent)
    {
        var info = new FileInfo(FilePath(tile));
        return absent.State == NavTileState.Missing
            ? !info.Exists
            : info.Exists && info.Length == absent.Length && info.LastWriteTimeUtc.Ticks == absent.WriteTicks;
    }

    private TileLoad LoadFile(TileKey tile)
    {
        Lazy<TileLoad> lazy = _inflight.GetOrAdd(tile, t => new Lazy<TileLoad>(() => ReadAndParse(t), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<TileKey, Lazy<TileLoad>>(tile, lazy));
        }
    }

    private TileLoad ReadAndParse(TileKey tile)
    {
        string path = FilePath(tile);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new TileLoad(NavTileState.Missing, $"No {info.Name} in {_directory}.", null, 0, 0);
        }

        long length = info.Length;
        long ticks = info.LastWriteTimeUtc.Ticks;
        try
        {
            return new TileLoad(NavTileState.Ok, null, MmapTileReader.Parse(File.ReadAllBytes(path)), length, ticks);
        }
        catch (InvalidDataException e)
        {
            _log.LogWarning("Navmesh tile {Path} refused: {Reason}", path, e.Message);
            return new TileLoad(NavTileState.Rejected, e.Message, null, length, ticks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Navmesh tile {Path} could not be read: {Reason}", path, e.Message);
            return new TileLoad(NavTileState.Rejected, $"could not read the file: {e.Message}", null, length, ticks);
        }
    }

    // Called with the write lock held.
    private void Install(TileKey tile, TileLoad load)
    {
        ParsedNavTile file = load.File!;
        DtMeshHeader h = file.Data.header;
        (int X, int Y, int Layer) detour = (h.x, h.y, h.layer);
        if (_byDetour.TryGetValue(detour, out TileKey holder))
        {
            Reject(tile, load, $"claims Detour tile ({h.x}, {h.y}, layer {h.layer}), which terrain tile {holder.X},{holder.Y} already holds");
            return;
        }

        DtStatus status;
        long tileRef;
        try
        {
            status = _mesh.AddTile(file.Data, 0, 0, out tileRef);
        }
        catch (IndexOutOfRangeException e)
        {
            // DotRecast allocates links from the tile's own table without a bounds check (the free list is threaded through it and
            // every AllocLink caller indexes it): a table smaller than the polygons need throws part-way, after the tile is already
            // in the mesh's hash.
            // ref: tests/ThirdParty/DotRecast/DotRecast.Detour/DtNavMesh.cs:455-459 (links[maxLinkCount - 1].next, unchecked).
            Reject(tile, load, $"DotRecast AddTile ran past the tile's link table ({file.Data.header.maxLinkCount} links): {e.Message}");
            Rebuild();
            return;
        }

        if (status.Failed())
        {
            Reject(tile, load, $"DotRecast AddTile failed with status 0x{status.Value:x}{(status.Has(DtStatus.DT_OUT_OF_MEMORY) ? " (out of tiles: the .mmap allows " + Parameters.MaxTiles + ")" : string.Empty)}");
            return;
        }

        _resident[tile] = new ResidentNavTile
        {
            Key = tile,
            File = file,
            Tile = _mesh.GetTileByRef(tileRef),
            TileRef = tileRef,
            LastUse = Interlocked.Increment(ref _useClock),
        };
        _byDetour[detour] = tile;
        _absent.Remove(tile);
    }

    private void Reject(TileKey tile, TileLoad load, string reason)
    {
        _log.LogWarning("Navmesh tile {Tile} of map {Map} refused: {Reason}", tile, MapId, reason);
        _absent[tile] = new Absent(NavTileState.Rejected, reason, load.Length, load.WriteTicks);
    }

    // Replaces a mesh that a failed AddTile left half linked by a new one holding the resident tiles again. A tile whose data
    // fails the same way is dropped (and refused) and the pass starts over.
    private void Rebuild()
    {
        while (true)
        {
            DtNavMesh mesh = NewMesh();
            TileKey? failed = null;
            foreach ((TileKey key, ResidentNavTile resident) in _resident)
            {
                try
                {
                    DtStatus status = mesh.AddTile(resident.File.Data, 0, 0, out long tileRef);
                    if (status.Failed())
                    {
                        failed = key;
                        break;
                    }

                    resident.TileRef = tileRef;
                    resident.Tile = mesh.GetTileByRef(tileRef);
                }
                catch (IndexOutOfRangeException)
                {
                    failed = key;
                    break;
                }
            }

            if (failed is not { } bad)
            {
                _mesh = mesh;
                return;
            }

            ResidentNavTile dropped = _resident[bad];
            _resident.Remove(bad);
            DtMeshHeader h = dropped.File.Data.header;
            _byDetour.Remove((h.x, h.y, h.layer));
            _absent[bad] = new Absent(NavTileState.Rejected, "DotRecast could not re-link the tile after a failed neighbour", dropped.File.FileBytes, 0);
        }
    }

    // Called with the write lock held: removes least recently used tiles beyond the limit, never one of `keep`.
    private void Evict(HashSet<TileKey> keep)
    {
        while (_resident.Count > _maxResident)
        {
            ResidentNavTile? victim = null;
            foreach (ResidentNavTile candidate in _resident.Values)
            {
                if (!keep.Contains(candidate.Key) && (victim is null || candidate.LastUse < victim.LastUse))
                {
                    victim = candidate;
                }
            }

            if (victim is null)
            {
                return;
            }

            _mesh.RemoveTile(victim.TileRef);
            DtMeshHeader h = victim.File.Data.header;
            _byDetour.Remove((h.x, h.y, h.layer));
            _resident.Remove(victim.Key);
        }
    }
}
