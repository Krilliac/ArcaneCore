using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;
using ShapeStats = ArcaneCore.World.Playerbots.PlayerbotPathShaping.ShapeStats;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// <see cref="PlayerbotPathShaping"/> on the real Stormwind Trade District mesh (vmangos MoveMapGen tiles, agent radius 0.2 yd): the routes
/// from the district toward the city gate west of it, whose string-pulled corners sit on the boundary of the mesh beside walls and doodads.
/// </summary>
public sealed class PlayerbotPathShapingRealTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotPathShapingRealTerrainTests(ITestOutputHelper output) : base(output) { }

    // The gate toward Elwynn (west of the district) and four places in the district (Z is a search height; the mesh surface is used).
    private static readonly Vector3 GateApproach = new(-8960f, 510f, 96.8f);

    private const float TightYards = 0.5f;
    private const float Probe = 0.6f;

    [RealTerrainBotTheory]
    [InlineData(-8900f, 560f)]
    [InlineData(-8880f, 580f)]
    [InlineData(-8880f, 600f)]
    [InlineData(-8860f, 600f)]
    [InlineData(-8860f, 640f)]
    [InlineData(-8820f, 660f)]
    [InlineData(-8840f, 660f)]
    [InlineData(-8800f, 420f)]
    [InlineData(-8780f, 460f)]
    [InlineData(-8860f, 720f)]
    public async Task RoutesFromTheTradeDistrictToTheGate_KeepTheirTightCornersClearOfTheObstacle_OrLeaveThemUnchanged(float fromX, float fromY)
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(new Vector3(fromX, fromY, 150f));
        Vector3 start = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, new Vector3(fromX, fromY, 96f)));
        await terrain.PlaceAsync(GateApproach with { Z = 150f });
        Vector3 goal = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, GateApproach));
        output.WriteLine($"start {start} goal {goal}");

        await terrain.PlaceAsync(start);
        PlayerbotRoute? unshaped = await PlanAsync(terrain, goal, new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 600, PathShaping = false });
        Assert.NotNull(unshaped);
        await terrain.PlaceAsync(start);
        PlayerbotRoute? shaped = await PlanAsync(terrain, goal, new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 600 });
        Assert.NotNull(shaped);

        await terrain.Host.OnWorldAsync(() =>
        {
            Map map = terrain.Player.Map!;
            var options = new PlayerbotOptions();
            var settings = PlayerbotPathShaping.Settings.From(options);
            IReadOnlyList<Vector3> input = unshaped!.Points;

            // The per-path cost, cold and warm, on exactly the points the hook shaped.
            ShapeStats cold = default;
            IReadOnlyList<Vector3> direct = PlayerbotPathShaping.ShapePoints(map, input, settings, ref cold);
            ShapeStats warm = default;
            PlayerbotPathShaping.ShapePoints(map, input, settings, ref warm);
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            output.WriteLine($"unshaped {input.Count} points {unshaped.Distance:F1} yd; shaped by the hook {shaped!.Points.Count} points {shaped.Distance:F1} yd");
            output.WriteLine($"cost cold {Ms(cold.ElapsedTicks):F3} ms, warm {Ms(warm.ElapsedTicks):F3} ms: considered {warm.CornersConsidered} shaped {warm.CornersShaped} "
                + $"rounded {warm.CornersRounded} fallbacks {warm.Fallbacks}; probes {warm.Probes} segments {warm.Segments} estimated queries {warm.Queries}");

            // The hook shaped the same points the direct call does.
            Assert.Equal(direct.Count, shaped.Points.Count);
            for (int i = 0; i < direct.Count; i++)
                Assert.True(Vector3.Distance(direct[i], shaped.Points[i]) < 0.01f, $"hook point {i} {shaped.Points[i]} != direct {direct[i]}");

            Assert.Equal(input[0], shaped.Points[0]);
            Assert.Equal(input[^1], shaped.Points[^1]);
            // Which output points stand for which corners: a corner the shaper left alone is in the output exactly; the output points between
            // two such anchors replace the corners between them.
            var outputSet = new HashSet<Vector3>(shaped.Points);
            var anchors = new List<int>();
            for (int i = 0; i < input.Count; i++)
                if (outputSet.Contains(input[i])) anchors.Add(i);
            Assert.Contains(0, anchors);
            Assert.Contains(input.Count - 1, anchors);

            // The legs the shaper made are judged by the shaper's own contract (SegmentAcceptable): strict, or no worse than the mesh leg on
            // the same side of the corner. (The mesh's own legs are not judged: stairs and ramps of the district fail the strict chord check.)
            (int newLegs, int relativeLegs) = CheckNewLegs(map, input, shaped.Points, anchors);
            output.WriteLine($"{newLegs} legs made by the shaper, all acceptable; {relativeLegs} fail the strict check and pass the relative one; "
                + $"the shaper reports {warm.RelativeSegments} relative segments (reversed chords {warm.ReversedChords})");
            output.WriteLine($"fallbacks: room {warm.FallbackRoom} ground {warm.FallbackGround} segment {warm.FallbackSegment}; partial pushes {warm.PartialPushes}; arcs rejected {warm.ArcRejected}; "
                + $"considered {warm.CornersConsidered}, queries {warm.Queries} of {PlayerbotPathShaping.MaxQueriesPerPath}");
            Assert.True(relativeLegs <= warm.RelativeSegments, $"{relativeLegs} emitted legs rely on the relative contract but the shaper reports {warm.RelativeSegments}");
            if (warm.RelativeSegments > 0)
                Assert.True(relativeLegs > 0, $"the shaper reports {warm.RelativeSegments} relative segments but no emitted leg needs the relative contract");

            float clearance = options.CornerClearanceYards;
            float accumulated = 0f;
            int tight = 0, shapedCorners = 0, unchanged = 0, needFallback = 0, exempt = 0, partial = 0, ordinal = 0;
            for (int i = 1; i < input.Count - 1; i++)
            {
                accumulated += Vector2.Distance(Flat(input[i - 1]), Flat(input[i]));
                if (!TryInward(input[i - 1], input[i], input[i + 1], out Vector2 inward, out float theta) || theta < PlayerbotPathShaping.MinShapeTurnRadians)
                    continue;
                bool shortLeg = Vector2.Distance(Flat(input[i - 1]), Flat(input[i])) < PlayerbotPathShaping.MinLegYards
                    || Vector2.Distance(Flat(input[i]), Flat(input[i + 1])) < PlayerbotPathShaping.MinLegYards;
                // The shaper looks at the corners that turn enough, have legs of a yard and lie within the horizon, in order, until its
                // query budget (or the corner cap) is used up; the corners it never reached are the ones after its CornersConsidered-th.
                bool eligible = accumulated <= PlayerbotRouteSmoothing.MaxSmoothHorizonYards && !shortLeg;
                if (eligible) ordinal++;
                if (InwardDistance(map, input[i], inward, Probe) >= TightYards) continue;
                tight++;

                if (!eligible)
                {
                    exempt++;
                    output.WriteLine($"corner {input[i]} theta {theta:F2} at {accumulated:F0} yd: beyond the shaping horizon or between short legs (not shaped by design)");
                    continue;
                }

                if (ordinal > warm.CornersConsidered)
                {
                    bool exhausted = warm.Queries >= PlayerbotPathShaping.MaxQueriesPerPath || warm.CornersShaped >= PlayerbotPathShaping.MaxShapedCornersPerPath;
                    output.WriteLine($"corner {input[i]} theta {theta:F2} at {accumulated:F0} yd: corner {ordinal} in the shaper's order, but it considered only {warm.CornersConsidered} "
                        + $"(queries {warm.Queries} of {PlayerbotPathShaping.MaxQueriesPerPath}, shaped {warm.CornersShaped} of {PlayerbotPathShaping.MaxShapedCornersPerPath}): not reached");
                    Assert.True(exhausted, "the shaper stopped before this corner without its query budget or corner cap being used up");
                    exempt++;
                    continue;
                }

                int outIndex = IndexOf(shaped.Points, input[i]);
                if (outIndex >= 0)
                {
                    // Left at its mesh position. That is allowed when the shaper reported a fallback, or when the shaper's own view of the corner
                    // (its predecessor is the last point it emitted, which a shaped corner before this one may have changed) is no tight turn.
                    unchanged++;
                    bool stillTight = TryInward(shaped.Points[outIndex - 1], input[i], input[i + 1], out Vector2 inward2, out float theta2)
                        && theta2 >= PlayerbotPathShaping.MinShapeTurnRadians
                        && Vector2.Distance(Flat(shaped.Points[outIndex - 1]), Flat(input[i])) >= PlayerbotPathShaping.MinLegYards
                        && InwardDistance(map, input[i], inward2, Probe) < TightYards;
                    if (stillTight) needFallback++;
                    output.WriteLine($"corner {input[i]} theta {theta:F2} at {accumulated:F0} yd: left unchanged ({(stillTight ? "a fallback" : "not tight in the shaper's own view")})");
                    continue;
                }

                // Shaped: the output points between the anchors around this corner stand for it; each keeps the clearance on the inside.
                int before = anchors.Last(a => a < i), after = anchors.First(a => a > i);
                int from = IndexOf(shaped.Points, input[before]) + 1, to = IndexOf(shaped.Points, input[after]);
                float worst = float.MaxValue, moved = 0f;
                Vector3 worstPoint = default;
                for (int k = from; k < to; k++)
                {
                    float ray = InwardDistance(map, shaped.Points[k], inward, clearance + 0.5f);
                    if (ray < worst) (worst, worstPoint) = (ray, shaped.Points[k]);
                    moved = MathF.Max(moved, Vector2.Distance(Flat(shaped.Points[k]), Flat(input[i])));
                }

                // The obstacle on the inside of the turn is InwardDistance(corner) from the corner, so a corner moved D yd from it keeps
                // min(clearance, D) - 0.05 even when the full push was not possible (a partial push, which the shaper must count).
                float obstacle = InwardDistance(map, input[i], inward, Probe);
                float required = MathF.Min(clearance, obstacle + moved) - 0.05f;
                if (worst < clearance - 0.05f) partial++;

                shapedCorners++;
                output.WriteLine($"corner {input[i]} theta {theta:F2} at {accumulated:F0} yd: replaced by {to - from} point(s), the worst has {worst:F2} yd of mesh on the inside of the turn at {worstPoint}");
                Assert.True(worst >= required, $"the shaped corner {worstPoint} (was {input[i]}) keeps only {worst:F2} yd to the obstacle on the inside of the turn (required {required:F2})");
            }

            output.WriteLine($"tight corners {tight}: shaped {shapedCorners}, left unchanged {unchanged} (of which fallbacks {needFallback}), exempt {exempt}; the shaper reports {warm.Fallbacks} fallbacks");
            Assert.True(tight - exempt >= 1, "the route has no tight corner within the shaping horizon; the test proves nothing");
            Assert.True(partial <= warm.PartialPushes, $"{partial} corners keep less than the clearance but the shaper counts only {warm.PartialPushes} partial pushes");
            Assert.True(needFallback <= warm.Fallbacks, $"{needFallback} tight corners were left unchanged but the shaper reports only {warm.Fallbacks} fallbacks");
            Assert.True(shapedCorners + unchanged + exempt == tight);
            Assert.True(shapedCorners >= 1 || warm.Fallbacks >= 1, "no tight corner was shaped and no fallback was reported");
            return true;
        });
    }

    /// <summary>
    /// Judge every leg of <paramref name="output"/> that is not a leg of <paramref name="input"/> by
    /// <see cref="PlayerbotPathShaping.SegmentAcceptable(Map, Vector3, Vector3, Vector3, Vector3, ref PlayerbotPathShaping.ShapeStats)"/> against
    /// the mesh leg on the same side of the corner it replaces: the points between two anchors (input points kept exactly) replace the
    /// corners between them, the legs in the first half belong to the incoming mesh leg, the rest to the outgoing one (as the shaper pairs them).
    /// </summary>
    private static (int NewLegs, int RelativeLegs) CheckNewLegs(Map map, IReadOnlyList<Vector3> input, IReadOnlyList<Vector3> output, List<int> anchors)
    {
        int newLegs = 0, relative = 0;
        for (int g = 1; g < anchors.Count; g++)
        {
            int before = anchors[g - 1], after = anchors[g];
            if (after == before + 1) continue;
            int from = IndexOf(output, input[before]);
            int to = IndexOf(output, input[after]);
            int replaced = to - from - 1;
            Vector3 inA = input[before], inB = input[before + 1], outA = input[after - 1], outB = input[after];
            for (int j = 0; j <= replaced; j++)
            {
                Vector3 a = output[from + j], b = output[from + j + 1];
                bool incoming = j <= replaced / 2;
                newLegs++;
                ShapeStats probe = default;
                bool strict = PlayerbotPathShaping.SegmentSafe(map, a, b);
                bool acceptable = incoming
                    ? PlayerbotPathShaping.SegmentAcceptable(map, a, b, inA, inB, ref probe)
                    : PlayerbotPathShaping.SegmentAcceptable(map, a, b, outA, outB, ref probe);
                Assert.True(acceptable, $"new leg {a} -> {b} ({(incoming ? "incoming" : "outgoing")} side of the corner) is not acceptable");
                if (!strict) relative++;
            }
        }

        return (newLegs, relative);
    }

    /// <summary>
    /// The gate corner the owner reported, on the real Trade District mesh: the route from (-8900, 560) to the gate approach turns at
    /// (-8921.6, 505.87), a boundary vertex of the mesh 1.18 rad wide. It is shaped (not left at the mesh vertex), every emitted point keeps
    /// the clearance from the obstacle and every new leg is acceptable.
    /// </summary>
    [RealTerrainBotFact]
    public async Task TheGateCornerOfTheOwnersRoute_IsShaped_AndItsPointsKeepTheClearance()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        Vector3 startNear = new(-8900f, 560f, 96f);
        await terrain.PlaceAsync(startNear with { Z = 150f });
        Vector3 start = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, startNear));
        await terrain.PlaceAsync(GateApproach with { Z = 150f });
        Vector3 goal = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, GateApproach));
        output.WriteLine($"start {start} goal {goal}");
        await terrain.PlaceAsync(start);
        PlayerbotRoute? unshaped = await PlanAsync(terrain, goal, new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 600, PathShaping = false });
        Assert.NotNull(unshaped);

        await terrain.Host.OnWorldAsync(() =>
        {
            Map map = terrain.Player.Map!;
            var options = new PlayerbotOptions();
            IReadOnlyList<Vector3> input = unshaped!.Points;
            int index = -1;
            for (int i = 1; i < input.Count - 1; i++)
                if (Vector2.Distance(Flat(input[i]), new Vector2(-8921.6f, 505.867f)) < 0.05f) index = i;
            Assert.True(index > 0, $"the route no longer turns at the gate corner (-8921.6, 505.867); points: {string.Join(" ", input)}");
            Vector3 prev = input[index - 1], v = input[index], next = input[index + 1];
            output.WriteLine($"gate corner {v}: prev {prev} next {next}");
            Assert.True(Vector2.Distance(Flat(prev), new Vector2(-8919.199f, 518.133f)) < 0.05f, $"prev is {prev}");
            Assert.True(Vector2.Distance(Flat(next), new Vector2(-8922.933f, 505.60f)) < 0.05f, $"next is {next}");

            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(options), ref stats);
            output.WriteLine($"considered {stats.CornersConsidered} shaped {stats.CornersShaped} rounded {stats.CornersRounded} fallbacks {stats.Fallbacks} "
                + $"(room {stats.FallbackRoom} ground {stats.FallbackGround} segment {stats.FallbackSegment}) partial pushes {stats.PartialPushes} relative segments {stats.RelativeSegments}");
            Assert.NotSame(input, shaped);

            // The original point must be gone from the output.
            foreach (Vector3 p in shaped)
                Assert.True(Vector2.Distance(Flat(p), Flat(v)) >= 0.05f, $"the output still holds the mesh corner {p}");

            var anchors = new List<int>();
            var outputSet = new HashSet<Vector3>(shaped);
            for (int i = 0; i < input.Count; i++)
                if (outputSet.Contains(input[i])) anchors.Add(i);
            int before = anchors.Last(a => a < index), after = anchors.First(a => a > index);
            int from = IndexOf(shaped, input[before]) + 1, to = IndexOf(shaped, input[after]);

            TryInward(prev, v, next, out Vector2 inward, out _);
            float obstacle = InwardDistance(map, v, inward, Probe);
            float moved = 0f, worst = float.MaxValue;
            for (int k = from; k < to; k++)
            {
                float ray = InwardDistance(map, shaped[k], inward, options.CornerClearanceYards + 0.5f);
                worst = MathF.Min(worst, ray);
                moved = MathF.Max(moved, Vector2.Distance(Flat(shaped[k]), Flat(v)));
                output.WriteLine($"emitted {shaped[k]}: {Vector2.Distance(Flat(shaped[k]), Flat(v)):F2} yd from the mesh corner, inward mesh ray {ray:F2} yd");
            }

            output.WriteLine($"obstacle {obstacle:F2} yd inside the mesh corner; push (farthest emitted point) {moved:F2}; D = obstacle + push = {obstacle + moved:F2}; "
                + $"worst inward distance {worst:F2}; relative segments {stats.RelativeSegments}");
            Assert.True(to - from >= 1, "no replacement point was emitted");
            Assert.True(worst >= options.CornerClearanceYards - 0.05f, $"an emitted point keeps only {worst:F2} yd inward mesh distance, expected >= {options.CornerClearanceYards - 0.05f}");
            (int newLegs, int relativeLegs) = CheckNewLegs(map, input, shaped, anchors);
            output.WriteLine($"{newLegs} legs made by the shaper, all acceptable; {relativeLegs} rely on the relative contract");
            Assert.True(relativeLegs <= stats.RelativeSegments);
            return true;
        });
    }

    private static async Task<PlayerbotRoute?> PlanAsync(Terrain terrain, Vector3 goal, PlayerbotOptions options)
    {
        PlayerbotRoute? route = null;
        for (int attempt = 0; attempt < 50 && route is null; attempt++)
        {
            route = await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(terrain.Player, goal, options, out PlayerbotRoute? r) ? r : null);
            if (route is null)
            {
                await Task.Delay(20);
                await terrain.Host.World.AdvanceClockAsync(100);
            }
        }

        return route;
    }

    private static bool IsInputLeg(IReadOnlyList<Vector3> input, Vector3 a, Vector3 b)
    {
        for (int i = 1; i < input.Count; i++)
            if (input[i - 1] == a && input[i] == b) return true;
        return false;
    }

    private static int IndexOf(IReadOnlyList<Vector3> points, Vector3 p)
    {
        for (int i = 0; i < points.Count; i++)
            if (points[i] == p) return i;
        return -1;
    }

    private static Vector2 Flat(Vector3 p) => new(p.X, p.Y);

    /// <summary>The direction into the inside of the turn at <paramref name="v"/> (toward where the obstacle is) and the flat turn angle.</summary>
    private static bool TryInward(Vector3 prev, Vector3 v, Vector3 next, out Vector2 inward, out float theta)
    {
        inward = default;
        theta = 0f;
        Vector2 a = Flat(v) - Flat(prev), b = Flat(next) - Flat(v);
        if (a.Length() < 1e-3f || b.Length() < 1e-3f) return false;
        a = Vector2.Normalize(a);
        b = Vector2.Normalize(b);
        theta = MathF.Acos(Math.Clamp(Vector2.Dot(a, b), -1f, 1f));
        Vector2 d = b - a;
        if (d.Length() < 1e-3f) return false;
        inward = Vector2.Normalize(d);
        return true;
    }

    /// <summary>How far the walkable mesh extends from <paramref name="from"/> along <paramref name="direction"/> (capped at <paramref name="length"/>).</summary>
    private static float InwardDistance(Map map, Vector3 from, Vector2 direction, float length)
    {
        MeshRaycastResult ray = map.Collision.MeshPoints!.Raycast(map.MapId, from,
            new Vector3(from.X + (direction.X * length), from.Y + (direction.Y * length), from.Z), PlayerbotMoveCheck.MeshOptions);
        Assert.True(ray.Available, $"the mesh cannot answer at {from}");
        return ray.Hit ? ray.Fraction * length : length;
    }

    private static Vector3 OnMesh(Map map, Vector3 near)
    {
        Assert.True(map.Collision.TryGetMeshFloor(near.X, near.Y, near.Z, 6f, 6f, out float surface), $"no mesh at {near}");
        return near with { Z = surface + 0.1f };
    }
}
