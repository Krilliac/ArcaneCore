using System.Numerics;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>What <see cref="GroundHeight.TrySnap"/> found the floor in.</summary>
public enum GroundSource
{
    /// <summary>No floor in the window: the height was kept.</summary>
    None,

    /// <summary>The terrain surface.</summary>
    Terrain,

    /// <summary>A static model floor (WMO or M2) that <see cref="MapCollision.GetHeight"/> chose.</summary>
    Model,

    /// <summary>The navigation-mesh surface less <see cref="GroundHeight.MeshBias"/>, used only where the map has no terrain or model answer.</summary>
    Mesh,

    /// <summary>The liquid surface, with <see cref="LiquidMode.Surface"/> over a floor under the liquid.</summary>
    LiquidSurface,
}

/// <summary>What a point over liquid snaps to.</summary>
public enum LiquidMode
{
    /// <summary>The floor under the liquid: a walker wading through.</summary>
    Bottom,

    /// <summary>The liquid surface where the liquid covers the floor: a swimmer, or a position given in water.</summary>
    Surface,
}

/// <summary>
/// How <see cref="GroundHeight"/> looks for the floor under a point. <c>new GroundQuery()</c> is the walker default
/// (<see cref="GroundHeight.Walker"/>); <c>default(GroundQuery)</c> is all zeros and is not a usable query.
/// </summary>
/// <param name="Up">The highest floor taken above the point's z (yd); the floor is probed from z + Up.</param>
/// <param name="Down">The lowest floor taken below the point's z (yd). A floor outside [z - Down, z + Up] is another storey: no floor.</param>
/// <param name="Liquid">What a point over liquid snaps to.</param>
/// <param name="PreferTerrainOverProps">
/// A model floor more than 0.4 yd above the terrain, with the terrain nearer to z, gives the terrain: an M2 doodad, a roof eave or a
/// branch over the walkable ground is not the floor (<see cref="MapCollision.GetHeight"/> prefers any model above the terrain, vmangos parity).
/// </param>
/// <param name="AllowMeshFallback">No terrain or model answer: the navigation mesh's surface less <see cref="GroundHeight.MeshBias"/>.</param>
/// <param name="LoadTiles">Load the collision and terrain tiles under the point first (<see cref="MapCollision.LoadTilesOnDemand"/>, a synchronous read): GM and console paths with no player near. Off, nothing is read from disk: a point over a terrain tile that is not in memory keeps its height (or takes the mesh fallback).</param>
public readonly record struct GroundQuery(
    float Up,
    float Down,
    LiquidMode Liquid,
    bool PreferTerrainOverProps,
    bool AllowMeshFallback,
    bool LoadTiles)
{
    /// <summary>The walker default: floors from 2 yd above to 4 yd below, the bottom under liquid, props not preferred, mesh fallback on, no tile loading.</summary>
    public GroundQuery()
        : this(2f, 4f, LiquidMode.Bottom, true, true, false)
    {
    }
}

/// <summary>
/// The floor under a point, for units that stand on the ground: terrain plus static models (<see cref="MapCollision.GetHeight"/>), the
/// way the walking bots resolve it (<c>PlayerbotMotion.ResolveWalkFloor</c>), with a model-top guard, a storey window and a navmesh
/// fallback. The navmesh surface itself sits +0.20 yd (median, p95 +0.55) above the real floor (Recast rounds spans up and adds a cell
/// height to detail vertices), so a mesh height kept as a unit's z floats it; <see cref="DrapePath"/> puts every point of a creature leg
/// on the ground and splits a chord that would pass over or under it (docs/areas/collision-pathing.md, "Ground snap").
/// <para>
/// Thread affinity: world thread (or inside the serial gate). The model tree (<c>VMapManager</c>) is world-thread state whose tiles
/// are loaded and freed in place, and the terrain tiles are shared by the instances of a map id: never call this from a path-job pool thread.
/// Allocation: <see cref="TrySnap"/> and <see cref="SnapOrKeep"/> allocate nothing; <see cref="DrapePath"/> allocates its result list.
/// </para>
/// </summary>
public static class GroundHeight
{
    /// <summary>How far above the floor a unit stands (the bots' constant, <c>PlayerbotMotion</c>): the client draws a unit a hair above the ground.</summary>
    public const float StandOffset = 0.05f;

    /// <summary>The measured median of the navmesh surface above the terrain (float lane probe 1: +0.225 over 9752 terrain points). Used only by the mesh fallback.</summary>
    public const float MeshBias = 0.20f;

    /// <summary>How far above the point the mesh fallback looks for a surface (at least; the query's Up if that is more): the window of the raise-only mesh drape this helper replaced.</summary>
    public const float MeshFallbackUpYards = 4f;

    /// <summary>A model floor must beat the terrain by more than this to be taken for a prop (<c>PlayerbotMotion.ResolveWalkFloor</c>).</summary>
    public const float PropHeightYards = 0.4f;

    /// <summary>How much nearer to z the terrain must be than the model top for the guard to take it (<c>PlayerbotMotion.ResolveWalkFloor</c>).</summary>
    public const float PropCloserYards = 0.25f;

    /// <summary>The walker default query.</summary>
    public static readonly GroundQuery Walker = new();

    /// <summary>How far above a navigation-mesh height a floor may be and still be the one the mesh stands on (<see cref="DrapePath"/> <c>cornersOnMesh</c>).</summary>
    public const float MeshCeilingYards = 1.0f;

    /// <summary>Tolerance for a window edge (float noise of heights near z + Up or z - Down).</summary>
    private const float WindowEpsilon = 0.001f;

    /// <summary>
    /// The ground under (<paramref name="x"/>, <paramref name="y"/>) near <paramref name="z"/>, standing height included
    /// (<see cref="StandOffset"/>): the terrain or model floor <see cref="MapCollision.GetHeight"/> gives (with the model-top guard of
    /// <see cref="GroundQuery.PreferTerrainOverProps"/>) when it lies in [z - Down, z + Up]; else, with
    /// <see cref="GroundQuery.AllowMeshFallback"/>, the navigation-mesh surface less <see cref="MeshBias"/> within [z - Down, z + max(Up,
    /// <see cref="MeshFallbackUpYards"/>)]. False, with <paramref name="snappedZ"/> = <paramref name="z"/> and
    /// <paramref name="source"/> = <see cref="GroundSource.None"/>, when an input is not finite or neither finds a floor in its window (no
    /// data, or another storey): the caller keeps the height it had.
    /// </summary>
    public static bool TrySnap(MapCollision collision, float x, float y, float z, in GroundQuery query, out float snappedZ, out GroundSource source)
        => TrySnap(collision, x, y, z, z, query, out snappedZ, out source);

    /// <summary>
    /// <see cref="TrySnap(MapCollision, float, float, float, in GroundQuery, out float, out GroundSource)"/> with the height the model-top guard
    /// (<see cref="GroundQuery.PreferTerrainOverProps"/>) compares against given apart from <paramref name="z"/>: a point between two known
    /// heights is looked for near the interpolated <paramref name="z"/>, but whether a model floor over the terrain is a prop or a floor
    /// stepped onto is decided by the nearer end's height (<paramref name="guardZ"/>), which is a floor, not an interpolation.
    /// </summary>
    internal static bool TrySnap(MapCollision collision, float x, float y, float z, float guardZ, in GroundQuery query, out float snappedZ, out GroundSource source)
    {
        ArgumentNullException.ThrowIfNull(collision);
        snappedZ = z;
        source = GroundSource.None;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            return false;
        }

        if (query.LoadTiles)
        {
            collision.LoadTilesOnDemand(x, y);
        }

        float terrain;
        float floor;
        if (!query.LoadTiles && !collision.Map.Terrain.TryGetLoadedHeight(x, y, out terrain))
        {
            // The terrain tile is not in memory and reading it is a disk read: a drape runs on the world thread, so it keeps the height it
            // had (below, the mesh fallback, which also never loads) and the normal loaders bring the tile in.
            terrain = TerrainTile.InvalidHeightValue;
            floor = TerrainTile.InvalidHeightValue;
        }
        else
        {
            terrain = collision.Map.Terrain.GetHeight(x, y, z);
            floor = collision.GetHeight(x, y, z, query.Up, useModels: true, maxSearchDistance: query.Up + query.Down); // the probe starts at z + Up
        }

        if (query.PreferTerrainOverProps && Known(terrain) && Known(floor)
            && floor > terrain + PropHeightYards && MathF.Abs(terrain - guardZ) + PropCloserYards < MathF.Abs(floor - guardZ))
        {
            floor = terrain;
        }

        if (Known(floor) && floor <= z + query.Up + WindowEpsilon && floor >= z - query.Down - WindowEpsilon)
        {
            source = Known(terrain) && MathF.Abs(terrain - floor) <= 0.02f ? GroundSource.Terrain : GroundSource.Model;
            snappedZ = floor + StandOffset;
            if (query.Liquid == LiquidMode.Surface
                && (collision.Map.GetLiquidStatus(x, y, floor, LiquidTypeFlags.AllLiquids, out LiquidData liquid)
                    & (LiquidStatus.WaterWalk | LiquidStatus.InWater | LiquidStatus.UnderWater)) != 0
                && float.IsFinite(liquid.Level) && liquid.Level > floor
                && liquid.Level <= z + query.Up + WindowEpsilon && liquid.Level >= z - query.Down - WindowEpsilon)
            {
                snappedZ = liquid.Level;
                source = GroundSource.LiquidSurface;
            }

            return true;
        }

        // No usable terrain or model floor in the window (none at all, or in another storey). The mesh surface is a walkable one, so it is
        // looked for higher than a model floor is (MeshFallbackUp): a chord across an arched cave floor, such as Grizzled Den's, runs up to
        // 3.2 yd under the floor, which a model probe from z + 2 starts below.
        if (query.AllowMeshFallback
            && collision.TryGetMeshFloor(x, y, z, query.Down, MathF.Max(query.Up, MeshFallbackUpYards), out float mesh) && float.IsFinite(mesh))
        {
            snappedZ = mesh - MeshBias + StandOffset;
            source = GroundSource.Mesh;
            return true;
        }

        return false;
    }

    /// <summary><paramref name="point"/> with its z on the ground (<see cref="TrySnap"/>), or unchanged when there is no floor in the window.</summary>
    public static Vector3 SnapOrKeep(MapCollision collision, Vector3 point, in GroundQuery query)
        => TrySnap(collision, point.X, point.Y, point.Z, query, out float z, out _) ? point with { Z = z } : point;

    /// <summary>
    /// A leg draped over the ground. Every corner of <paramref name="points"/> is snapped (<see cref="SnapOrKeep"/>; x and y untouched, so
    /// the destination keeps its place); then each segment from the previous point (<paramref name="start"/> for the first) is sampled every
    /// <paramref name="step"/> yd or less and a snapped sample is kept only where leaving it out would put the polyline more than
    /// <paramref name="tolerance"/> yd off the ground at a sample (a sliding window from the last kept point), so a flat leg gains nothing
    /// and a hill gains the points it needs; between samples the ground's own curvature adds a little (about 0.07 yd on a 4 yd step over
    /// the steepest rolling ground measured). Each sample is looked for near the ground before it, so a hill the straight chord misses by
    /// more than the window is followed. A sample with no floor in the window (another storey, a hole) is left out and no point moves more
    /// than the query's Up/Down. Insertion never takes the result past <paramref name="maxPoints"/> points: when it would, the step doubles
    /// (up to six times) and the leg is redone, so the whole leg is coarser rather than cut off; corners alone are never dropped. The
    /// client draws a leg as straight chords between points, so the sent points are the only ground it follows.
    /// </summary>
    /// <param name="query">The floor query; the walker default (<see cref="Walker"/>) when null.</param>
    /// <param name="cornersOnMesh"><paramref name="points"/> are navigation-mesh heights, which are never under the floor they stand over by more
    /// than a hair (Recast rounds a span top up): their floor is looked for from at most <see cref="MeshCeilingYards"/> above, so a roof or
    /// an upper storey 2 yd over the walkable floor is not taken for it (it would be inside the plain window of 2 yd).</param>
    /// <param name="modelTolerance">The tolerance at a sample whose floor is a model (a WMO ramp or stair) or the mesh; <paramref name="tolerance"/> when null.
    /// Terrain is what the eye follows over open ground and gets the tight one; a model floor is stepped and its client chords are looser
    /// anyway, so a looser tolerance there saves points (Undercity's median leg: 12 points at 0.11, 7 at 0.40).</param>
    public static List<Vector3> DrapePath(
        MapCollision collision,
        Vector3 start,
        IReadOnlyList<Vector3> points,
        float step = 4f,
        float tolerance = 0.15f,
        int maxPoints = 32,
        GroundQuery? query = null,
        float? modelTolerance = null,
        bool cornersOnMesh = false)
    {
        ArgumentNullException.ThrowIfNull(collision);
        ArgumentNullException.ThrowIfNull(points);
        GroundQuery q = query ?? Walker;
        var corners = new List<Vector3>(points.Count);
        GroundQuery cornerQuery = cornersOnMesh ? q with { Up = MathF.Min(q.Up, MeshCeilingYards) } : q;
        for (int i = 0; i < points.Count; i++)
        {
            corners.Add(SnapOrKeep(collision, points[i], cornerQuery));
        }

        if (corners.Count == 0 || !float.IsFinite(step) || step <= 0f || corners.Count >= maxPoints)
        {
            return corners; // nothing to insert: a leg that already fills the cap would only redo the whole sampling up to six times
        }

        float stride = MathF.Max(step, 0.5f);
        for (int attempt = 0; attempt <= MaxStepDoublings; attempt++, stride *= 2f)
        {
            if (TryInsertGroundPoints(collision, start, corners, stride, tolerance, modelTolerance ?? tolerance, maxPoints, q) is { } draped)
            {
                return draped;
            }
        }

        return corners;
    }

    /// <summary>The most times <see cref="DrapePath"/> doubles its step to fit <c>maxPoints</c> before it gives up on insertion.</summary>
    private const int MaxStepDoublings = 6;

    /// <summary>The snapped <paramref name="corners"/> with ground points inserted every <paramref name="step"/> yd where the chord misses; null when they would pass <paramref name="maxPoints"/>.</summary>
    private static List<Vector3>? TryInsertGroundPoints(
        MapCollision collision, Vector3 start, List<Vector3> corners, float step, float tolerance, float modelTolerance, int maxPoints, in GroundQuery q)
    {
        var result = new List<Vector3>(corners.Count + 8);
        var nodes = new List<(float At, Vector3 Point, float Tol)>(16);
        Vector3 prev = start;
        for (int i = 0; i < corners.Count; i++)
        {
            Vector3 corner = corners[i];
            float dx = corner.X - prev.X;
            float dy = corner.Y - prev.Y;
            float length = MathF.Sqrt((dx * dx) + (dy * dy));
            if (float.IsFinite(length) && length > step)
            {
                // The ground every `length / pieces` (<= step) yd along the segment, each looked for near the ground before it (a hill the chord
                // misses by more than the window is still followed); a sample with no floor is left out.
                int pieces = (int)MathF.Ceiling(length / step);
                nodes.Clear();
                nodes.Add((0f, prev, tolerance));
                for (int j = 1; j < pieces; j++)
                {
                    float at = length * j / pieces;
                    float t = at / length;
                    float x = prev.X + (dx * t);
                    float y = prev.Y + (dy * t);
                    (float lastAt, Vector3 last, _) = nodes[^1];
                    float expected = last.Z + ((corner.Z - last.Z) * ((at - lastAt) / (length - lastAt)));
                    float guardZ = at <= length - at ? prev.Z : corner.Z; // the nearer end of the whole segment: the floor a step-up is next to
                    if (TrySnap(collision, x, y, expected, guardZ, q, out float ground, out GroundSource source))
                    {
                        nodes.Add((at, new Vector3(x, y, ground), source is GroundSource.Model or GroundSource.Mesh ? modelTolerance : tolerance));
                    }
                }

                nodes.Add((length, corner, tolerance));

                // Keep a sample only where dropping it would put the polyline more than the tolerance off the ground at a sample: a sliding
                // window from the last kept point, widened while every sample it skips stays within the tolerance of its chord.
                int kept = 0;
                for (int end = 2; end < nodes.Count; end++)
                {
                    if (WindowFits(nodes, kept, end))
                    {
                        continue;
                    }

                    if (result.Count + 1 + (corners.Count - i) > maxPoints)
                    {
                        return null;
                    }

                    kept = end - 1;
                    result.Add(nodes[kept].Point);
                }
            }

            result.Add(corner);
            prev = corner;
        }

        return result;
    }

    /// <summary>Whether the chord from <c>nodes[from]</c> to <c>nodes[to]</c> passes within each node's own tolerance (in z) of every node between them.</summary>
    private static bool WindowFits(List<(float At, Vector3 Point, float Tol)> nodes, int from, int to)
    {
        (float fromAt, Vector3 a, _) = nodes[from];
        (float toAt, Vector3 b, _) = nodes[to];
        float span = toAt - fromAt;
        for (int k = from + 1; k < to; k++)
        {
            (float at, Vector3 p, float tolerance) = nodes[k];
            float chordZ = a.Z + ((b.Z - a.Z) * ((at - fromAt) / span));
            if (MathF.Abs(p.Z - chordZ) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Known(float height)
        => float.IsFinite(height) && height > TerrainTile.InvalidHeight;
}
