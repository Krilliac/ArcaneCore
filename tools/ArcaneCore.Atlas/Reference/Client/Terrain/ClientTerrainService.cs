using ArcaneCore.Atlas.Core;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>The outcome of loading a map's WDT.</summary>
/// <param name="State"><see cref="TileState.Ok"/>, <see cref="TileState.Missing"/> or <see cref="TileState.Rejected"/>.</param>
/// <param name="Detail">Why the state is not Ok.</param>
/// <param name="File">The parsed WDT when <paramref name="State"/> is Ok.</param>
/// <param name="Map">The Map.dbc row that named the folder, when known.</param>
/// <param name="Path">The in-archive path that was read, when known.</param>
public sealed record WdtLoad(TileState State, string? Detail, WdtFile? File, ClientMap? Map, string? Path);

/// <summary>The outcome of loading one tile's ADT.</summary>
/// <param name="State"><see cref="TileState.Ok"/>, <see cref="TileState.Missing"/> or <see cref="TileState.Rejected"/>.</param>
/// <param name="Detail">Why the state is not Ok.</param>
/// <param name="File">The parsed ADT when <paramref name="State"/> is Ok.</param>
/// <param name="Path">The in-archive path that was read, when known.</param>
/// <param name="Archive">The archive that supplied the file, when known.</param>
public sealed record AdtLoad(TileState State, string? Detail, AdtFile? File, string? Path, string? Archive);

/// <summary>
/// Surface normal, slope, walkability and liquid depth sampled at a world position from the client's 1.12.1 ADT.
/// </summary>
/// <param name="Tile">The server tile containing the world position.</param>
/// <param name="Terrain">The underlying height and triangle sample.</param>
/// <param name="Normal">Unit upward surface normal of the MCVT triangle, or <c>(0, 0, 0)</c> at a hole.</param>
/// <param name="SlopeDegrees">Angle between the triangle normal and world up (+Z) in degrees, or <c>NaN</c> at a hole.</param>
/// <param name="IsSteep">True when <paramref name="SlopeDegrees"/> is at least 60 degrees.</param>
/// <param name="WalkableSlope">True when the square is not a hole and <paramref name="SlopeDegrees"/> is less than 60 degrees.</param>
/// <param name="LiquidZ">Highest visible liquid surface height at the position, or <c>null</c> when no liquid is visible.</param>
/// <param name="LiquidDepth">Clamped liquid depth <c>max(0, liquidZ - terrainZ)</c>, or <c>0</c> when dry or at a hole.</param>
public readonly record struct TerrainSurfaceSample(
    TileKey Tile,
    TerrainSample Terrain,
    WorldPoint Normal,
    double SlopeDegrees,
    bool IsSteep,
    bool WalkableSlope,
    double? LiquidZ,
    double LiquidDepth);

/// <summary>Steepness summary for one MCNK chunk inside an analyzed ADT tile.</summary>
/// <param name="ChunkIndex">MCNK index 0..255 (<c>Row * 16 + Column</c>).</param>
/// <param name="ChunkRow">Chunk row 0..15.</param>
/// <param name="ChunkColumn">Chunk column 0..15.</param>
/// <param name="SteepTriangles">Number of triangles in the chunk with slope at least 60 degrees.</param>
/// <param name="TotalTriangles">Total non-hole triangles in the chunk.</param>
/// <param name="MaxSlopeDegrees">Maximum triangle slope angle in degrees within the chunk.</param>
/// <param name="MinZ">Minimum vertex Z in the chunk.</param>
/// <param name="MaxZ">Maximum vertex Z in the chunk.</param>
public readonly record struct TerrainChunkSteepness(
    int ChunkIndex,
    int ChunkRow,
    int ChunkColumn,
    int SteepTriangles,
    int TotalTriangles,
    double MaxSlopeDegrees,
    double MinZ,
    double MaxZ);

/// <summary>Tile-wide elevation, slope, hole, liquid coverage, and area analysis for one client ADT.</summary>
/// <param name="Tile">The analyzed tile key.</param>
/// <param name="MinElevation">Minimum terrain sample height across drawn squares, or <c>NaN</c> if all squares are holes.</param>
/// <param name="MaxElevation">Maximum terrain sample height across drawn squares, or <c>NaN</c> if all squares are holes.</param>
/// <param name="MeanElevation">Arithmetic mean of triangle centroid elevations across drawn triangles, or <c>NaN</c> if none.</param>
/// <param name="TotalTriangles">Total drawn terrain triangles (4 per non-hole square).</param>
/// <param name="SteepTriangles">Number of drawn triangles with slope at least 60 degrees.</param>
/// <param name="SteepTriangleRatio">Ratio <c>SteepTriangles / TotalTriangles</c> (0 when <paramref name="TotalTriangles"/> is 0).</param>
/// <param name="HoleSquares">Total squares cut out by MCNK hole bitmaps (out of 16384).</param>
/// <param name="LiquidSquares">Number of squares (out of 16384) covered by at least one visible liquid layer.</param>
/// <param name="LiquidCoverageRatio">Ratio <c>LiquidSquares / 16384.0</c>.</param>
/// <param name="AreaIds">Sorted distinct <c>AreaId</c> values present across the tile's 256 chunks.</param>
/// <param name="SteepestChunks">Top chunks ordered by <c>SteepTriangles</c> descending, then <c>MaxSlopeDegrees</c> descending.</param>
public sealed record TerrainTileAnalysis(
    TileKey Tile,
    double MinElevation,
    double MaxElevation,
    double MeanElevation,
    int TotalTriangles,
    int SteepTriangles,
    double SteepTriangleRatio,
    int HoleSquares,
    int LiquidSquares,
    double LiquidCoverageRatio,
    IReadOnlyList<uint> AreaIds,
    IReadOnlyList<TerrainChunkSteepness> SteepestChunks);

/// <summary>
/// Finds, reads and caches the client's WDT and ADT files for the reference terrain layers, the placement source and
/// the commands. The client file source and the map directory are looked up on every call (the foundation module that
/// registers them may run after this service is created), and every cached result remembers which file source produced
/// it. Thread-safe; parsed files are immutable.
/// </summary>
public sealed class ClientTerrainService
{
    /// <summary>How many parsed ADTs are kept (about half a megabyte each).</summary>
    public const int DefaultCacheCapacity = 64;

    // A WDT is a few tens of kilobytes and a client has about 25 maps.
    private const int MaxCachedWdts = 64;

    private readonly Func<IClientFileSource?> _files;
    private readonly Func<IMapDirectory?> _maps;
    private readonly ILogger _log;
    // Only parsed files stay cached: an absent or refused file is looked up again next time, so a file that appears
    // (an extraction finishing) or is repaired is found without restarting Atlas.
    private readonly LoadCache<(IClientFileSource Files, uint MapId), WdtLoad> _wdts = new(MaxCachedWdts);
    private readonly LoadCache<(IClientFileSource Files, TileKey Tile), AdtLoad> _adts;

    /// <summary>Creates the service over lazily resolved dependencies.</summary>
    /// <param name="files">Returns the client file source, or null when none is registered.</param>
    /// <param name="maps">Returns the map directory, or null when none is registered.</param>
    /// <param name="log">Logger; refusals are logged at Warning or below.</param>
    /// <param name="cacheCapacity">Parsed ADTs to keep.</param>
    public ClientTerrainService(Func<IClientFileSource?> files, Func<IMapDirectory?> maps, ILogger log, int cacheCapacity = DefaultCacheCapacity)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheCapacity, 1);
        _adts = new LoadCache<(IClientFileSource Files, TileKey Tile), AdtLoad>(cacheCapacity);
    }

    /// <summary>The registered client file source, or null.</summary>
    public IClientFileSource? Files => _files();

    /// <summary>The registered map directory, or null.</summary>
    public IMapDirectory? Maps => _maps();

    /// <summary>AreaTable.dbc name for an area id when a map directory is registered and knows it.</summary>
    public string? AreaName(uint areaId) => _maps()?.AreaName(areaId);

    /// <summary>Drops every cached WDT and ADT so the next request re-reads the files.</summary>
    public void Clear()
    {
        _wdts.Clear();
        _adts.Clear();
    }

    /// <summary>Loads (or returns the cached) WDT of <paramref name="mapId"/>.</summary>
    public WdtLoad LoadWdt(uint mapId)
    {
        IClientFileSource? files = _files();
        if (files is null)
        {
            return new WdtLoad(TileState.Missing, "No client files are available (no MPQ reader or extracted client directory is registered).", null, null, null);
        }

        IMapDirectory? maps = _maps();
        if (maps is null)
        {
            return new WdtLoad(TileState.Missing, $"No map directory (Map.dbc) is available to name the folder of map {mapId}.", null, null, null);
        }

        if (!maps.TryGetMap(mapId, out ClientMap map))
        {
            return new WdtLoad(TileState.Missing, $"Map {mapId} is not in Map.dbc.", null, null, null);
        }

        return _wdts.GetOrLoad((files, mapId), () => ReadWdt(files, map), IsParsed);
    }

    /// <summary>
    /// Loads (or returns the cached) ADT of <paramref name="tile"/>. The tile must be inside the grid. A tile the WDT
    /// does not list, an absent file and a zero-length file (a patch archive's deletion marker) are Missing; a file the
    /// reader refuses is Rejected with the reason.
    /// </summary>
    public AdtLoad LoadAdt(TileKey tile)
    {
        if (!tile.IsInGrid)
        {
            throw new ArgumentException($"Tile {tile} is not inside the 64x64 grid, so it has no ADT.", nameof(tile));
        }

        WdtLoad wdt = LoadWdt(tile.MapId);
        if (wdt.File is not { } wdtFile || wdt.Map is not { } map || _files() is not { } files)
        {
            return new AdtLoad(wdt.State, wdt.Detail, null, null, null);
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:857-858 (the extractor converts only the tiles MAIN lists)
        if (!wdtFile.HasTile(tile.X, tile.Y))
        {
            return new AdtLoad(TileState.Missing, $"The WDT of {map.Directory} does not list tile ({tile.X}, {tile.Y}); the client never loads it.", null, null, null);
        }

        return _adts.GetOrLoad((files, tile), () => ReadAdt(files, map, tile), IsParsed);
    }

    /// <summary>
    /// The answer for <paramref name="tile"/> when it is known without parsing the ADT (already loaded, or the WDT rules
    /// the tile out); null when only reading the ADT can tell.
    /// </summary>
    public AdtLoad? PeekAdt(TileKey tile)
    {
        if (!tile.IsInGrid)
        {
            return null;
        }

        WdtLoad wdt = LoadWdt(tile.MapId);
        if (wdt.File is not { } wdtFile || wdt.Map is not { } map || _files() is not { } files)
        {
            return new AdtLoad(wdt.State, wdt.Detail, null, null, null);
        }

        if (!wdtFile.HasTile(tile.X, tile.Y))
        {
            return new AdtLoad(TileState.Missing, $"The WDT of {map.Directory} does not list tile ({tile.X}, {tile.Y}); the client never loads it.", null, null, null);
        }

        return _adts.TryPeek((files, tile), out AdtLoad? cached) ? cached : null;
    }

    /// <summary>
    /// The tiles of <paramref name="mapId"/> that have an ADT: reads (and caches) the map's WDT for its <c>MAIN</c>
    /// table, then runs one <see cref="IClientFileSource.Exists"/> check per WDT-listed tile without parsing any ADT.
    /// Empty when the map, its WDT or the client files are unavailable. The existence checks are not cached, so the
    /// result follows the files.
    /// </summary>
    public IReadOnlyCollection<TileKey> AvailableTiles(uint mapId)
    {
        IClientFileSource? files = _files();
        WdtLoad wdt = LoadWdt(mapId);
        if (files is null || wdt.File is not { } wdtFile || wdt.Map is not { } map)
        {
            return [];
        }

        return [.. wdtFile.Tiles(mapId).Where(t => files.Exists(ClientMapPaths.AdtPath(map.Directory, t)))];
    }

    /// <summary>
    /// Samples the client terrain height, exact MCVT 4-triangle surface normal, slope angle in degrees, walkability
    /// (<c>slopeDegrees &lt; 60.0</c>), and clamped liquid depth (<c>max(0, liquidZ - terrainZ)</c>) at world
    /// (<paramref name="x"/>, <paramref name="y"/>) on <paramref name="mapId"/>. Returns <c>null</c> when the position is
    /// outside the 64x64 tile grid or the tile has no readable ADT.
    /// </summary>
    public TerrainSurfaceSample? SampleSurface(uint mapId, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return null;
        }

        double tx = Math.Floor(32 - (x / WorldGrid.TileSize));
        double ty = Math.Floor(32 - (y / WorldGrid.TileSize));
        if (tx is < 0 or >= WorldGrid.TilesPerSide || ty is < 0 or >= WorldGrid.TilesPerSide)
        {
            return null;
        }

        var tile = new TileKey(mapId, (int)tx, (int)ty);
        AdtLoad load = LoadAdt(tile);
        return load.File is { } adt ? ClientTerrainGeometry.SampleSurfaceAt(adt, tile, x, y) : null;
    }

    /// <summary>
    /// Computes elevation relief (min/max/mean), steep-triangle count and ratio, hole square count, liquid coverage
    /// ratio, sorted unique <c>AreaId</c> list, and steepest chunks for <paramref name="tile"/>. Returns <c>null</c>
    /// when the tile's ADT is missing or rejected.
    /// </summary>
    public TerrainTileAnalysis? AnalyzeTile(TileKey tile)
    {
        AdtLoad load = LoadAdt(tile);
        return load.File is { } adt ? ClientTerrainGeometry.AnalyzeTile(tile, adt) : null;
    }

    private static bool IsParsed(WdtLoad load) => load.State == TileState.Ok;

    private static bool IsParsed(AdtLoad load) => load.State == TileState.Ok;

    private WdtLoad ReadWdt(IClientFileSource files, ClientMap map)
    {
        string path = ClientMapPaths.WdtPath(map.Directory);
        byte[]? bytes;
        try
        {
            bytes = files.Read(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Client terrain: {Path} could not be read: {Message}", path, e.Message);
            return new WdtLoad(TileState.Rejected, $"{path} could not be read: {e.Message}", null, map, path);
        }

        if (bytes is null)
        {
            return new WdtLoad(TileState.Missing, $"{path} is not in the client files ({files.Description}).", null, map, path);
        }

        if (bytes.Length == 0)
        {
            return new WdtLoad(TileState.Missing, $"{path} is zero bytes long (a patch archive deleted it).", null, map, path);
        }

        try
        {
            return new WdtLoad(TileState.Ok, null, WdtFile.Parse(bytes), map, path);
        }
        catch (InvalidDataException e)
        {
            _log.LogWarning("Client terrain: {Path} was refused: {Message}", path, e.Message);
            return new WdtLoad(TileState.Rejected, $"{path}: {e.Message}", null, map, path);
        }
    }

    private AdtLoad ReadAdt(IClientFileSource files, ClientMap map, TileKey tile)
    {
        string path = ClientMapPaths.AdtPath(map.Directory, tile);
        byte[]? bytes;
        try
        {
            bytes = files.Read(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Client terrain: {Path} could not be read: {Message}", path, e.Message);
            return new AdtLoad(TileState.Rejected, $"{path} could not be read: {e.Message}", null, path, null);
        }

        if (bytes is null)
        {
            return new AdtLoad(TileState.Missing, $"{path} is not in the client files ({files.Description}).", null, path, null);
        }

        if (bytes.Length == 0)
        {
            return new AdtLoad(TileState.Missing, $"{path} is zero bytes long (a patch archive deleted it).", null, path, files.ArchiveOf(path));
        }

        try
        {
            return new AdtLoad(TileState.Ok, null, AdtFile.Parse(bytes), path, files.ArchiveOf(path));
        }
        catch (InvalidDataException e)
        {
            _log.LogWarning("Client terrain: {Path} was refused: {Message}", path, e.Message);
            return new AdtLoad(TileState.Rejected, $"{path}: {e.Message}", null, path, files.ArchiveOf(path));
        }
    }
}
