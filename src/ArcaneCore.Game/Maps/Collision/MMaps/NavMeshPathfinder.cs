using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Maps.Collision.MMaps;

/// <summary>
/// The navmesh-backed <see cref="IPathfinder"/> (vmangos <c>MMapManager</c> + <c>PathFinder</c>):
/// one <see cref="NavMesh"/> per map from <c>NNN.mmap</c>, tiles loaded and released with the
/// map's grids (<see cref="ICollisionTileLifecycle"/>), paths by <see cref="NavMeshQuery"/>.
/// <para>
/// A map without a <c>.mmap</c> answers straight lines (<see cref="PathType.NotUsingPath"/>), as
/// vmangos does for maps without navmesh. With a navmesh, a start or end farther than the search
/// box from any loaded polygon answers <see cref="PathType.NoPath"/> (a two-point shortcut, so a
/// caller may still decide to move straight); an end off the mesh but above or below it is
/// projected and the path is <see cref="PathType.Incomplete"/>. Corrupt files are logged and read
/// as missing; nothing here throws into the world thread.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class NavMeshPathfinder : IPathfinder, ICollisionTileLifecycle
{
    /// <summary>vmangos <c>PathFinder</c> search box half-extents (Recast x, y, z) for the nearest polygon.</summary>
    public static readonly Vector3 NearExtents = new(3, 5, 3);

    /// <summary>Second, taller box for points high above or below the mesh (flagged as off the mesh).</summary>
    public static readonly Vector3 FarExtents = new(3, 200, 3);

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, NavMesh?> _meshes = [];

    public NavMeshPathfinder(string directory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool Enabled => true;

    /// <summary>The map's navmesh, reading <c>NNN.mmap</c> on first use; null when the map has none (or it is unusable).</summary>
    public NavMesh? GetNavMesh(uint mapId)
    {
        if (_meshes.TryGetValue(mapId, out NavMesh? cached))
        {
            return cached;
        }

        NavMesh? mesh = null;
        string path = Path.Combine(_directory, NavMeshFormat.ParamsFileName(mapId));
        if (ReadFile(path) is { } bytes)
        {
            try
            {
                mesh = new NavMesh(mapId, NavMeshParams.Parse(bytes));
                _logger.LogInformation("MMaps: map {MapId} navmesh parameters loaded", mapId);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogError("MMaps: {Path} is not usable navmesh parameters ({Reason}); map {MapId} paths are straight lines", path, ex.Message, mapId);
            }
        }

        _meshes[mapId] = mesh;
        return mesh;
    }

    /// <summary>Load a terrain tile's navmesh tile (vmangos <c>MMapManager::loadMap</c>). False when nothing was loaded.</summary>
    public bool LoadTile(uint mapId, int tileX, int tileY)
    {
        if (GetNavMesh(mapId) is not { } mesh)
        {
            return false;
        }

        if (mesh.IsTerrainTileLoaded(tileX, tileY))
        {
            return true;
        }

        string path = Path.Combine(_directory, NavMeshFormat.TileFileName(mapId, tileX, tileY));
        if (ReadFile(path) is not { } bytes)
        {
            return false;
        }

        try
        {
            NavMeshTile tile = NavMeshTile.ParseFile(bytes);
            if (mesh.AddTile(tileX, tileY, tile))
            {
                return true;
            }

            _logger.LogError("MMaps: {Path} claims Detour tile ({X}, {Y}), which is already loaded; ignored", path, tile.X, tile.Y);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError("MMaps: {Path} is not a usable navmesh tile ({Reason}); ignored", path, ex.Message);
        }

        return false;
    }

    /// <summary>Release a terrain tile's navmesh tile (vmangos <c>MMapManager::unloadMap</c>).</summary>
    public void UnloadTile(uint mapId, int tileX, int tileY)
    {
        if (_meshes.GetValueOrDefault(mapId) is { } mesh)
        {
            mesh.RemoveTile(tileX, tileY);
        }
    }

    void ICollisionTileLifecycle.OnTileLoaded(uint mapId, int tileX, int tileY) => LoadTile(mapId, tileX, tileY);

    void ICollisionTileLifecycle.OnTileUnloaded(uint mapId, int tileX, int tileY) => UnloadTile(mapId, tileX, tileY);

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        options ??= PathOptions.Default;
        if (!IsFinite(start) || !IsFinite(end))
        {
            return PathResult.None(start);
        }

        if (_meshes.GetValueOrDefault(mapId) is not { } mesh)
        {
            return PathResult.StraightLine(start, end, PathType.Normal | PathType.NotUsingPath);
        }

        Vector3 startRc = NavMeshFormat.ToRecast(start);
        Vector3 endRc = NavMeshFormat.ToRecast(end);
        if (!TryLocate(mesh, startRc, options, out NavPolyRef startPoly, out Vector3 startOnMesh, out _)
            || !TryLocate(mesh, endRc, options, out NavPolyRef endPoly, out Vector3 endOnMesh, out bool endFar))
        {
            return PathResult.StraightLine(start, end, PathType.NoPath);
        }

        (List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> corridor, bool complete) =
            NavMeshQuery.FindCorridor(mesh, startPoly, startOnMesh, endPoly, endOnMesh, options);
        if (!complete && !options.AllowPartial)
        {
            return PathResult.StraightLine(start, end, PathType.NoPath);
        }

        Vector3 goal = complete ? endOnMesh : corridor[^1].Poly.Tile.ClosestPointOnPoly(corridor[^1].Poly.Poly, endRc);
        List<Vector3> corners = NavMeshQuery.StringPull(startOnMesh, [.. corridor.Skip(1).Select(c => (c.Left, c.Right))], goal);

        var points = new List<Vector3>(corners.Count) { start };
        for (int i = 1; i < corners.Count; i++)
        {
            points.Add(NavMeshFormat.ToWorld(corners[i]));
        }

        if (points.Count == 1)
        {
            points.Add(NavMeshFormat.ToWorld(goal));
        }

        PathType type = complete && !endFar ? PathType.Normal : PathType.Incomplete;
        int max = Math.Max(2, options.MaxPoints);
        if (points.Count > max)
        {
            points.RemoveRange(max, points.Count - max);
            type |= PathType.Short;
        }

        return new PathResult(type, points);
    }

    private static bool TryLocate(NavMesh mesh, Vector3 position, PathOptions options, out NavPolyRef poly, out Vector3 onMesh, out bool far)
    {
        far = false;
        if (mesh.TryFindNearestPoly(position, NearExtents, options, out poly, out onMesh))
        {
            far = NavMeshTile.DistanceXzSquared(onMesh, position) > 0.01f || MathF.Abs(onMesh.Y - position.Y) > NearExtents.Y;
            return true;
        }

        far = true;
        return mesh.TryFindNearestPoly(position, FarExtents, options, out poly, out onMesh);
    }

    private byte[]? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "MMaps: could not read {Path}", path);
            return null;
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
