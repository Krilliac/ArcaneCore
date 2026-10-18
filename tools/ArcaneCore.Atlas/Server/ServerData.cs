using System.Buffers.Binary;
using System.Globalization;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Server;

/// <summary>A model spawn as a <c>.vmtile</c> or <c>.vmtree</c> lists it: the placement and the tree slot the server loads it into.</summary>
public sealed record VMapSpawn(ModelSpawn Spawn, uint Slot);

/// <summary>The spawns of one <c>.vmtile</c> (<see cref="VMapTree.ParseTile"/>), kept with the set of slots they use.</summary>
public sealed class VMapTileData
{
    /// <summary>Wraps the parsed spawn list.</summary>
    public VMapTileData(IReadOnlyList<VMapSpawn> spawns)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        Spawns = spawns;
        Slots = [.. spawns.Select(s => s.Slot)];
    }

    /// <summary>The spawns in file order.</summary>
    public IReadOnlyList<VMapSpawn> Spawns { get; }

    /// <summary>The tree slots the spawns occupy (a slot is one model placement map-wide).</summary>
    public HashSet<uint> Slots { get; }
}

/// <summary>What a <c>NNN.vmtree</c> says about a map, without the BIH (<see cref="VMapTree.Parse"/>).</summary>
/// <param name="IsTiled">False for maps whose models all live in the tree file itself (WMO-only maps).</param>
/// <param name="SlotCount">The number of spawn slots the tree has (a spawn naming a slot at or past it is skipped by the server).</param>
/// <param name="GlobalSpawns">The tree file's own spawns (untiled maps only).</param>
public sealed record VMapTreeData(bool IsTiled, int SlotCount, IReadOnlyList<VMapSpawn> GlobalSpawns);

/// <summary>A parsed <c>.mmtile</c> with the header numbers read independently of the server's parser.</summary>
/// <param name="Tile">The server's parse of the tile.</param>
/// <param name="FileBytes">The size of the file.</param>
/// <param name="HeaderPolyCount">The Detour header's polygon count (file offset 44), off-mesh polygons included.</param>
/// <param name="HeaderBvNodeCount">The Detour header's BV node count (file offset 68).</param>
/// <param name="HeaderOffMeshCount">The Detour header's off-mesh connection count (file offset 72).</param>
/// <param name="UsesLiquids">The generator header's uses-liquids flag.</param>
public sealed record NavTileData(NavMeshTile Tile, int FileBytes, int HeaderPolyCount, int HeaderBvNodeCount, int HeaderOffMeshCount, bool UsesLiquids);

/// <summary>The navmesh parameters of a map (<c>NNN.mmap</c>).</summary>
/// <param name="Parameters">The parsed <see cref="NavMeshParams"/>.</param>
public sealed record NavParamsData(NavMeshParams Parameters);

/// <summary>Bounds of the parsed-file caches of a <see cref="ServerData"/>, in bytes of file read.</summary>
/// <param name="TerrainBytes">Parsed <c>.map</c> files.</param>
/// <param name="NavBytes">Parsed <c>.mmtile</c> files.</param>
/// <param name="VMapTileBytes">Parsed <c>.vmtile</c> and <c>.vmtree</c> files.</param>
/// <param name="ModelBytes">Parsed <c>.vmo</c> model files.</param>
public sealed record ServerDataLimits(long TerrainBytes = 192L << 20, long NavBytes = 256L << 20, long VMapTileBytes = 32L << 20, long ModelBytes = 384L << 20);

/// <summary>
/// Reads the extracted server data (<c>maps</c>, <c>vmaps</c>, <c>mmaps</c>) with the ArcaneCore.Game readers and keeps the
/// parsed files in bounded caches. Every method may be called from any thread.
/// <para>
/// Why the readers are safe off the server's owner thread: each one is a static function over a byte array that allocates its
/// result and touches no shared mutable state. <see cref="TerrainTile.Parse"/> returns an object that is immutable after parsing
/// (TerrainTile.cs, class remarks). <see cref="VMapTree.ParseTile"/> and <see cref="VMapTree.Parse"/> build locals only
/// (VMapTree.cs lines 44-76). <see cref="WorldModel.Parse"/> builds locals only (WorldModel.cs lines 391-413) and the
/// model, group and instance types expose read-only data. <see cref="NavMeshTile.ParseFile"/> takes a process-unique id with
/// <see cref="Interlocked"/> (NavMeshTile.cs line 71) and publishes its lazily built caches whole, since searches already run on several
/// threads (NavMeshTile.cs lines 85-86). None of these objects is ever handed to the server's engine on the owner thread: the engine
/// reads the files again into its own managers, so the two sides share nothing.
/// </para>
/// </summary>
public sealed class ServerData
{
    private readonly ParseCache<TileKey, TerrainTile> _terrain;
    private readonly ParseCache<TileKey, NavTileData> _nav;
    private readonly ParseCache<TileKey, VMapTileData> _vmapTiles;
    private readonly ParseCache<uint, VMapTreeData> _vmapTrees;
    private readonly ParseCache<string, WorldModel> _models;
    private readonly ParseCache<uint, NavParamsData> _navParams;

    /// <summary>Opens the data directories named by <paramref name="paths"/> (nothing is read yet).</summary>
    public ServerData(AtlasPaths paths, ServerDataLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        limits ??= new ServerDataLimits();
        Paths = paths;
        _terrain = new(limits.TerrainBytes);
        _nav = new(limits.NavBytes);
        _vmapTiles = new(limits.VMapTileBytes);
        _vmapTrees = new(limits.VMapTileBytes);
        _models = new(limits.ModelBytes);
        _navParams = new(1L << 20);
    }

    /// <summary>The configured paths.</summary>
    public AtlasPaths Paths { get; }

    /// <summary>The <c>maps</c> directory, or null when absent.</summary>
    public string? MapsDirectory => Paths.MapsDirectory;

    /// <summary>The <c>vmaps</c> directory, or null when absent.</summary>
    public string? VMapsDirectory => Paths.VMapsDirectory;

    /// <summary>The <c>mmaps</c> directory, or null when absent.</summary>
    public string? MMapsDirectory => Paths.MMapsDirectory;

    /// <summary>Counters of the caches, for status reports.</summary>
    public IReadOnlyDictionary<string, ParseCacheStats> CacheStats => new SortedDictionary<string, ParseCacheStats>(StringComparer.Ordinal)
    {
        ["terrain"] = _terrain.Stats,
        ["navTiles"] = _nav.Stats,
        ["vmapTiles"] = _vmapTiles.Stats,
        ["vmapTrees"] = _vmapTrees.Stats,
        ["models"] = _models.Stats,
        ["navParams"] = _navParams.Stats,
    };

    /// <summary>One terrain tile (<c>MMMXXYY.map</c>) parsed by <see cref="TerrainTile.Parse"/>.</summary>
    public ParsedFile<TerrainTile> TerrainFile(TileKey tile)
    {
        if (!tile.IsInGrid)
        {
            return ParsedFile<TerrainTile>.Missing($"{tile} is not a terrain tile");
        }

        return _terrain.GetOrLoad(tile, () => Load(MapsDirectory, TerrainTile.FileName(tile.MapId, tile.X, tile.Y), static bytes => TerrainTile.Parse(bytes)));
    }

    /// <summary>One navmesh tile (<c>MMMXXYY.mmtile</c>) parsed by <see cref="NavMeshTile.ParseFile"/>.</summary>
    public ParsedFile<NavTileData> NavFile(TileKey tile)
    {
        if (!tile.IsInGrid)
        {
            return ParsedFile<NavTileData>.Missing($"{tile} is not a terrain tile");
        }

        return _nav.GetOrLoad(tile, () => Load(MMapsDirectory, NavMeshFormat.TileFileName(tile.MapId, tile.X, tile.Y), ParseNavTile));
    }

    /// <summary>The model spawns of one terrain tile (<c>NNN_YY_XX.vmtile</c>) parsed by <see cref="VMapTree.ParseTile"/>.</summary>
    public ParsedFile<VMapTileData> VMapTileFile(TileKey tile)
    {
        if (!tile.IsInGrid)
        {
            return ParsedFile<VMapTileData>.Missing($"{tile} is not a terrain tile");
        }

        return _vmapTiles.GetOrLoad(tile, () => Load(VMapsDirectory, VMapFormat.TileFileName(tile.MapId, tile.X, tile.Y), static bytes =>
            new VMapTileData([.. VMapTree.ParseTile(bytes).Select(s => new VMapSpawn(s.Spawn, s.Slot))])));
    }

    /// <summary>A map's <c>NNN.vmtree</c> parsed by <see cref="VMapTree.Parse"/>.</summary>
    public ParsedFile<VMapTreeData> VMapTreeFile(uint mapId)
        => _vmapTrees.GetOrLoad(mapId, () => Load(VMapsDirectory, VMapFormat.TreeFileName(mapId), bytes =>
        {
            (VMapTree tree, List<(ModelSpawn Spawn, uint Slot)> global) = VMapTree.Parse(mapId, bytes);
            return new VMapTreeData(tree.IsTiled, tree.Tree.PrimitiveCount, [.. global.Select(s => new VMapSpawn(s.Spawn, s.Slot))]);
        }));

    /// <summary>A model file (<c>*.vmo</c>) named by a spawn, parsed by <see cref="WorldModel.Parse"/>.</summary>
    public ParsedFile<WorldModel> ModelFile(string spawnName)
    {
        ArgumentNullException.ThrowIfNull(spawnName);
        if (!VMapFormat.IsSafeModelName(spawnName))
        {
            return ParsedFile<WorldModel>.Rejected($"model name '{spawnName}' is not a plain file name");
        }

        return _models.GetOrLoad(spawnName, () => Load(VMapsDirectory, VMapFormat.ModelFileName(spawnName), static bytes => WorldModel.Parse(bytes)));
    }

    /// <summary>A map's navmesh parameters (<c>NNN.mmap</c>).</summary>
    public ParsedFile<NavParamsData> NavParamsFile(uint mapId)
        => _navParams.GetOrLoad(mapId, () => Load(MMapsDirectory, NavMeshFormat.ParamsFileName(mapId), static bytes => new NavParamsData(NavMeshParams.Parse(bytes))));

    /// <summary>The cached terrain parse of a tile when one exists; never reads.</summary>
    public bool TryPeekTerrain(TileKey tile, out ParsedFile<TerrainTile> file) => _terrain.TryPeek(tile, out file);

    /// <summary>The cached navmesh parse of a tile when one exists; never reads.</summary>
    public bool TryPeekNav(TileKey tile, out ParsedFile<NavTileData> file) => _nav.TryPeek(tile, out file);

    /// <summary>The cached vmap tile parse when one exists; never reads.</summary>
    public bool TryPeekVMapTile(TileKey tile, out ParsedFile<VMapTileData> file) => _vmapTiles.TryPeek(tile, out file);

    /// <summary>Whether the tile's <c>.map</c> file exists (a directory lookup, no read).</summary>
    public bool TerrainFileExists(TileKey tile)
        => tile.IsInGrid && MapsDirectory is { } dir && File.Exists(Path.Combine(dir, TerrainTile.FileName(tile.MapId, tile.X, tile.Y)));

    /// <summary>Whether the tile's <c>.vmtile</c> file exists (a directory lookup, no read).</summary>
    public bool VMapTileFileExists(TileKey tile)
        => tile.IsInGrid && VMapsDirectory is { } dir && File.Exists(Path.Combine(dir, VMapFormat.TileFileName(tile.MapId, tile.X, tile.Y)));

    /// <summary>Whether the tile's <c>.mmtile</c> file exists (a directory lookup, no read).</summary>
    public bool NavFileExists(TileKey tile)
        => tile.IsInGrid && MMapsDirectory is { } dir && File.Exists(Path.Combine(dir, NavMeshFormat.TileFileName(tile.MapId, tile.X, tile.Y)));

    /// <summary>Whether the map's <c>.vmtree</c> file exists.</summary>
    public bool VMapTreeFileExists(uint mapId)
        => VMapsDirectory is { } dir && File.Exists(Path.Combine(dir, VMapFormat.TreeFileName(mapId)));

    /// <summary>Whether the map has a <c>.mmap</c> parameter file.</summary>
    public bool NavParamsFileExists(uint mapId)
        => MMapsDirectory is { } dir && File.Exists(Path.Combine(dir, NavMeshFormat.ParamsFileName(mapId)));

    /// <summary>Whether any of the map's data files exists (one terrain tile, the vmap tree, a vmap tile or the navmesh parameters).</summary>
    public bool MapHasAnyData(uint mapId)
        => VMapTreeFileExists(mapId) || NavParamsFileExists(mapId) || TerrainTiles(mapId).Count > 0 || VMapTileKeys(mapId).Count > 0 || NavTiles(mapId).Count > 0;

    /// <summary>The tiles that have a <c>.map</c> file, from the directory listing alone.</summary>
    public IReadOnlyCollection<TileKey> TerrainTiles(uint mapId) => List(MapsDirectory, mapId, ".map", yFirst: false);

    /// <summary>The tiles that have an <c>.mmtile</c> file, from the directory listing alone.</summary>
    public IReadOnlyCollection<TileKey> NavTiles(uint mapId) => List(MMapsDirectory, mapId, ".mmtile", yFirst: false);

    /// <summary>The tiles that have a <c>.vmtile</c> file, from the directory listing alone (no global tile).</summary>
    public IReadOnlyCollection<TileKey> VMapTileKeys(uint mapId) => List(VMapsDirectory, mapId, ".vmtile", yFirst: true);

    /// <summary>The tiles that have a <c>.vmtile</c> file, plus the global tile when the map's tree holds its own spawns.</summary>
    public IReadOnlyCollection<TileKey> VMapTiles(uint mapId)
    {
        IReadOnlyCollection<TileKey> tiles = VMapTileKeys(mapId);
        if (VMapTreeFile(mapId) is { IsOk: true, Value: { IsTiled: false, GlobalSpawns.Count: > 0 } })
        {
            return [.. tiles, TileKey.Global(mapId)];
        }

        return tiles;
    }

    /// <summary>
    /// The terrain tile a Detour tile covers, found from the map's navmesh parameters the way the server's tile loader indexes them:
    /// Detour X comes from world Y and Detour Y from world X (<see cref="NavMeshFormat.ToRecast"/>).
    /// </summary>
    public static TileKey? TerrainTileOfDetour(uint mapId, NavMeshParams parameters, int detourX, int detourY)
    {
        // Centre of the Detour tile in Recast space (y unused), then back to world: (x, y) = (recast z, recast x).
        double recastX = parameters.Origin.X + ((detourX + 0.5) * parameters.TileWidth);
        double recastZ = parameters.Origin.Z + ((detourY + 0.5) * parameters.TileHeight);
        return WorldGrid.TileOf(mapId, recastZ, recastX);
    }

    private static NavTileData ParseNavTile(byte[] bytes)
    {
        NavMeshTile tile = NavMeshTile.ParseFile(bytes);

        // ParseFile succeeded, so the file holds the 20-byte generator header and the 100-byte Detour header.
        // dtMeshHeader order (DetourNavMesh.h:261-289): magic, version, x, y, layer, userId, polyCount (+24), vertCount,
        // maxLinkCount, detailMeshCount, detailVertCount, detailTriCount, bvNodeCount (+48), offMeshConCount (+52).
        const int prefix = MmapTileHeader.ByteSize;
        int polyCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(prefix + 24));
        int bvNodeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(prefix + 48));
        int offMeshCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(prefix + 52));
        bool usesLiquids = (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) & 1) != 0;
        return new NavTileData(tile, bytes.Length, polyCount, bvNodeCount, offMeshCount, usesLiquids);
    }

    private static (ParsedFile<T> File, long Cost) Load<T>(string? directory, string fileName, Func<byte[], T> parse)
        where T : class
    {
        if (directory is null)
        {
            return (ParsedFile<T>.Missing($"{fileName}: the data directory is not configured"), 128);
        }

        string path = Path.Combine(directory, fileName);
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return (ParsedFile<T>.Missing($"{fileName}: no such file"), 128);
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (ParsedFile<T>.Rejected($"{fileName}: unreadable ({e.GetType().Name}: {e.Message})"), 256);
        }

        try
        {
            return (ParsedFile<T>.Ok(parse(bytes), fileName), bytes.Length);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException)
        {
            return (ParsedFile<T>.Rejected($"{fileName}: {e.GetType().Name}: {e.Message}"), 256);
        }
    }

    private static List<TileKey> List(string? directory, uint mapId, string extension, bool yFirst)
    {
        var tiles = new List<TileKey>();
        if (directory is null)
        {
            return tiles;
        }

        // Names are the map id padded to three digits, then the two tile indices: "MMMXXYY.ext", or "MMM_YY_XX.vmtile" for vmaps.
        string prefix = mapId.ToString("D3", CultureInfo.InvariantCulture);
        foreach (string path in Directory.EnumerateFiles(directory, prefix + "*" + extension))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            ReadOnlySpan<char> rest = name.AsSpan(prefix.Length);
            ReadOnlySpan<char> first;
            ReadOnlySpan<char> second;
            if (yFirst)
            {
                if (rest.Length != 6 || rest[0] != '_' || rest[3] != '_')
                {
                    continue;
                }

                first = rest[1..3];
                second = rest[4..6];
            }
            else
            {
                if (rest.Length != 4)
                {
                    continue;
                }

                first = rest[..2];
                second = rest[2..4];
            }

            if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int a)
                || !int.TryParse(second, NumberStyles.None, CultureInfo.InvariantCulture, out int b))
            {
                continue;
            }

            // The vmap name carries tile Y first; the terrain and navmesh names carry tile X first.
            var key = yFirst ? new TileKey(mapId, b, a) : new TileKey(mapId, a, b);
            if (key.IsInGrid)
            {
                tiles.Add(key);
            }
        }

        tiles.Sort(static (l, r) => l.X != r.X ? l.X.CompareTo(r.X) : l.Y.CompareTo(r.Y));
        return tiles;
    }
}
