using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Core.Numerics;

namespace ArcaneCore.Atlas.Extract;

/// <summary>What happened to one map during a <see cref="MmapExtractor"/> run.</summary>
/// <param name="Id">The map id.</param>
/// <param name="Tiles">Tiles the map has terrain or models for.</param>
/// <param name="Written">Tile files written in this run.</param>
/// <param name="Existing">Tiles left alone because a current tile file was already there.</param>
/// <param name="Empty">Tiles with nothing walkable (no file is written for them).</param>
/// <param name="Failed">Tiles that could not be built.</param>
/// <param name="Polygons">Polygons in the tiles written.</param>
public sealed record MmapExtractMapReport(uint Id, int Tiles, int Written, int Existing, int Empty, int Failed, long Polygons);

/// <summary>The outcome of a <see cref="MmapExtractor"/> run.</summary>
/// <param name="MmapsDirectory">The folder the files are in.</param>
/// <param name="Maps">Per map, in map id order.</param>
/// <param name="Bytes">Bytes written.</param>
/// <param name="Problems">Tiles that failed, sub-tiles Recast gave up on and model files that were missing, with the reason.</param>
public sealed record MmapExtractReport(string MmapsDirectory, IReadOnlyList<MmapExtractMapReport> Maps, long Bytes, IReadOnlyList<string> Problems);

/// <summary>
/// Writes the server's navigation meshes (<c>mmaps/NNN.mmap</c> and <c>NNNXXYY.mmtile</c>) from the server's own
/// terrain and collision data (<c>maps/</c> and <c>vmaps/</c> of the same data root), as the reference generator does:
/// one Detour tile per terrain tile, built by <see cref="MmapTileBuilder"/> from the triangles <see cref="MmapTerrain"/>
/// and <see cref="MmapModels"/> collect. The files are mmap version 6 over Detour version 7 and are read by the world
/// server's <see cref="NavMeshPathfinder"/>. Tiles are independent, so they are built in parallel.
/// </summary>
/// <remarks>
/// Not written: the transports' own meshes (<c>goNNNN.mmtile</c>), off-mesh connections, and parameter overrides from a
/// <c>config.json</c>. The polygons are Recast's, so they are not the reference tool's polygon for polygon; the
/// parameters, the triangle marking and the filters are.
/// </remarks>
// ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:152-507 and TileWorker.cpp:355-428
public sealed class MmapExtractor
{
    /// <summary>The folders of a data root.</summary>
    public const string MapsFolder = "maps";
    public const string VmapsFolder = "vmaps";
    public const string MmapsFolder = "mmaps";

    // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:521-531 (maps left out of a whole-world run: Colin.wdt and the unused Emerald Dream)
    private static readonly uint[] JunkMaps = [42, 169];

    private readonly string _dataRoot;
    private readonly string _mmapsDirectory;

    /// <param name="dataRoot">The server data root holding <c>maps</c> and <c>vmaps</c>.</param>
    /// <param name="mmapsDirectory">Where the files go; null for <c>&lt;data root&gt;/mmaps</c>.</param>
    public MmapExtractor(string dataRoot, string? mmapsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = dataRoot;
        _mmapsDirectory = mmapsDirectory ?? Path.Combine(dataRoot, MmapsFolder);
    }

    /// <summary>Builds the navigation meshes.</summary>
    /// <param name="onlyMaps">Map ids to build; null or empty builds every map that has data, except the two unused test maps.</param>
    /// <param name="overwrite">Rebuild tiles whose current file exists; otherwise they are kept.</param>
    /// <param name="quick">Skip removing terrain inside models and model triangles under the terrain (the reference's quick mode).</param>
    /// <param name="threads">Tiles built at once (at least 1).</param>
    /// <param name="onlyTile">
    /// Build this one tile (the server's tile X and Y) of each selected map. The map's parameter file is still written
    /// from all its tiles, so the tile fits a full build.
    /// </param>
    public MmapExtractReport Extract(IReadOnlyCollection<uint>? onlyMaps, bool overwrite, bool quick, int threads, CancellationToken cancellationToken, (int X, int Y)? onlyTile = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        string mapsDirectory = Path.Combine(_dataRoot, MapsFolder);
        string vmapsDirectory = Path.Combine(_dataRoot, VmapsFolder);
        string mmapsDirectory = _mmapsDirectory;
        Directory.CreateDirectory(mmapsDirectory);

        SortedDictionary<uint, SortedSet<uint>> tiles = DiscoverTiles(mapsDirectory, vmapsDirectory);
        var cache = new MmapModelCache(vmapsDirectory);
        var problems = new ConcurrentBag<string>();
        var reports = new List<MmapExtractMapReport>();
        long bytes = 0;
        IEnumerable<uint> selected = onlyMaps is { Count: > 0 }
            ? onlyMaps.Distinct().Order()
            : tiles.Keys.Where(id => !JunkMaps.Contains(id));
        foreach (uint mapId in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tiles.TryGetValue(mapId, out SortedSet<uint>? mapTiles))
            {
                throw new ArgumentException($"Map {mapId} has neither maps nor vmaps in '{_dataRoot}'.", nameof(onlyMaps));
            }

            if (mapTiles.Count == 0)
            {
                // A map without terrain tiles is one building: its tiles are the ones the building's box covers.
                AddTilesOfGlobalModel(mapId, cache, mapTiles);
            }

            if (mapTiles.Count == 0)
            {
                reports.Add(new MmapExtractMapReport(mapId, 0, 0, 0, 0, 0, 0));
                continue;
            }

            // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:430-489 (buildNavMesh: the origin is the low corner of the tile with the highest indices)
            int tileXMax = mapTiles.Max(t => (int)(t >> 16));
            int tileYMax = mapTiles.Max(t => (int)(t & 0xFFFF));
            var origin = new RcVec3f(((32 - tileXMax) * MmapTerrain.GridSize) - MmapTerrain.GridSize, 1.17549435E-38f, ((32 - tileYMax) * MmapTerrain.GridSize) - MmapTerrain.GridSize);
            var parameters = new byte[NavMeshParams.Size];
            BinaryPrimitives.WriteSingleLittleEndian(parameters, origin.X);
            BinaryPrimitives.WriteSingleLittleEndian(parameters.AsSpan(4), origin.Y);
            BinaryPrimitives.WriteSingleLittleEndian(parameters.AsSpan(8), origin.Z);
            BinaryPrimitives.WriteSingleLittleEndian(parameters.AsSpan(12), MmapTerrain.GridSize);
            BinaryPrimitives.WriteSingleLittleEndian(parameters.AsSpan(16), MmapTerrain.GridSize);
            BinaryPrimitives.WriteInt32LittleEndian(parameters.AsSpan(20), mapTiles.Count);
            bytes += WriteWhole(Path.Combine(mmapsDirectory, NavMeshFormat.ParamsFileName(mapId)), parameters);

            // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:319-370 (buildSingleTile)
            IEnumerable<uint> building = mapTiles;
            if (onlyTile is { } single)
            {
                uint packedSingle = ((uint)single.Y << 16) | (uint)single.X;
                if (single.X is < 0 or > 63 || single.Y is < 0 or > 63 || !mapTiles.Contains(packedSingle))
                {
                    throw new ArgumentException($"Map {mapId} has no tile ({single.X}, {single.Y}).", nameof(onlyTile));
                }

                building = [packedSingle];
            }

            int written = 0;
            int existing = 0;
            int empty = 0;
            int failed = 0;
            long polygons = 0;
            long tileBytes = 0;
            Parallel.ForEach(building, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken }, packed =>
            {
                int tileX = (int)(packed >> 16);
                int tileY = (int)(packed & 0xFFFF);

                // The file is named like the terrain file: map, then the server's tile X (the generator's Y) and Y.
                string name = NavMeshFormat.TileFileName(mapId, tileY, tileX);
                string target = Path.Combine(mmapsDirectory, name);
                if (!overwrite && IsCurrentTile(target))
                {
                    Interlocked.Increment(ref existing);
                    return;
                }

                try
                {
                    MmapTileBuilder.Result result = BuildTile(mapId, tileX, tileY, mapsDirectory, cache, origin, quick, problems, name, cancellationToken);
                    if (result.FailedSubTiles > 0)
                    {
                        problems.Add($"{name}: Recast gave up on {result.FailedSubTiles} of 625 sub-tiles; they are missing from the tile.");
                    }

                    if (result.File is null)
                    {
                        if (result.Problem is null)
                        {
                            Interlocked.Increment(ref empty);
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                            problems.Add($"{name}: {result.Problem}");
                        }

                        return;
                    }

                    Interlocked.Add(ref tileBytes, WriteWhole(target, result.File));
                    Interlocked.Add(ref polygons, result.Polygons);
                    Interlocked.Increment(ref written);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref failed);
                    problems.Add($"{name}: {e.Message}");
                }
            });

            bytes += tileBytes;
            reports.Add(new MmapExtractMapReport(mapId, mapTiles.Count, written, existing, empty, failed, polygons));
        }

        return new MmapExtractReport(mmapsDirectory, reports, bytes, [.. problems.Distinct().Order(StringComparer.Ordinal)]);
    }

    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:386-428 (buildTile)
    private static MmapTileBuilder.Result BuildTile(uint mapId, int tileX, int tileY, string mapsDirectory, MmapModelCache cache, RcVec3f origin, bool quick, ConcurrentBag<string> problems, string name, CancellationToken cancellationToken)
    {
        var mesh = new MmapMeshData();
        var terrain = new MmapTerrain(mapsDirectory, skipLiquid: false);
        terrain.Load(mapId, tileX, tileY, mesh);
        MmapTerrain.CleanVertices(mesh.SolidVerts, mesh.SolidTris);
        MmapTerrain.CleanVertices(mesh.LiquidVerts, mesh.LiquidTris);
        var models = new MmapModels(cache, quick);
        models.Load(mapId, tileX, tileY, mesh);
        foreach (string missing in models.MissingModels.Distinct(StringComparer.Ordinal))
        {
            problems.Add($"{name}: model file '{missing.TrimEnd('\0')}' is not in vmaps; the model is left out.");
        }

        if (mesh.SolidVerts.Count == 0 && mesh.LiquidVerts.Count == 0)
        {
            return new MmapTileBuilder.Result(null, 0, 0, null);
        }

        // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:491-507 (getTileBounds: the tile's square, with the height range of its vertices)
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        foreach (List<float> verts in (List<float>[])[mesh.LiquidVerts, mesh.SolidVerts])
        {
            for (int i = 1; i < verts.Count; i += 3)
            {
                minY = MathF.Min(minY, verts[i]);
                maxY = MathF.Max(maxY, verts[i]);
            }
        }

        var bmax = new RcVec3f((32 - tileX) * MmapTerrain.GridSize, maxY, (32 - tileY) * MmapTerrain.GridSize);
        var bmin = new RcVec3f(bmax.X - MmapTerrain.GridSize, minY, bmax.Z - MmapTerrain.GridSize);

        // A Recast-space point (world y, z, x) is under the map when it is below the terrain and under a model.
        // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:1197-1205 (IsUnderMap)
        Func<float, float, float, bool>? isUnderMap = quick
            ? null
            : (y, z, x) => !(z + 0.2f > terrain.GetHeight(x, y)) && models.IsUnderModel(x, y, z + 0.2f);
        return MmapTileBuilder.Build(mapId, mesh, bmin, bmax, origin, isUnderMap, usesLiquids: true, cancellationToken);
    }

    /// <summary>
    /// The tiles each map has data for, as the generator's packed (X, Y): from the terrain files and the model tile files.
    /// The generator's X is the server's tile Y.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:152-217 (discoverTiles)
    private static SortedDictionary<uint, SortedSet<uint>> DiscoverTiles(string mapsDirectory, string vmapsDirectory)
    {
        var tiles = new SortedDictionary<uint, SortedSet<uint>>();
        SortedSet<uint> Of(uint mapId)
        {
            if (!tiles.TryGetValue(mapId, out SortedSet<uint>? set))
            {
                tiles[mapId] = set = [];
            }

            return set;
        }

        static bool Digits(string text, int start, int count, out uint value)
            => uint.TryParse(text.AsSpan(start, count), NumberStyles.None, CultureInfo.InvariantCulture, out value);

        if (Directory.Exists(mapsDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(mapsDirectory, "*.map"))
            {
                // MMMXXYY.map: the server's tile X, then Y.
                string name = Path.GetFileName(file);
                if (name.Length == 11 && Digits(name, 0, 3, out uint mapId) && Digits(name, 3, 2, out uint serverX) && Digits(name, 5, 2, out uint serverY))
                {
                    Of(mapId).Add((serverY << 16) | serverX);
                }
            }
        }

        if (Directory.Exists(vmapsDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(vmapsDirectory, "*.vmtree"))
            {
                string name = Path.GetFileName(file);
                if (name.Length == 10 && Digits(name, 0, 3, out uint mapId))
                {
                    Of(mapId);
                }
            }

            foreach (string file in Directory.EnumerateFiles(vmapsDirectory, "*.vmtile"))
            {
                // MMM_YY_XX.vmtile: the server's tile Y, then X.
                string name = Path.GetFileName(file);
                if (name.Length == 16 && Digits(name, 0, 3, out uint mapId) && Digits(name, 4, 2, out uint serverY) && Digits(name, 7, 2, out uint serverX))
                {
                    Of(mapId).Add((serverY << 16) | serverX);
                }
            }
        }

        return tiles;
    }

    // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:275-317 (getGridBounds) and :376-388
    private static void AddTilesOfGlobalModel(uint mapId, MmapModelCache cache, SortedSet<uint> tiles)
    {
        var mesh = new MmapMeshData();
        if (!new MmapModels(cache, quick: true).Load(mapId, 64, 64, mesh) || mesh.SolidVerts.Count + mesh.LiquidVerts.Count == 0)
        {
            return;
        }

        float minX = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxZ = float.MinValue;
        foreach (List<float> verts in (List<float>[])[mesh.SolidVerts, mesh.LiquidVerts])
        {
            for (int i = 0; i < verts.Count; i += 3)
            {
                minX = MathF.Min(minX, verts[i]);
                maxX = MathF.Max(maxX, verts[i]);
                minZ = MathF.Min(minZ, verts[i + 2]);
                maxZ = MathF.Max(maxZ, verts[i + 2]);
            }
        }

        // Coordinates to tile indices, cut towards zero as the reference's conversion to an unsigned index does.
        int tileXMax = (int)(32 - (minX / MmapTerrain.GridSize));
        int tileYMax = (int)(32 - (minZ / MmapTerrain.GridSize));
        int tileXMin = (int)(32 - (maxX / MmapTerrain.GridSize));
        int tileYMin = (int)(32 - (maxZ / MmapTerrain.GridSize));
        for (int x = Math.Max(0, tileXMin); x <= Math.Min(63, tileXMax); x++)
        {
            for (int y = Math.Max(0, tileYMin); y <= Math.Min(63, tileYMax); y++)
            {
                tiles.Add(((uint)x << 16) | (uint)y);
            }
        }
    }

    /// <summary>Whether a file is a tile of the current format versions (such a tile is kept unless overwriting).</summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:355-384 (shouldSkipTile)
    private static bool IsCurrentTile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[20];
        using FileStream stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(header) == NavMeshFormat.MmapMagic
            && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == NavMeshFormat.DetourVersion
            && BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) == NavMeshFormat.MmapVersion;
    }

    private static long WriteWhole(string target, byte[] bytes)
    {
        string staging = target + ".part";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, target, overwrite: true);
        return bytes.Length;
    }
}
