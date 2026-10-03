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

    internal TerrainTile LoadTile(uint mapId, int tileX, int tileY)
    {
        if (_mapsDirectory is null)
        {
            return TerrainTile.Empty;
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
