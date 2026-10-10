using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Maps.Collision.VMaps;

/// <summary>
/// The vmap-backed <see cref="ILineOfSight"/> (vmangos <c>VMapManager2</c>): one
/// <see cref="VMapTree"/> per map, created from <c>NNN.vmtree</c> on first use, tiles loaded and
/// released with the map's grids (<see cref="ICollisionTileLifecycle"/>), model files shared
/// between spawns and maps.
/// <para>
/// Missing data is normal and reads as open (a map without a vmtree, a tile without a vmtile);
/// unreadable or corrupt files are logged once and also read as open — never an exception into
/// the world thread. A corrupt tile is dropped whole; a spawn whose model is missing or corrupt,
/// whose slot is out of range or whose name is not a plain file name is skipped.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class VMapManager : ILineOfSight, ICollisionTileLifecycle, ICollisionTilePrefetch
{
    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, VMapTree?> _trees = [];
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
        _trees[mapId] = tree;
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

        string path = Path.Combine(_directory, VMapFormat.TileFileName(mapId, tileX, tileY));
        if (!_prefetch.TryTake(mapId, tileX, tileY, out List<(ModelSpawn Spawn, uint Slot)> spawns))
        {
            byte[]? bytes = ReadFile(path, missingIsNormal: true);
            if (bytes is null)
            {
                return false;
            }

            try
            {
                spawns = VMapTree.ParseTile(bytes);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogError("VMaps: {Path} is not a usable {Magic} tile ({Reason}); the tile is ignored", path, VMapFormat.Magic, ex.Message);
                return false;
            }
        }

        tree.AddTile((tileX, tileY), Instantiate(tree, spawns, path));
        return true;
    }

    /// <summary>Release a tile's models (vmangos <c>VMapManager2::unloadMap</c>).</summary>
    public void UnloadTile(uint mapId, int tileX, int tileY)
    {
        if (_trees.GetValueOrDefault(mapId) is { } tree)
        {
            tree.RemoveTile((tileX, tileY));
        }
    }

    void ICollisionTileLifecycle.OnTileLoaded(uint mapId, int tileX, int tileY) => LoadTile(mapId, tileX, tileY);

    /// <summary>Prefetched tiles the loader took instead of reading them (diagnostics and tests).</summary>
    internal int PrefetchHits => _prefetch.Hits;

    /// <summary>
    /// Read and parse a tile's <c>.vmtile</c> and the model files it names that are not cached yet, on the thread pool
    /// (<see cref="TilePrefetchCache{T}"/>), so <see cref="LoadTile"/> only instantiates them. Nothing happens for a map without vmap
    /// data, an untiled map or a tile already loaded. World thread.
    /// </summary>
    public void Prefetch(uint mapId, int tileX, int tileY)
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
                    // Left to the world thread, which reads it again and logs what it always logged.
                }
            }

            return spawns;
        });
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

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
    {
        var point = new Vector3(x, y, z);
        if (!HeightEnabled || !IsFinite(point) || _trees.GetValueOrDefault(mapId) is not { } tree)
        {
            return null;
        }

        return tree.GetHeight(VMapFormat.ToInternal(point), maxSearchDistance);
    }

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
        byte[]? bytes = ReadFile(path, missingIsNormal: true);
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
            _logger.LogError("VMaps: {Path} is not a usable {Magic} tree ({Reason}); map {MapId} has no line of sight data", path, VMapFormat.Magic, ex.Message, mapId);
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
            byte[]? bytes = ReadFile(path, missingIsNormal: false);
            if (bytes is not null)
            {
                try
                {
                    model = WorldModel.Parse(bytes);
                    ModelFilesLoaded++;
                }
                catch (InvalidDataException ex)
                {
                    _logger.LogError("VMaps: {Path} is not a usable model ({Reason}); its spawns are skipped", path, ex.Message);
                }
            }
        }

        _models[name] = model;
        _cachedModelNames.TryAdd(name, 0);
        return model;
    }

    private byte[]? ReadFile(string path, bool missingIsNormal)
    {
        try
        {
            if (!File.Exists(path))
            {
                if (!missingIsNormal)
                {
                    _logger.LogError("VMaps: could not find {Path}", path);
                }

                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "VMaps: could not read {Path}", path);
            return null;
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
