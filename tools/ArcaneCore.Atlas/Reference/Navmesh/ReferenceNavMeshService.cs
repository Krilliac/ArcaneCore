using System.Globalization;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>The result of opening a map's navmesh parameters.</summary>
/// <param name="Map">The map, or null when <paramref name="State"/> is not <see cref="NavTileState.Ok"/>.</param>
/// <param name="State"><see cref="NavTileState.Missing"/> when there is no mmaps directory or no <c>NNN.mmap</c>; <see cref="NavTileState.Rejected"/> when the file is unreadable.</param>
/// <param name="Detail">Why, for a missing or refused map.</param>
public sealed record NavMapOpen(ReferenceNavMap? Map, NavTileState State, string? Detail);

/// <summary>
/// The DotRecast reference navmesh of every map under one <c>mmaps</c> directory: opens <c>NNN.mmap</c> on first use, keeps one
/// <see cref="ReferenceNavMap"/> per map and lists the tile files each map has. Registered as a service so the diff lane can read the
/// same DotRecast mesh the reference layers and queries use.
/// </summary>
/// <remarks>Thread safety: any thread. Disposed with the Atlas context.</remarks>
public sealed class ReferenceNavMeshService : IDisposable
{
    /// <summary>Tiles kept in memory per map (a large tile is a few MB of DotRecast objects).</summary>
    public const int DefaultMaxResidentTiles = 96;

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, ReferenceNavMap> _maps = [];
    private readonly ILogger _log;
    private readonly int _maxResident;
    private bool _disposed;

    /// <summary>Creates the service over <paramref name="mmapsDirectory"/> (null when the data root has no <c>mmaps</c> folder).</summary>
    public ReferenceNavMeshService(string? mmapsDirectory, ILogger log, int maxResidentTilesPerMap = DefaultMaxResidentTiles)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResidentTilesPerMap, 9);
        Directory = mmapsDirectory;
        _log = log;
        _maxResident = maxResidentTilesPerMap;
    }

    /// <summary>The <c>mmaps</c> directory, or null.</summary>
    public string? Directory { get; }

    /// <summary>Opens (or returns) the map's navmesh. Failures are not cached: a file extracted later is found on the next call.</summary>
    public NavMapOpen OpenMap(uint mapId)
    {
        if (Directory is null)
        {
            return new NavMapOpen(null, NavTileState.Missing, "No mmaps directory: the terrain data root has no 'mmaps' folder.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_maps.TryGetValue(mapId, out ReferenceNavMap? open))
            {
                return new NavMapOpen(open, NavTileState.Ok, null);
            }
        }

        string path = Path.Combine(Directory, NavMeshFormat.ParamsFileName(mapId));
        if (!File.Exists(path))
        {
            return new NavMapOpen(null, NavTileState.Missing, $"No {NavMeshFormat.ParamsFileName(mapId)} in {Directory}: map {mapId} has no navmesh.");
        }

        NavMeshParams parameters;
        try
        {
            parameters = MmapParamsReader.Parse(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Navmesh parameters {Path} refused: {Reason}", path, e.Message);
            return new NavMapOpen(null, NavTileState.Rejected, $"{NavMeshFormat.ParamsFileName(mapId)} is unusable: {e.Message}");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_maps.TryGetValue(mapId, out ReferenceNavMap? map))
            {
                map = new ReferenceNavMap(mapId, Directory, parameters, _maxResident, _log);
                _maps[mapId] = map;
            }

            return new NavMapOpen(map, NavTileState.Ok, null);
        }
    }

    /// <summary>Map ids that have an <c>NNN.mmap</c> file (a directory listing).</summary>
    public IReadOnlyList<uint> MapIds()
    {
        if (Directory is null)
        {
            return [];
        }

        var ids = new List<uint>();
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.mmap"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length >= 3 && name.All(char.IsAsciiDigit) && uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            {
                ids.Add(id);
            }
        }

        ids.Sort();
        return ids;
    }

    /// <summary>Tiles of <paramref name="mapId"/> that have a <c>NNNXXYY.mmtile</c> file (a directory listing; nothing is read).</summary>
    public IReadOnlyCollection<TileKey> AvailableTiles(uint mapId)
    {
        if (Directory is null || !System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        string prefix = mapId.ToString("D3", CultureInfo.InvariantCulture);
        var tiles = new List<TileKey>();
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, prefix + "????.mmtile"))
        {
            // NavMeshFormat.TileFileName: {map:D3}{tileX:D2}{tileY:D2}.mmtile, so two digits each after the map id.
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == prefix.Length + 4 && name.AsSpan(prefix.Length).ContainsAnyExceptInRange('0', '9') is false
                && int.TryParse(name.AsSpan(prefix.Length, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(name.AsSpan(prefix.Length + 2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int y))
            {
                var key = new TileKey(mapId, x, y);
                if (key.IsInGrid)
                {
                    tiles.Add(key);
                }
            }
        }

        tiles.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        return tiles;
    }

    /// <summary>Disposes every opened map.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (ReferenceNavMap map in _maps.Values)
            {
                map.Dispose();
            }

            _maps.Clear();
        }
    }
}
