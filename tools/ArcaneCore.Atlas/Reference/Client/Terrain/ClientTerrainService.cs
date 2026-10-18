using System.Collections.Concurrent;
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
/// Finds, reads and caches the client's WDT and ADT files for the reference terrain layers, the placement source and
/// the commands. The client file source and the map directory are looked up on every call (the foundation module that
/// registers them may run after this service is created), and every cached result remembers which file source produced
/// it. Thread-safe; parsed files are immutable.
/// </summary>
public sealed class ClientTerrainService
{
    /// <summary>How many parsed ADTs are kept (about half a megabyte each).</summary>
    public const int DefaultCacheCapacity = 64;

    private readonly Func<IClientFileSource?> _files;
    private readonly Func<IMapDirectory?> _maps;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<(IClientFileSource Files, uint MapId), Lazy<WdtLoad>> _wdts = new();
    private readonly ConcurrentDictionary<(IClientFileSource Files, uint MapId), Lazy<IReadOnlyCollection<TileKey>>> _available = new();
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

    /// <summary>Drops every cached WDT, tile list and ADT so the next request re-reads the files.</summary>
    public void Clear()
    {
        _wdts.Clear();
        _available.Clear();
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

        return _wdts.GetOrAdd((files, mapId), key => new Lazy<WdtLoad>(() => ReadWdt(key.Files, map), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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

        // ref: D:\refs\vmangos\contrib\extractor\System.cpp:857-858 (the extractor converts only the tiles MAIN lists)
        if (!wdtFile.HasTile(tile.X, tile.Y))
        {
            return new AdtLoad(TileState.Missing, $"The WDT of {map.Directory} does not list tile ({tile.X}, {tile.Y}); the client never loads it.", null, null, null);
        }

        return _adts.GetOrLoad((files, tile), () => ReadAdt(files, map, tile));
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
    /// The tiles of <paramref name="mapId"/> that have an ADT: those the WDT lists whose file the client files hold.
    /// Empty when the map, its WDT or the client files are unavailable.
    /// </summary>
    public IReadOnlyCollection<TileKey> AvailableTiles(uint mapId)
    {
        IClientFileSource? files = _files();
        WdtLoad wdt = LoadWdt(mapId);
        if (files is null || wdt.File is not { } wdtFile || wdt.Map is not { } map)
        {
            return [];
        }

        return _available.GetOrAdd((files, mapId), _ => new Lazy<IReadOnlyCollection<TileKey>>(
            () => [.. wdtFile.Tiles(mapId).Where(t => files.Exists(ClientMapPaths.AdtPath(map.Directory, t)))],
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private WdtLoad ReadWdt(IClientFileSource files, ClientMap map)
    {
        string path = ClientMapPaths.WdtPath(map.Directory);
        byte[]? bytes;
        try
        {
            bytes = files.Read(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
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
        catch (Exception e) when (e is InvalidDataException or IOException)
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
