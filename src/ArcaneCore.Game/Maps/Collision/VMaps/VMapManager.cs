using ArcaneCore.Kernel.Diagnostics;
using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Maps.Collision.VMaps;

/// <summary>
/// The vmap-backed <see cref="ILineOfSight"/> (vmangos <c>VMapManager2</c>): one
/// <see cref="VMapTree"/> per map, created from <c>NNN.vmtree</c> on first use, tiles loaded and
/// released with the map's grids (<see cref="ICollisionTileLifecycle"/>) or loaded for a question about a place no grid covers
/// (<see cref="ICollisionTileDemand"/>, at most <see cref="MaxOnDemandTiles"/> per map), model files shared between spawns and maps.
/// <para>
/// Missing data is normal and reads as open (a map without a vmtree, a tile without a vmtile);
/// unreadable or corrupt files are logged once and also read as open — never an exception into
/// the world thread. A corrupt tile is dropped whole; a spawn whose model is missing or corrupt,
/// whose slot is out of range or whose name is not a plain file name is skipped.
/// </para>
/// <para>Thread affinity: world thread (or inside the serial gate). The exception is a ground drape in shared mode
/// (<see cref="VMapSharedReader"/>): <see cref="GetModelHeight"/> then reads loaded tiles from any map thread under the read side of
/// one lock, and every change of the loaded tiles takes its write side.</para>
/// </summary>
public sealed class VMapManager : ILineOfSight, IModelCoverage, ICollisionTileLifecycle, ICollisionTilePrefetch, ICollisionTileDemand
{
    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, VMapTree?> _trees = [];

    // Read side: shared ground drapes on map threads outside the serial gate (GetModelHeightShared). Write side: every change of _trees,
    // _emptyTiles and the loaded tiles of a VMapTree (the install or release only, never a file read or parse). See VMapSharedReader.
    private readonly ReaderWriterLockSlim _sharedLock = new(LockRecursionPolicy.NoRecursion);

    // Tiles whose load was attempted and whose vmtile file does not exist (vmangos StaticMapTree::LoadMapTile: loaded, empty). Kept apart from
    // VMapTree.IsTileLoaded, which keeps meaning "a model tile was read" (anti-cheat HasModels uses it). Cleared on unload.
    private readonly HashSet<(uint MapId, int X, int Y)> _emptyTiles = [];

    // Tiles LoadTileOnDemand loaded that no grid holds (an empty one too: releasing it forgets that it is empty), each with when it was
    // last asked for; the lowest is released first.
    private readonly Dictionary<(uint MapId, int X, int Y), long> _onDemand = [];
    private long _onDemandAsks;

    private readonly Dictionary<string, WorldModel?> _models = new(StringComparer.Ordinal);

    // Prefetch (TilePrefetchCache): parsed tile spawn lists, the models they need that were not cached yet (parsed on the pool), and
    // the names already cached (read by the pool to skip them; written on the world thread).
    private readonly TilePrefetchCache<List<(ModelSpawn Spawn, uint Slot)>> _prefetch = new();
    private readonly ConcurrentDictionary<string, WorldModel> _prefetchedModels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _cachedModelNames = new(StringComparer.Ordinal);
    private const int MaxPrefetchedModels = 512;

    public VMapManager(string directory, bool enableLineOfSight = true, bool enableHeight = true, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _logger = logger ?? NullLogger.Instance;
        LineOfSightEnabled = enableLineOfSight;
        HeightEnabled = enableHeight;
    }

    /// <summary>vmangos <c>isLineOfSightCalcEnabled</c>.</summary>
    public bool LineOfSightEnabled { get; }

    /// <summary>vmangos <c>isHeightCalcEnabled</c> (also gates area info).</summary>
    public bool HeightEnabled { get; }

    public bool Enabled => LineOfSightEnabled || HeightEnabled;

    /// <summary>Model files read so far (diagnostics and tests).</summary>
    public int ModelFilesLoaded { get; private set; }

    /// <summary>The map's tree, reading its vmtree on first use; null when the map has no (usable) vmap data.</summary>
    public VMapTree? GetTree(uint mapId)
    {
        if (_trees.TryGetValue(mapId, out VMapTree? cached))
        {
            return cached;
        }

        VMapTree? tree = LoadTree(mapId);
        _sharedLock.EnterWriteLock();
        try
        {
            _trees[mapId] = tree;
        }
        finally
        {
            _sharedLock.ExitWriteLock();
        }

        return tree;
    }

    /// <summary>Load a terrain tile's models (vmangos <c>VMapManager2::loadMap</c>). False when nothing could be loaded.</summary>
    public bool LoadTile(uint mapId, int tileX, int tileY)
    {
        if (GetTree(mapId) is not { } tree)
        {
            return false;
        }

        if (!tree.IsTiled || tree.IsTileLoaded(tileX, tileY))
        {
            return true;
        }

        if (_emptyTiles.Contains((mapId, tileX, tileY)))
        {
            return false; // same answer as the attempt that found no file: no model tile is loaded
        }

        string path = Path.Combine(_directory, VMapFormat.TileFileName(mapId, tileX, tileY));
        if (!_prefetch.TryTake(mapId, tileX, tileY, out List<(ModelSpawn Spawn, uint Slot)> spawns))
        {
            byte[]? bytes = ReadFile(path, missingIsNormal: true, out bool missing);
            if (bytes is null)
            {
                if (missing)
                {
                    // vmangos StaticMapTree::LoadMapTile: a missing tile file is a tile without models. Remembered apart from the tree's
                    // loaded tiles so IsTileLoaded (and anti-cheat HasModels) stay false; CoverageAt answers Loaded for it.
                    _sharedLock.EnterWriteLock();
                    try
                    {
                        _emptyTiles.Add((mapId, tileX, tileY));
                    }
                    finally
                    {
                        _sharedLock.ExitWriteLock();
                    }
                }

                return false;
            }

            try
            {
                spawns = VMapTree.ParseTile(bytes);
            }
            catch (InvalidDataException ex)
            {
                FaultLedger.Shared.Record("collision.vmap-tile", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {mapId} tile ({tileX}, {tileY})", ex.Message);
                _logger.LogError(ex, "VMaps: {Path} is not a usable {Magic} tile ({Reason}); the tile is ignored", path, VMapFormat.Magic, ex.Message);
                return false;
            }
        }

        List<(ModelInstance Instance, uint Slot)> instances = Instantiate(tree, spawns, path); // file and model reads outside the lock
        _sharedLock.EnterWriteLock();
        try
        {
            tree.AddTile((tileX, tileY), instances);
        }
        finally
        {
            _sharedLock.ExitWriteLock();
        }

        return true;
    }

    /// <summary>Release a tile's models (vmangos <c>VMapManager2::unloadMap</c>).</summary>
    public void UnloadTile(uint mapId, int tileX, int tileY)
    {
        _onDemand.Remove((mapId, tileX, tileY)); // owner-only bookkeeping: no shared reader looks at it
        _sharedLock.EnterWriteLock();
        try
        {
            _emptyTiles.Remove((mapId, tileX, tileY)); // a reload tries the file again
            if (_trees.GetValueOrDefault(mapId) is { } tree)
            {
                tree.RemoveTile((tileX, tileY));
            }
        }
        finally
        {
            _sharedLock.ExitWriteLock();
        }
    }

    /// <summary>Whether the model tile under (<paramref name="x"/>, <paramref name="y"/>) is loaded or known to be empty (a grid or an on-demand request holds it). World thread.</summary>
    public bool HoldsTile(uint mapId, float x, float y)
        => Terrain.TerrainTile.TileOf(x, y) is { } tile
            && (_emptyTiles.Contains((mapId, tile.X, tile.Y)) || _trees.GetValueOrDefault(mapId) is { } tree && tree.IsTileLoaded(tile.X, tile.Y));

    /// <summary>
    /// Release the tile under (<paramref name="x"/>, <paramref name="y"/>) only when it is held on demand, never a tile a grid holds.
    /// For a caller that loaded it for one question and no longer needs it. World thread.
    /// </summary>
    public bool ReleaseOnDemandTile(uint mapId, float x, float y)
    {
        if (Terrain.TerrainTile.TileOf(x, y) is not { } tile || !_onDemand.ContainsKey((mapId, tile.X, tile.Y)))
        {
            return false;
        }

        UnloadTile(mapId, tile.X, tile.Y);
        return true;
    }

    /// <summary>Models parsed and cached, loaded tiles or not (diagnostics and tests).</summary>
    public int CachedModelCount => _models.Count;

    /// <summary>
    /// Drop the cached models no loaded tile uses (the cache otherwise only grows: a model stays after the last tile naming it unloads).
    /// A later tile that names one parses it again. Returns how many were dropped. World thread.
    /// </summary>
    public int TrimUnusedModels()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (VMapTree? tree in _trees.Values)
        {
            tree?.CollectModelNames(used);
        }

        string[] unused = [.. _models.Where(m => m.Value is not null && !used.Contains(m.Key)).Select(m => m.Key)];
        // No lock: _models is read and written by the owner thread only (AcquireModel), a shared reader never looks at it, and the models
        // its tiles hold stay reachable through their ModelInstance objects, so dropping a cache entry frees nothing a reader uses.
        foreach (string name in unused)
        {
            _models.Remove(name);
            _cachedModelNames.TryRemove(name, out _);
        }

        return unused.Length;
    }

    void ICollisionTileLifecycle.OnTileLoaded(uint mapId, int tileX, int tileY)
    {
        _onDemand.Remove((mapId, tileX, tileY)); // the grid holds it now
        LoadTile(mapId, tileX, tileY);
    }

    /// <summary>
    /// The most model tiles kept loaded per map for <see cref="LoadTileOnDemand"/> beyond those the grids hold; the one asked for longest
    /// ago is released first. The lifts the bots ride stand on few tiles (Kalimdor's four lifts on four), so this bounds the worst case.
    /// </summary>
    public const int MaxOnDemandTiles = 16;

    /// <summary>Tiles loaded on demand and not held by a grid (tests and inspection).</summary>
    public int OnDemandTileCount => _onDemand.Count;

    /// <summary>
    /// <see cref="ICollisionTileDemand.LoadTileOnDemand"/>: the models of the terrain tile under (<paramref name="x"/>,
    /// <paramref name="y"/>), held on demand among at most <see cref="MaxOnDemandTiles"/> of its map. Answers as <see cref="LoadTile"/>
    /// does: false for a tile without models or without a usable file, true on an untiled map (its models came with the tree).
    /// </summary>
    public bool LoadTileOnDemand(uint mapId, float x, float y)
    {
        if (Terrain.TerrainTile.TileOf(x, y) is not { } tile || GetTree(mapId) is not { } tree)
        {
            return false;
        }

        if (!tree.IsTiled)
        {
            return true;
        }

        (uint, int, int) key = (mapId, tile.X, tile.Y);
        if (tree.IsTileLoaded(tile.X, tile.Y) || _emptyTiles.Contains(key))
        {
            // Held on demand: asked for now. A tile a grid holds is not in the table and stays the grid's.
            if (_onDemand.ContainsKey(key))
            {
                _onDemand[key] = ++_onDemandAsks;
            }

            return tree.IsTileLoaded(tile.X, tile.Y);
        }

        MakeOnDemandRoom(mapId);
        bool loaded = LoadTile(mapId, tile.X, tile.Y);
        if (loaded || _emptyTiles.Contains(key))
        {
            _onDemand[key] = ++_onDemandAsks;
        }

        return loaded;
    }

    /// <summary>With <see cref="MaxOnDemandTiles"/> of the map held on demand, release the one asked for longest ago.</summary>
    private void MakeOnDemandRoom(uint mapId)
    {
        int held = 0;
        (uint MapId, int X, int Y) oldest = default;
        long oldestAsk = long.MaxValue;
        foreach (((uint MapId, int X, int Y) tile, long ask) in _onDemand)
        {
            if (tile.MapId != mapId)
            {
                continue;
            }

            held++;
            if (ask < oldestAsk)
            {
                oldest = tile;
                oldestAsk = ask;
            }
        }

        if (held >= MaxOnDemandTiles)
        {
            UnloadTile(oldest.MapId, oldest.X, oldest.Y);
        }
    }

    /// <summary>Prefetched tiles the loader took instead of reading them (diagnostics and tests).</summary>
    internal int PrefetchHits => _prefetch.Hits;

    /// <summary>Whether a prefetch of the tile was asked for, is finished and has not been taken (diagnostics and tests).</summary>
    internal bool IsPrefetchFinished(uint mapId, int tileX, int tileY) => _prefetch.Contains(mapId, tileX, tileY) && !_prefetch.IsRunning(mapId, tileX, tileY);

    /// <summary>
    /// Read and parse a tile's <c>.vmtile</c> and the model files it names that are not cached yet, on the thread pool
    /// (<see cref="TilePrefetchCache{T}"/>), so <see cref="LoadTile"/> only instantiates them. Nothing happens for a map without vmap
    /// data, an untiled map or a tile already loaded. World thread.
    /// </summary>
    public void Prefetch(uint mapId, int tileX, int tileY) => Prefetch(mapId, tileX, tileY, arrival: false);

    /// <summary><see cref="Prefetch(uint, int, int)"/> for a teleport's destination, against the arrival budget (<see cref="TilePrefetchCache{T}.MaxArrivalEntries"/>). World thread.</summary>
    public void PrefetchArrival(uint mapId, int tileX, int tileY) => Prefetch(mapId, tileX, tileY, arrival: true);

    private void Prefetch(uint mapId, int tileX, int tileY, bool arrival)
    {
        if (GetTree(mapId) is not { IsTiled: true } tree || tree.IsTileLoaded(tileX, tileY))
        {
            return;
        }

        if (_prefetchedModels.Count > MaxPrefetchedModels)
        {
            _prefetchedModels.Clear(); // models of tiles nobody loaded; a later load reads what it needs itself
        }

        string path = Path.Combine(_directory, VMapFormat.TileFileName(mapId, tileX, tileY));
        _prefetch.Request(mapId, tileX, tileY, () =>
        {
            if (!File.Exists(path))
            {
                return null;
            }

            List<(ModelSpawn Spawn, uint Slot)> spawns = VMapTree.ParseTile(File.ReadAllBytes(path));
            foreach ((ModelSpawn spawn, uint _) in spawns)
            {
                string name = spawn.Name;
                if (_cachedModelNames.ContainsKey(name) || _prefetchedModels.ContainsKey(name) || !VMapFormat.IsSafeModelName(name))
                {
                    continue;
                }

                string modelPath = Path.Combine(_directory, VMapFormat.ModelFileName(name));
                try
                {
                    if (File.Exists(modelPath))
                    {
                        _prefetchedModels.TryAdd(name, WorldModel.Parse(File.ReadAllBytes(modelPath)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    FaultLedger.Shared.Record("collision.vmap-prefetch", ex);
                    AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {mapId} tile ({tileX}, {tileY})", ex.Message);
                    // Left to the world thread, which reads it again and logs what it always logged.
                }
            }

            return spawns;
        }, arrival);
    }

    void ICollisionTileLifecycle.OnTileUnloaded(uint mapId, int tileX, int tileY) => UnloadTile(mapId, tileX, tileY);

    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
    {
        if (!LineOfSightEnabled || !IsFinite(from) || !IsFinite(to) || _trees.GetValueOrDefault(mapId) is not { } tree)
        {
            return true;
        }

        Vector3 a = VMapFormat.ToInternal(from);
        Vector3 b = VMapFormat.ToInternal(to);
        return a == b || tree.IsInLineOfSight(a, b, ignoreM2);
    }

    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        hit = to;
        if (!LineOfSightEnabled || !IsFinite(from) || !IsFinite(to) || _trees.GetValueOrDefault(mapId) is not { } tree)
        {
            return false;
        }

        if (!tree.TryGetObjectHit(VMapFormat.ToInternal(from), VMapFormat.ToInternal(to), modifyDistance, out Vector3 internalHit))
        {
            return false;
        }

        hit = VMapFormat.ToWorld(internalHit);
        return true;
    }

    /// <summary>Coverage of a terrain tile. Reads the map's vmtree from disk on first use (<see cref="GetTree"/>).</summary>
    public ModelCoverage CoverageAt(uint mapId, int tileX, int tileY)
    {
        if (!HeightEnabled)
        {
            return ModelCoverage.Unavailable;
        }

        if (GetTree(mapId) is not { } tree)
        {
            return ModelCoverage.None;
        }

        return !tree.IsTiled || tree.IsTileLoaded(tileX, tileY) || _emptyTiles.Contains((mapId, tileX, tileY))
            ? ModelCoverage.Loaded
            : ModelCoverage.TileNotLoaded;
    }

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
    {
        var point = new Vector3(x, y, z);
        if (VMapSharedReader.Active)
        {
            return HeightEnabled && IsFinite(point) ? GetModelHeightShared(mapId, point, maxSearchDistance) : null;
        }

        if (!HeightEnabled || !IsFinite(point) || _trees.GetValueOrDefault(mapId) is not { } tree)
        {
            return null;
        }

        return tree.GetHeight(VMapFormat.ToInternal(point), maxSearchDistance);
    }

    /// <summary>
    /// <see cref="GetModelHeight"/> for a ground drape outside the serial gate (<see cref="VMapSharedReader"/>), from any thread: the same
    /// <see cref="VMapTree.GetHeight"/> over the same loaded tiles, under the read side of the lock. When the map's tree has not been read
    /// yet or the model tile under the point is not loaded (and not known to be empty) it answers null and records the abort, so the drape is
    /// repeated inside the gate, where a tile may be loaded; it never loads anything itself.
    /// </summary>
    private float? GetModelHeightShared(uint mapId, Vector3 point, float maxSearchDistance)
    {
        // A shared drape takes the read lock once for all its samples (VMapSharedReader.Scope); a bare shared-mode caller takes it per call.
        bool take = !VMapSharedReader.LockHeld;
        if (take)
        {
            _sharedLock.EnterReadLock();
        }

        try
        {
            if (!_trees.TryGetValue(mapId, out VMapTree? tree))
            {
                VMapSharedReader.Abort(SharedDrapeAbort.ModelTileNotLoaded);
                return null;
            }

            if (tree is null)
            {
                return null; // the map has no vmap data: open, as for the owner
            }

            if (tree.IsTiled
                && (Terrain.TerrainTile.TileOf(point.X, point.Y) is not { } tile
                    || !(tree.IsTileLoaded(tile.X, tile.Y) || _emptyTiles.Contains((mapId, tile.X, tile.Y)))))
            {
                VMapSharedReader.Abort(SharedDrapeAbort.ModelTileNotLoaded);
                return null;
            }

            return tree.GetHeight(VMapFormat.ToInternal(point), maxSearchDistance);
        }
        finally
        {
            if (take)
            {
                _sharedLock.ExitReadLock();
            }
        }
    }

    /// <summary>The lock shared reads hold for reading and every change of the loaded tiles for writing (tests: the write-lock seam).</summary>
    internal ReaderWriterLockSlim SharedLock => _sharedLock;

    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
    {
        info = default;
        var point = new Vector3(x, y, z);
        if (!HeightEnabled || !IsFinite(point) || _trees.GetValueOrDefault(mapId) is not { } tree)
        {
            return false;
        }

        return tree.TryGetAreaInfo(VMapFormat.ToInternal(point), out info);
    }

    private VMapTree? LoadTree(uint mapId)
    {
        string path = Path.Combine(_directory, VMapFormat.TreeFileName(mapId));
        byte[]? bytes = ReadFile(path, missingIsNormal: true, out _);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            (VMapTree tree, List<(ModelSpawn Spawn, uint Slot)> global) = VMapTree.Parse(mapId, bytes);
            if (!tree.IsTiled)
            {
                tree.AddTile((-1, -1), Instantiate(tree, global, path));
            }

            _logger.LogInformation("VMaps: map {MapId} tree loaded ({Slots} spawn slots, tiled: {Tiled})", mapId, tree.Tree.PrimitiveCount, tree.IsTiled);
            return tree;
        }
        catch (InvalidDataException ex)
        {
            FaultLedger.Shared.Record("collision.vmap-tree", ex);
            AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {mapId}", ex.Message);
            _logger.LogError(ex, "VMaps: {Path} is not a usable {Magic} tree ({Reason}); map {MapId} has no line of sight data", path, VMapFormat.Magic, ex.Message, mapId);
            return null;
        }
    }

    private List<(ModelInstance Instance, uint Slot)> Instantiate(VMapTree tree, List<(ModelSpawn Spawn, uint Slot)> spawns, string source)
    {
        var result = new List<(ModelInstance, uint)>(spawns.Count);
        foreach ((ModelSpawn spawn, uint slot) in spawns)
        {
            if (!tree.IsValidSlot(slot))
            {
                _logger.LogError("VMaps: {Path}: spawn {Id} names tree slot {Slot} of {Count}; skipped", source, spawn.Id, slot, tree.Tree.PrimitiveCount);
                continue;
            }

            if (AcquireModel(spawn.Name) is { } model)
            {
                result.Add((new ModelInstance(spawn, model), slot));
            }
        }

        return result;
    }

    private WorldModel? AcquireModel(string name)
    {
        if (_models.TryGetValue(name, out WorldModel? cached))
        {
            return cached;
        }

        WorldModel? model = null;
        if (_prefetchedModels.TryRemove(name, out WorldModel? prefetched))
        {
            model = prefetched;
            ModelFilesLoaded++;
        }
        else if (!VMapFormat.IsSafeModelName(name))
        {
            _logger.LogError("VMaps: refusing model name '{Name}' (not a plain file name)", name);
        }
        else
        {
            string path = Path.Combine(_directory, VMapFormat.ModelFileName(name));
            byte[]? bytes = ReadFile(path, missingIsNormal: false, out _);
            if (bytes is not null)
            {
                try
                {
                    model = WorldModel.Parse(bytes);
                    ModelFilesLoaded++;
                }
                catch (InvalidDataException ex)
                {
                    FaultLedger.Shared.Record("collision.vmap-model", ex);
                    AnomalyLedger.Shared.Report("collision", "tile-load-failed", name, ex.Message);
                    _logger.LogError(ex, "VMaps: {Path} is not a usable model ({Reason}); its spawns are skipped", path, ex.Message);
                }
            }
        }

        _models[name] = model;
        _cachedModelNames.TryAdd(name, 0);
        return model;
    }

    private byte[]? ReadFile(string path, bool missingIsNormal, out bool missing)
    {
        missing = false;
        try
        {
            if (!File.Exists(path))
            {
                if (!missingIsNormal)
                {
                    _logger.LogError("VMaps: could not find {Path}", path);
                }

                missing = true;
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FaultLedger.Shared.Record("collision.vmap-read", ex);
            AnomalyLedger.Shared.Report("collision", "tile-load-failed", path, ex.Message);
            _logger.LogError(ex, "VMaps: could not read {Path}", path);
            return null;
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
