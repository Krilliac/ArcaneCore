using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The check a bot's movement leg must pass when it does not come from a navigation-mesh corridor (a stepped terrain route, a retreat
/// trail, a step of such a route): the mesh proves its own corridors walkable, nothing else does, and the server does not collide
/// bot movement packets (anticheat exempts managed bots). Two tests, both stateless:
/// <list type="number">
/// <item>Line of sight at chest height <b>including M2 doodads</b> (<c>ignoreM2: false</c>). The shared default
/// (<see cref="ILineOfSight.IsInLineOfSight"/>, <see cref="MapCollision.IsInLineOfSight"/>) ignores them as vmangos does for spells, so
/// fences, trees and rocks never blocked a bot; spells keep that default, only these movement checks differ.</item>
/// <item>Where a mesh is loaded under the leg, the mesh raycast (<see cref="IMeshPointQuery.Raycast"/>): a leg that leaves the mesh
/// (a hole, a building's footprint, a wall edge) stops there. A leg whose start is off the mesh, or whose ray ends where no mesh tile is
/// loaded beyond (no tile, tile not read yet), is not judged by the mesh: that is "no data", not a wall, and the line of sight alone
/// applies.</item>
/// </list>
/// A refused leg that can still make progress is cut at the hit point (minus <see cref="CutMarginYards"/>) instead of refused outright.
/// <para>
/// Cost: one raycast (a bounded polygon walk, no allocation, <see cref="PathOptions"/> shared) and one vmap segment query per leg, a
/// handful more (<see cref="CutSearchSteps"/>) only when the segment is blocked. Callers run it per leg when a route is planned, not
/// per tick; the per-step checks of a stepped route (<see cref="PlayerbotMotion"/>) use <see cref="TryValidateStep"/> and <see cref="LineClear"/>.
/// No allocation apart from the route a caller asks for. World thread.
/// </para>
/// </summary>
internal static class PlayerbotMoveCheck
{
    /// <summary>Height above the feet of the line-of-sight ends: chest, so that a fence, a rock or a low wall is hit and a floor model is not.</summary>
    internal const float ChestYards = 1.2f;

    /// <summary>How far short of the obstacle a cut leg stops.</summary>
    internal const float CutMarginYards = 0.5f;

    /// <summary>The shortest cut leg worth walking (flat yards): a shorter one is a refusal.</summary>
    internal const float MinCutYards = 1.5f;

    /// <summary>Bisection steps that find where a blocked segment first blocks (1/64 of its length).</summary>
    internal const int CutSearchSteps = 6;

    /// <summary>Maximum upward step onto a walkable floor for a non-navigated (<c>DirectStep</c>) step.</summary>
    internal const float DirectStepMaxStepUpYards = 2f;

    /// <summary>
    /// Maximum downward distance from an un-draped <c>DirectStep</c> candidate height to the walkable floor beneath it that may be
    /// draped onto the floor instead of floating in mid-air or failing support (covers 6..10 yd elevation drift while still rejecting
    /// 30 yd voids/cliffs).
    /// </summary>
    internal const float DirectStepMaxDrapeYards = 11f;

    /// <summary>
    /// Maximum drop from the lower of a <c>DirectStep</c>'s two ends to the floor under its destination. The drape
    /// (<see cref="DirectStepMaxDrapeYards"/>) brings a step whose waypoint height drifted above the floor down onto it; it never lets a bot
    /// that stands on the floor step off an edge into a pit or down a cliff deeper than this. Never below
    /// <see cref="PlayerbotMotion.DirectStepDrapeMinYards"/>: a standing bot lower than that is not snapped to the floor before it steps,
    /// so its first step must not be refused for a drop it does not have.
    /// </summary>
    internal const float DirectStepMaxDropYards = PlayerbotMotion.DirectStepDrapeMinYards;

    /// <summary>
    /// Steepest ground (rise over run, about 50 degrees, the walkable limit) a <c>DirectStep</c> may descend: the floor under the step's
    /// start may lie this much lower per flat yard than the floor under its end without the step counting as a drop.
    /// </summary>
    internal const float DirectStepMaxSlope = 1.19f;

    /// <summary>How far past a mesh edge the "is there mesh beyond" probe is taken.</summary>
    private const float BeyondYards = 1f;

    /// <summary>The bot's own mesh filter (steep slopes excluded, as for every bot route). Read-only after construction.</summary>
    internal static readonly PathOptions MeshOptions = new() { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes };

    /// <summary>Whether no static model, M2 doodads included, blocks the chest-height segment between two feet positions.</summary>
    internal static bool LineClear(MapCollision collision, Vector3 from, Vector3 to)
        => collision.LineOfSight.IsInLineOfSight(collision.Map.MapId,
            new Vector3(from.X, from.Y, from.Z + ChestYards), new Vector3(to.X, to.Y, to.Z + ChestYards), ignoreM2: false);

    /// <summary>
    /// Validate and floor-drape a non-navigated (<c>DirectStep</c>) step from <paramref name="from"/> to <paramref name="to"/>.
    /// Resolves the walkable floor at <paramref name="to"/>, drapes elevated candidate heights within <see cref="DirectStepMaxDrapeYards"/>
    /// onto <c>ground + 0.05f</c>, and verifies chest-height line of sight. Returns false with <paramref name="rejectReason"/> when no
    /// valid ground support exists or the line of sight is blocked.
    /// </summary>
    internal static bool TryValidateStep(Map map, Vector3 from, Vector3 to, out Vector3 drapedTo, out string? rejectReason)
    {
        drapedTo = to;
        if (!IsFinite(from) || !IsFinite(to))
        {
            rejectReason = "NonFiniteCoordinate";
            return false;
        }

        float maxCeilingZ = MathF.Max(from.Z, to.Z) + 1.25f;
        float ground = PlayerbotMotion.ResolveWalkFloor(map, to.X, to.Y, to.Z, 0f, maxCeilingZ);
        if (!PlayerbotMotion.ValidHeight(ground) || ground - to.Z > DirectStepMaxStepUpYards || to.Z - ground > DirectStepMaxDrapeYards)
        {
            float raised = PlayerbotMotion.ResolveWalkFloor(map, to.X, to.Y, to.Z, DirectStepMaxStepUpYards, maxCeilingZ);
            if (PlayerbotMotion.ValidHeight(raised)
                && raised - to.Z <= DirectStepMaxStepUpYards
                && to.Z - raised <= DirectStepMaxDrapeYards)
            {
                ground = raised;
            }
        }

        // The drape is for a waypoint height that drifted above the floor (the bot is draped onto it before it steps,
        // PlayerbotMotion.TrySnapDirectStepStart): measured from the lower end, a floor far below both is a drop, not drift.
        if (!PlayerbotMotion.ValidHeight(ground) || ground - to.Z > DirectStepMaxStepUpYards || to.Z - ground > DirectStepMaxDrapeYards
            || !DropAllowed(map, from, to, ground, maxCeilingZ))
        {
            rejectReason = "NoGroundSupport";
            return false;
        }

        Vector3 grounded = to with { Z = ground };
        if (!LineClear(map.Collision, from, grounded))
        {
            rejectReason = "LineBlocked";
            return false;
        }

        drapedTo = to with { Z = ground + 0.05f };
        rejectReason = null;
        return true;
    }

    /// <summary>
    /// Whether the floor under a step's end (<paramref name="ground"/>) is no pit: at most <see cref="DirectStepMaxDropYards"/> under the
    /// lower end, or, for a bot standing on its floor, no more than a walkable slope falls over the step's length (the floor under
    /// <paramref name="from"/> against the floor under <paramref name="to"/>, so waypoint heights that drifted above a slope do not
    /// turn a descent into a drop).
    /// </summary>
    private static bool DropAllowed(Map map, Vector3 from, Vector3 to, float ground, float maxCeilingZ)
    {
        float lower = MathF.Min(from.Z, to.Z);
        if (lower - ground <= DirectStepMaxDropYards) return true;
        float fromFloor = PlayerbotMotion.ResolveWalkFloor(map, from.X, from.Y, from.Z, 0f, maxCeilingZ);
        if (!PlayerbotMotion.ValidHeight(fromFloor) || lower - fromFloor > DirectStepMaxDropYards) return false;
        float run = MathF.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Y - from.Y) * (to.Y - from.Y)));
        return fromFloor - ground <= run * DirectStepMaxSlope;
    }

    /// <summary>Whether the whole leg from <paramref name="from"/> to <paramref name="to"/> may be walked as it is (not cut, not refused).</summary>
    internal static bool CanStep(Map map, Vector3 from, Vector3 to)
        => TryClip(map, from, to, out Vector3 reach) && reach == to;

    /// <summary>
    /// The part of the leg that may be walked: <paramref name="reach"/> is <paramref name="to"/> exactly for a clear leg, the cut point
    /// (<see cref="CutMarginYards"/> before the first obstacle) for a blocked one that still makes <see cref="MinCutYards"/> of progress.
    /// False (reach = from) for a leg with a non-finite end or one blocked at once.
    /// </summary>
    internal static bool TryClip(Map map, Vector3 from, Vector3 to, out Vector3 reach)
    {
        reach = from;
        if (!IsFinite(from) || !IsFinite(to)) return false;
        MapCollision collision = map.Collision;
        Vector3 end = to;
        bool cut = false;
        if (MeshCut(map, from, to, out Vector3 meshEnd))
        {
            end = meshEnd;
            cut = true;
        }

        if (!LineClear(collision, from, end))
        {
            float clear = 0f, blocked = 1f;
            for (int step = 0; step < CutSearchSteps; step++)
            {
                float middle = (clear + blocked) * 0.5f;
                if (LineClear(collision, from, Vector3.Lerp(from, end, middle))) clear = middle;
                else blocked = middle;
            }

            end = BackOff(from, Vector3.Lerp(from, end, clear), CutMarginYards);
            cut = true;
        }

        if (!cut)
        {
            reach = to;
            return true;
        }

        if (Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(end.X, end.Y)) < MinCutYards) return false;
        reach = end;
        return true;
    }

    /// <summary>
    /// The mesh raycast of the leg: true with <paramref name="end"/> the point (<see cref="CutMarginYards"/> before the mesh edge) where the
    /// leg leaves the mesh. False when the leg stays on it, or when the mesh cannot judge it (no mesh, the start off it, no mesh beyond the edge).
    /// </summary>
    internal static bool MeshCut(Map map, Vector3 from, Vector3 to, out Vector3 end)
    {
        end = to;
        if (map.Collision.MeshPoints is not { } mesh) return false;
        uint mapId = map.MapId;
        MeshRaycastResult ray = mesh.Raycast(mapId, from, to, MeshOptions);
        if (!ray.Available || !ray.Hit || ray.Fraction >= 1f) return false;
        Vector2 flat = new(to.X - from.X, to.Y - from.Y);
        float length = flat.Length();
        if (!float.IsFinite(length) || length < 1e-3f) return false;
        Vector2 direction = flat / length;

        // Mesh beyond the edge? An edge between two polygons that the filter refuses, a hole or a wall has mesh within a yard; the
        // border of the loaded tiles has none, and that is no evidence of an obstacle (the stepper exists to cross such ground).
        Vector3 probe = new(ray.Point.X + (direction.X * BeyondYards), ray.Point.Y + (direction.Y * BeyondYards), ray.Point.Z);
        if (!mesh.Raycast(mapId, probe, probe, MeshOptions).Available) return false;

        float yards = MathF.Max(0f, (length * ray.Fraction) - CutMarginYards);
        end = new Vector3(from.X + (direction.X * yards), from.Y + (direction.Y * yards), ray.Point.Z);
        return true;
    }

    /// <summary>
    /// A stepped terrain route from <paramref name="start"/> to <paramref name="destination"/> (<see cref="PlayerbotNavigation.TryTerrainRoute"/>)
    /// whose every yard is clear of models including doodads and which stays on the mesh where one is loaded; refused otherwise. It is
    /// never cut here: the caller (<c>PlanDirect</c>) answers a refusal, and <see cref="PlayerbotNavigation.TryPlanToward"/> then plans a
    /// shorter chunk (down to 24 yards), which is how a stepped route ends before an obstacle and how the mesh's own shorter step stays
    /// preferred to a cut stepped one.
    /// </summary>
    internal static bool TerrainRoute(Map map, Vector3 start, Vector3 destination, PlayerbotOptions options, out PlayerbotRoute? route)
    {
        MapCollision collision = map.Collision;
        if (!PlayerbotNavigation.TryTerrainRoute(start, destination, options, (x, y, z) => collision.GetHeight(x, y, z),
                (a, b) => LineClear(collision, a, b), out route) || route is null
            || MeshCut(map, start, route.Points[^1], out _))
        {
            route = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The walk back along a trail: from <paramref name="here"/> through <paramref name="crumbs"/> from the newest down to index
    /// <paramref name="end"/>, each leg checked (<see cref="TryClip"/>). The route ends where the first blocked leg is cut, or at the
    /// last clear point; null when not even the first leg makes progress.
    /// </summary>
    internal static PlayerbotRoute? TrailRoute(Map map, Vector3 here, IReadOnlyList<Vector3> crumbs, int end)
    {
        var points = new List<Vector3>(crumbs.Count - end + 1) { here };
        float length = 0f;
        for (int index = crumbs.Count - 1; index >= end; index--)
        {
            Vector3 previous = points[^1];
            bool whole = TryClip(map, previous, crumbs[index], out Vector3 reach);
            if (!whole || reach != crumbs[index])
            {
                // Cut (or refused): keep the part that makes progress and stop, a leg beyond a blocked one is not reached.
                if (reach != previous)
                {
                    length += Vector3.Distance(previous, reach);
                    points.Add(reach);
                }

                break;
            }

            length += Vector3.Distance(previous, reach);
            points.Add(reach);
        }

        return points.Count >= 2 ? new PlayerbotRoute(points, length) : null;
    }

    private static Vector3 BackOff(Vector3 from, Vector3 point, float yards)
    {
        float length = Vector3.Distance(from, point);
        if (length <= yards) return from;
        return Vector3.Lerp(point, from, yards / length);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
