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
/// <param name="Partial">Tiles written (and counted in <paramref name="Written"/>) with at least one sub-tile missing because its build failed.</param>
public sealed record MmapExtractMapReport(uint Id, int Tiles, int Written, int Existing, int Empty, int Failed, long Polygons, int Partial = 0);

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

    /// <summary>
    /// Test seam: called with the tile file's name after each tile file is written (on whichever thread built it), so a test
    /// can stop a run deterministically between tiles. Null in production.
    /// </summary>
    internal Action<string>? TileWritten { get; set; }

    /// <param name="dataRoot">The server data root holding <c>maps</c> and <c>vmaps</c>.</param>
    /// <param name="mmapsDirectory">
    /// Where the files go; required, as for the maps and vmaps extractors, so no caller writes through an unchecked
    /// <c>&lt;data root&gt;/mmaps</c>. <c>extract.mmaps</c> passes the real folder its check judged for that path.
    /// </param>
    public MmapExtractor(string dataRoot, string mmapsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(mmapsDirectory);
        _dataRoot = dataRoot;
        _mmapsDirectory = mmapsDirectory;
    }

    /// <summary>Builds the navigation meshes.</summary>
    /// <param name="onlyMaps">Map ids to build; null or empty builds every map that has data, except the two unused test maps.</param>
    /// <param name="overwrite">Rebuild tiles whose current file exists; otherwise they are kept.</param>
    /// <param name="quick">Skip removing terrain inside models and model triangles under the terrain (the reference's quick mode).</param>
    /// <param name="threads">Tiles built at once (at least 1).</param>
    /// <param name="onlyTile">
    /// Build this one tile (the server's tile X and Y) of each selected map. The map's parameter file is still written
    /// from all its tiles, so the tile fits a full build. A map whose tiles on disk were built against other parameters
    /// (origin, tile size or tile count) is refused with an <see cref="ArgumentException"/> and nothing of it is written or
    /// deleted, whatever <paramref name="overwrite"/> says: the tiles beside the new tile would stay in the wrong slot, so
    /// that map has to be built whole, without a tile. A refused tile (that refusal, or a map without that tile) ends the whole
    /// run with the exception: maps processed earlier in the same run keep the files written for them, but no report is returned.
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
                try
                {
                    AddTilesOfGlobalModel(mapId, cache, mapTiles);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // One damaged file costs this map, not the run.
                    problems.Add($"map {mapId}: {e.Message}");
                    reports.Add(new MmapExtractMapReport(mapId, 0, 0, 0, 0, 1, 0));
                    continue;
                }
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

            // The requested tile is checked before anything of this map is written or deleted.
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

            // A tile's vertices are world coordinates, but its header's X and Y are its slot counted from the origin of the run
            // that built it, so a tile left from a run with another origin, tile size or tile count sits in the wrong slot of
            // this map's parameter file. When the parameter file is about to change, every tile of the map on disk is therefore
            // deleted BEFORE the new file is written: an interrupted or partial run then leaves fewer tiles, never stale ones that
            // the next run would take for current. One tile cannot be rebuilt alone against changed parameters.
            // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:286-288 and :813-814 (the tile's own X, Y), MapBuilder.cpp:430-489 (the origin)
            string parametersPath = Path.Combine(mmapsDirectory, NavMeshFormat.ParamsFileName(mapId));
            List<string> oldTiles = TileFilesOf(mmapsDirectory, mapId);
            bool parametersChanged = oldTiles.Count > 0 && ParametersDiffer(parametersPath, parameters);
            if (parametersChanged && onlyTile is not null)
            {
                throw new ArgumentException($"Map {mapId}'s navmesh parameters changed since its tiles were built; build the whole map without tile=. Nothing was written for this map.", nameof(onlyTile));
            }

            int deleted = 0;
            if (parametersChanged)
            {
                try
                {
                    foreach (string old in oldTiles)
                    {
                        // File.Delete removes a link itself and never follows it.
                        File.Delete(old);
                        deleted++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The parameter file is still the old one, which the tiles left still match.
                    problems.Add($"map {mapId}: the existing tiles built against the old {NavMeshFormat.ParamsFileName(mapId)} could not all be deleted ({deleted} of {oldTiles.Count} were); nothing else was written for this map: {e.Message}");
                    reports.Add(new MmapExtractMapReport(mapId, mapTiles.Count, 0, 0, 0, 1, 0));
                    continue;
                }
            }

            bytes += ExtractOutput.WriteWhole(parametersPath, parameters);

            int written = 0;
            int existing = 0;
            int empty = 0;
            int failed = 0;
            int partial = 0;
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

                // A tile that is rebuilt and comes out empty or failed must not leave its earlier file behind.
                void DropOldFile()
                {
                    try
                    {
                        File.Delete(target);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        problems.Add($"{name}: the earlier tile file could not be deleted: {e.Message}");
                    }
                }

                try
                {
                    MmapTileBuilder.Result result = BuildTile(mapId, tileX, tileY, mapsDirectory, cache, origin, quick, problems, name, cancellationToken);
                    if (result.FailedSubTiles > 0)
                    {
                        string cause = result.FirstFailure is null ? string.Empty : $" (first: {result.FirstFailure})";
                        problems.Add(result.UnexpectedFailure
                            ? $"{name}: {result.FailedSubTiles} of 625 sub-tiles failed with an unexpected error in the generator's own steps, not a Recast refusal{cause}; they are missing from the tile."
                            : $"{name}: Recast gave up on {result.FailedSubTiles} of 625 sub-tiles{cause}; they are missing from the tile.");
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

                        DropOldFile();
                        return;
                    }

                    Interlocked.Add(ref tileBytes, ExtractOutput.WriteWhole(target, result.File));
                    Interlocked.Add(ref polygons, result.Polygons);
                    Interlocked.Increment(ref written);
                    if (result.FailedSubTiles > 0)
                    {
                        Interlocked.Increment(ref partial);
                    }

                    TileWritten?.Invoke(name);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref failed);
                    problems.Add($"{name}: {e.Message}");
                    DropOldFile();
                }
            });

            if (parametersChanged)
            {
                problems.Add($"map {mapId}: {NavMeshFormat.ParamsFileName(mapId)} differs from the one the existing tiles were built with (origin, tile size or tile count); {deleted} existing tile file(s) were deleted and {written} of the map's {mapTiles.Count} tile(s) rebuilt.");
            }

            bytes += tileBytes;
            reports.Add(new MmapExtractMapReport(mapId, mapTiles.Count, written, existing, empty, failed, polygons, partial));
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

    /// <summary>
    /// The tile files of one map in the folder: only names that <see cref="NavMeshFormat.TileFileName"/> produces for the map
    /// (compared without regard to case, as a Windows or macOS file system resolves them), directly in the folder (nothing below it, no folders).
    /// </summary>
    private static List<string> TileFilesOf(string directory, uint mapId)
    {
        string prefix = mapId.ToString("D3", CultureInfo.InvariantCulture);
        var files = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory, prefix + "*.mmtile"))
        {
            string name = Path.GetFileName(file);
            if (name.Length == prefix.Length + 4 + ".mmtile".Length
                && int.TryParse(name.AsSpan(prefix.Length, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(name.AsSpan(prefix.Length + 2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int y)
                && string.Equals(name, NavMeshFormat.TileFileName(mapId, x, y), StringComparison.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }

        return files;
    }

    /// <summary>Whether the parameter file on disk is missing or is not the bytes about to be written.</summary>
    private static bool ParametersDiffer(string path, byte[] parameters)
    {
        try
        {
            return !File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(parameters);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
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
}
