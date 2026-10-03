using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// The <see cref="ILineOfSight"/> used without vmap data (vmangos with <c>vmap.enableLOS</c> /
/// <c>vmap.enableHeight</c> off): everything is in sight, nothing is hit, there is no model
/// height and no WMO area.
/// </summary>
public sealed class OpenLineOfSight : ILineOfSight
{
    public static readonly OpenLineOfSight Instance = new();

    public bool Enabled => false;

    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        hit = to;
        return false;
    }

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
    {
        info = default;
        return false;
    }
}

/// <summary>
/// The <see cref="IPathfinder"/> used without navigation data (vmangos <c>PathFinder</c> with
/// mmaps disabled: <c>BuildShortcut</c>, type NORMAL | NOT_USING_PATH): start → end.
/// </summary>
public sealed class StraightLinePathfinder : IPathfinder
{
    public static readonly StraightLinePathfinder Instance = new();

    public bool Enabled => false;

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        => PathResult.StraightLine(start, end, PathType.Normal | PathType.NotUsingPath);
}
