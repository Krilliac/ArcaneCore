using ArcaneCore.Game.Maps.Templates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Maps.Terrain;

/// <summary>
/// Terrain for every map of a world (vmangos <c>TerrainManager</c>): where the extractor output
/// lives, one <see cref="TerrainInfo"/> per map, and the area table zone lookups use.
/// <para>
/// The data directory is optional. When it is empty or has no <c>maps/</c> folder the manager is
/// disabled and every tile reads as empty (logged once); a missing file is normal (instances and
/// open sea have none — vmangos <c>GridMap::loadData</c> "Not return error if file not found");
/// an unreadable or incompatible file is logged as an error and also reads as empty.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class TerrainManager
{
    private readonly Dictionary<uint, TerrainInfo> _maps = [];
    private readonly ILogger _logger;
    private readonly string? _mapsDirectory;
    private readonly TilePrefetchCache<TerrainTile> _prefetch = new();

    public TerrainManager(string? dataDirectory, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            string maps = Path.Combine(dataDirectory, "maps");
            if (Directory.Exists(maps))
            {
                _mapsDirectory = maps;
                _logger.LogInformation("Terrain: reading .map files from {Directory}", maps);
            }
            else
            {
                _logger.LogWarning("Terrain: {Directory} not found; heights, areas and liquids are unavailable", maps);
            }
        }
        else
        {
            _logger.LogInformation("Terrain: no data directory configured (World:Maps:DataDirectory); heights, areas and liquids are unavailable");
        }
    }

    /// <summary>Whether a <c>maps/</c> directory was found.</summary>
    public bool Enabled => _mapsDirectory is not null;

    /// <summary>Area/zone lookup table (replaced when the world database is loaded).</summary>
    public AreaTable Areas { get; set; } = AreaTable.Empty;

    /// <summary>Number of tile files read so far (diagnostics and tests).</summary>
    public int FilesLoaded { get; private set; }

    /// <summary>The terrain of a map (created on first use).</summary>
    public TerrainInfo For(uint mapId)
    {
        if (!_maps.TryGetValue(mapId, out TerrainInfo? terrain))
        {
            terrain = new TerrainInfo(mapId, this);
            _maps[mapId] = terrain;
        }

        return terrain;
    }

    /// <summary>Prefetches the loader took instead of reading the file itself (diagnostics and tests).</summary>
    internal int PrefetchHits => _prefetch.Hits;

    /// <summary>
    /// Read and parse a tile's <c>.map</c> file on the thread pool so a later load only installs it (<see cref="TilePrefetchCache{T}"/>);
    /// nothing happens without terrain data or when the tile is already in memory. World thread.
    /// </summary>
    public void Prefetch(uint mapId, int tileX, int tileY)
    {
        if (_mapsDirectory is null || (_maps.TryGetValue(mapId, out TerrainInfo? terrain) && terrain.IsTileLoaded(tileX, tileY)))
        {
            return;
        }

        string path = Path.Combine(_mapsDirectory, TerrainTile.FileName(mapId, tileX, tileY));
        _prefetch.Request(mapId, tileX, tileY, () => File.Exists(path) ? TerrainTile.Parse(File.ReadAllBytes(path)) : null);
    }

    /// <summary>
    /// The zone and area at a position, read straight from the tile's <c>.map</c> file without the tile cache (vmangos
    /// <c>TerrainInfo::GetZoneAndAreaId</c> without WMO areas): (0, 0) without terrain data, outside the map or for a missing or
    /// unreadable file. Thread affinity: any thread. It reads only the data directory (fixed at construction) and the given
    /// <paramref name="areas"/> (immutable), never the world-thread tile cache. It reads one file per call, so it is for background work.
    /// </summary>
    public (uint ZoneId, uint AreaId) ReadZoneAndAreaIdDetached(uint mapId, float x, float y, AreaTable areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        if (_mapsDirectory is null || TerrainTile.TileOf(x, y) is not { } tile)
        {
            return (0, 0);
        }

        string path = Path.Combine(_mapsDirectory, TerrainTile.FileName(mapId, tile.X, tile.Y));
        try
        {
            if (!File.Exists(path))
            {
                return (0, 0);
            }

            return areas.GetZoneAndAreaId(TerrainTile.Parse(File.ReadAllBytes(path)).GetAreaFlag(x, y), mapId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Terrain: could not read {Path}", path);
            return (0, 0);
        }
    }

    internal TerrainTile LoadTile(uint mapId, int tileX, int tileY)
    {
        if (_mapsDirectory is null)
        {
            return TerrainTile.Empty;
        }

        if (_prefetch.TryTake(mapId, tileX, tileY, out TerrainTile prefetched))
        {
            FilesLoaded++;
            return prefetched;
        }

        string path = Path.Combine(_mapsDirectory, TerrainTile.FileName(mapId, tileX, tileY));
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return TerrainTile.Empty;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Terrain: could not read {Path}", path);
            return TerrainTile.Empty;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Terrain: could not read {Path}", path);
            return TerrainTile.Empty;
        }

        try
        {
            TerrainTile tile = TerrainTile.Parse(bytes);
            FilesLoaded++;
            return tile;
        }
        catch (InvalidDataException ex)
        {
            // vmangos: "Map file '%s' is non-compatible version (outdated?)" — logged, tile left empty.
            _logger.LogError("Terrain: {Path} is not a usable z1.4 .map file ({Reason}); re-extract the maps", path, ex.Message);
            return TerrainTile.Empty;
        }
    }
}
