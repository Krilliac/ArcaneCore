using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using ShapeStats = ArcaneCore.World.Playerbots.PlayerbotPathShaping.ShapeStats;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Verification of <see cref="PlayerbotPathShaping"/> on synthetic walkable regions (a union of axis-aligned rectangles on a flat floor,
/// answered by <see cref="RegionWorld"/> as the line of sight, the pathfinder and the mesh point query at once). The expected values come
/// from the geometry of the region and from the documented constants of the shaper, never from running it.
/// </summary>
public sealed class PlayerbotPathShapingTests
{
    private const float Floor = 83.53f;
    private const float Tolerance = 0.05f;

    private readonly ITestOutputHelper _output;

    public PlayerbotPathShapingTests(ITestOutputHelper output) => _output = output;

    // An L: a horizontal leg and a vertical leg, each 8 yards wide. The inner wall corner (32, 8) is the string-pulled vertex.
    private static readonly Rect[] LShape = [new(0f, 0f, 40f, 8f), new(32f, 0f, 40f, 40f)];
    private static readonly Vector2 LStart = new(1f, 7.5f);
    private static readonly Vector2 LCorner = new(32f, 8f);
    private static readonly Vector2 LEnd = new(33f, 39f);

    [Fact]
    public async Task AnLCorner_IsMovedOffTheInnerWallCorner_ByTheClearance_AndStaysOnTheWalkableRegion()
    {
        await RunAsync(LShape, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("L corner", world, stats, shaped);

            Assert.NotSame(input, shaped);
            Assert.Equal(input[0], shaped[0]);
            Assert.Equal(input[^1], shaped[^1]);
            Vector2 wallCorner = at(LCorner).XY();
            float clearance = new PlayerbotOptions().CornerClearanceYards;
            for (int i = 1; i < shaped.Count - 1; i++)
            {
                float d = Vector2.Distance(shaped[i].XY(), wallCorner);
                Assert.True(d >= clearance - Tolerance, $"shaped point {i} {shaped[i]} is {d:F2} yd from the inner wall corner, expected >= {clearance}");
            }

            _output.WriteLine("L corner: nearest approach of the shaped legs to the wall corner "
                + $"{NearestApproach(shaped, at(LCorner).XY()):F2} yd (the unshaped legs touch it)");
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            Assert.True(stats.CornersShaped >= 1, "the corner was not shaped");
            return true;
        });
    }

    [Fact]
    public async Task ARoundedLCorner_NeverTurnsMoreThanTheHeadingStepBetweenSegments()
    {
        await RunAsync(LShape, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            float inputTurn = MaxTurn(input);
            Assert.True(inputTurn > 1.4f, $"the input must have a ~90 degree snap, got {inputTurn:F3} rad");

            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            float shapedTurn = MaxTurn(shaped);
            _output.WriteLine($"input max turn {inputTurn:F3} rad, shaped max turn {shapedTurn:F3} rad over {shaped.Count} points, rounded {stats.CornersRounded}");

            Assert.True(stats.CornersRounded >= 1, "the corner was not rounded");
            Assert.True(shapedTurn <= PlayerbotPathShaping.MaxHeadingStepRadians + 1e-3f,
                $"max heading change {shapedTurn:F3} rad exceeds {PlayerbotPathShaping.MaxHeadingStepRadians} + 1e-3");
            return true;
        });
    }

    [Fact]
    public async Task ARightAngleOnOpenGround_IsRoundedByAnArc_WhoseVertexTurnsStayWithinTheHeadingStep()
    {
        Rect[] open = [new(0f, 0f, 100f, 100f)];
        await RunAsync(open, (world, map, at) =>
        {
            Vector3[] input = [at(new(10f, 10f)), at(new(40f, 10f)), at(new(40f, 40f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            float shapedTurn = MaxTurn(shaped);
            LogCost("open right angle", world, stats, shaped);

            Assert.Equal(1, stats.CornersRounded);
            Assert.Equal(input[0], shaped[0]);
            Assert.Equal(input[^1], shaped[^1]);
            Assert.True(shaped.Count >= 5, $"expected the two tangent points and at least one arc point, got {shaped.Count} points");
            Assert.True(shapedTurn <= PlayerbotPathShaping.MaxHeadingStepRadians + 1e-3f, $"max heading change {shapedTurn:F3} rad");
            // The arc starts the default 2.5 yd before the corner: its first point is on the incoming leg 2.5 yd short of the corner.
            Assert.InRange(Vector2.Distance(shaped[1].XY(), at(new(40f, 10f)).XY()), 2.5f - Tolerance, 2.5f + Tolerance);
            Assert.InRange(Vector2.Distance(shaped[^2].XY(), at(new(40f, 10f)).XY()), 2.5f - Tolerance, 2.5f + Tolerance);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            return true;
        });
    }

    [Fact]
    public async Task AnLCornerWithAShortLead_IsRoundedAndPushedOffTheWall_WithinTheHeadingStep()
    {
        await RunAsync(LShape, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            var settings = new PlayerbotPathShaping.Settings(true, 2f, 0f, TurnLeadYards: 1f);
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
            LogCost("L corner short lead", world, stats, shaped);

            Assert.Equal(1, stats.CornersRounded);
            Assert.True(MaxTurn(shaped) <= PlayerbotPathShaping.MaxHeadingStepRadians + 1e-3f, $"max heading change {MaxTurn(shaped):F3} rad");
            // Every emitted point keeps the clearance from the wall corner, the two tangent points included.
            Assert.True(shaped.Count >= 5, $"expected the tangent points and at least one arc point, got {shaped.Count} points");
            for (int i = 1; i < shaped.Count - 1; i++)
                Assert.True(Vector2.Distance(shaped[i].XY(), at(LCorner).XY()) >= 2f - Tolerance, $"point {i} {shaped[i]} is too close to the wall corner");
            _output.WriteLine("distances to the wall corner: " + string.Join(" ", shaped.Select(p => Vector2.Distance(p.XY(), at(LCorner).XY()).ToString("F2"))));
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            return true;
        });
    }

    [Fact]
    public async Task ClearanceOnly_WithoutRoundingOrCentring_MovesTheCornerToTheClearanceAndKeepsThePointCount()
    {
        await RunAsync(LShape, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            var settings = new PlayerbotPathShaping.Settings(true, 2f, CentringMaxYards: 0f, TurnLeadYards: 0f);
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
            LogCost("L corner clearance only", world, stats, shaped);

            Assert.Equal(3, shaped.Count);
            Assert.Equal(0, stats.CornersRounded);
            // The wall corner is on the mesh boundary (distance 0 inside), so the push is exactly the clearance.
            float d = Vector2.Distance(shaped[1].XY(), at(LCorner).XY());
            Assert.InRange(d, 2f - Tolerance, 2f + Tolerance);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            return true;
        });
    }

    [Fact]
    public async Task ACornerBetweenTwoWalls_MovesTowardTheMiddleOfTheCorridor_NoFartherThanTheCentringLimit()
    {
        await RunAsync(LShape, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            var settings = new PlayerbotPathShaping.Settings(true, 2f, CentringMaxYards: 10f, TurnLeadYards: 0f);
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
            LogCost("L corner centring", world, stats, shaped);

            // Along the diagonal from the inner corner (32, 8) to the outer wall (about 11.2 yd away at the bottom edge) the middle is about 5.6 yd.
            float d = Vector2.Distance(shaped[1].XY(), at(LCorner).XY());
            Assert.InRange(d, 5.2f, 6.0f);
            Assert.True(d <= 10f + Tolerance);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            return true;
        });
    }

    [Fact]
    public async Task ADoorwayTurnAtTheJamb_NeverLeavesTheRegion_AndKeepsItsClearanceFromTheJamb_OrIsReportedAsAFallback()
    {
        // A hall (30 x 10) with a 1.5 yard wide doorway leading north from x 20..21.5; the string-pulled path turns at the west jamb (20, 10).
        Rect[] doorway = [new(0f, 0f, 30f, 10f), new(20f, 10f, 21.5f, 30f)];
        await RunAsync(doorway, (world, map, at) =>
        {
            Vector3[] input = [at(new(2f, 5f)), at(new(20f, 10f)), at(new(20.75f, 25f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("doorway", world, stats, shaped);

            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            Assert.Equal(input[0], shaped[0]);
            Assert.Equal(input[^1], shaped[^1]);
            Assert.True(stats.CornersShaped >= 1 || stats.Fallbacks >= 1, "the jamb corner was neither shaped nor reported as a fallback");
            if (stats.CornersShaped == 0)
            {
                Assert.Equal(input, shaped.ToArray());
                return true;
            }

            // Shaped: each emitted point keeps min(clearance, D) - 0.05 from the jamb, D being how far the corner was moved from it
            // (the jamb is on the boundary, so the obstacle is the jamb itself).
            Vector2 jamb = at(new(20f, 10f)).XY();
            float moved = 0f;
            for (int i = 1; i < shaped.Count - 1; i++) moved = MathF.Max(moved, Vector2.Distance(shaped[i].XY(), jamb));
            float required = MathF.Min(new PlayerbotOptions().CornerClearanceYards, moved) - Tolerance;
            for (int i = 1; i < shaped.Count - 1; i++)
                Assert.True(Vector2.Distance(shaped[i].XY(), jamb) >= required, $"point {i} {shaped[i]} is {Vector2.Distance(shaped[i].XY(), jamb):F2} yd from the jamb, expected >= {required:F2}");
            return true;
        });
    }

    [Fact]
    public async Task APushWhoseLegClipsAnObstacleNearThePreviousPoint_IsReducedToASmallerRung_AndCountedAsPartial()
    {
        // The horizontal leg is only one yard wide (y 7..8) west of the vertical leg. The full 2 yd push puts the corner where the leg
        // from the start dips under y 7 before the corner (the region ends there), a push of 1.5 still clips, a push of 1 does not.
        Rect[] region = [new(0f, 7f, 32f, 8f), new(32f, 0f, 40f, 40f)];
        await RunAsync(region, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            var settings = new PlayerbotPathShaping.Settings(true, 2f, CentringMaxYards: 0f, TurnLeadYards: 0f);
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
            LogCost("push ladder", world, stats, shaped);

            Assert.Equal(3, shaped.Count);
            float d = Vector2.Distance(shaped[1].XY(), at(LCorner).XY());
            _output.WriteLine($"the corner moved {d:F2} yd of the 2 yd asked");
            Assert.InRange(d, 0.1f, 2f - Tolerance);
            Assert.True(stats.PartialPushes >= 1, "a push smaller than the clearance was not counted as partial");
            Assert.Equal(0, stats.Fallbacks);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            return true;
        });
    }

    [Theory]
    [InlineData(2.5f)]
    [InlineData(1f)]
    public async Task ARoundedLCorner_KeepsEveryArcPointAndTangentPointClearOfTheWallCorner(float lead)
    {
        await RunAsync(LShape, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            var settings = new PlayerbotPathShaping.Settings(true, 2f, CentringMaxYards: 0f, TurnLeadYards: lead);
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
            LogCost($"L corner lead {lead}", world, stats, shaped);

            Assert.Equal(1, stats.CornersRounded);
            Assert.True(shaped.Count >= 5);
            Vector2 wall = at(LCorner).XY();
            float moved = 0f;
            for (int i = 1; i < shaped.Count - 1; i++) moved = MathF.Max(moved, Vector2.Distance(shaped[i].XY(), wall));
            float required = MathF.Min(2f, moved) - Tolerance;
            _output.WriteLine("distances to the wall corner: " + string.Join(" ", shaped.Select(p => Vector2.Distance(p.XY(), wall).ToString("F2"))) + $"; required {required:F2}");
            for (int i = 1; i < shaped.Count - 1; i++)
                Assert.True(Vector2.Distance(shaped[i].XY(), wall) >= required, $"point {i} {shaped[i]} is too close to the wall corner");
            Assert.True(MaxTurn(shaped) <= PlayerbotPathShaping.MaxHeadingStepRadians + 1e-3f);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            return true;
        });
    }

    [Fact]
    public async Task AnSBendThroughAGap_NeverLeavesTheRegion_EachCornerIsShapedWithClearanceOrReportedAsAFallback()
    {
        // East along a hall, north through a connector, east again: two consecutive corners turning opposite ways. The inner wall corners are
        // (22, 6) and (28, 14). The outcome of the pair is not pinned (the entry of the second corner is the shaper to improve): only
        // that nothing is unsafe and nothing is silently left alone.
        Rect[] sBend = [new(0f, 0f, 28f, 6f), new(22f, 0f, 28f, 20f), new(22f, 14f, 60f, 20f)];
        await RunAsync(sBend, (world, map, at) =>
        {
            Vector3[] input = [at(new(1f, 1f)), at(new(22f, 6f)), at(new(28f, 14f)), at(new(59f, 19f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("S bend", world, stats, shaped);

            Assert.Equal(input[0], shaped[0]);
            Assert.Equal(input[^1], shaped[^1]);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);

            int unchanged = 0;
            foreach (Vector2 wallLocal in new[] { new Vector2(22f, 6f), new Vector2(28f, 14f) })
            {
                Vector3 corner = at(wallLocal);
                if (shaped.Contains(corner))
                {
                    unchanged++;
                    continue;
                }

                Vector2 wall = corner.XY();
                float nearest = float.MaxValue, farthest = 0f;
                for (int i = 1; i < shaped.Count - 1; i++)
                {
                    float d = Vector2.Distance(shaped[i].XY(), wall);
                    // Only the points around this corner (the other wall corner is 10 yd away).
                    if (d > 6f) continue;
                    nearest = MathF.Min(nearest, d);
                    farthest = MathF.Max(farthest, d);
                }

                float required = (stats.PartialPushes > 0 ? MathF.Min(2f, farthest) : 2f) - Tolerance;
                _output.WriteLine($"wall corner {wallLocal}: nearest emitted point {nearest:F2} yd, required {required:F2}");
                Assert.True(nearest >= required, $"the shaped corner at {wallLocal} keeps only {nearest:F2} yd from the wall corner");
            }

            if (unchanged > 0)
                Assert.True(stats.Fallbacks >= unchanged, $"{unchanged} corners were left unchanged but only {stats.Fallbacks} fallbacks were reported");
            return true;
        });
    }

    [Fact]
    public async Task ABendInAOneAndAHalfYardWideCorridor_KeepsTheMeshCorner_NothingIsSqueezed()
    {
        // A bridge: 1.5 yards wide, turning north in the middle; the push to the clearance would leave less than the 0.5 yd edge margin.
        Rect[] bridge = [new(0f, 0f, 20f, 1.5f), new(18.5f, 0f, 20f, 20f)];
        await RunAsync(bridge, (world, map, at) =>
        {
            Vector3[] input = [at(new(1f, 0.75f)), at(new(19.25f, 0.75f)), at(new(19.25f, 19f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("bridge", world, stats, shaped);

            Assert.True(stats.Fallbacks >= 1, "the squeezed corner was not reported as a fallback");
            Assert.Equal(input, shaped.ToArray());
            AssertInside(world, shaped);
            return true;
        });
    }

    [Fact]
    public async Task AGentleBendBelowTheMinimumTurn_IsReturnedAsTheSameListWithoutAnyMeshQuery()
    {
        Rect[] open = [new(0f, 0f, 100f, 100f)];
        await RunAsync(open, (world, map, at) =>
        {
            // About 0.2 rad (11 degrees) < MinShapeTurnRadians.
            Vector3[] input = [at(new(0f, 50f)), at(new(30f, 50f)), at(new(60f, 56f))];
            world.ResetCounters();
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);

            Assert.Same(input, shaped);
            Assert.Equal(0, stats.CornersConsidered);
            Assert.Equal(0, world.TotalQueries);
            return true;
        });
    }

    [Fact]
    public async Task WithShapingDisabled_ThePointsAndTheRouteAreReturnedAsTheSameReference_WithoutQueries()
    {
        await RunAsync(LShape, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            world.ResetCounters();
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, new PlayerbotPathShaping.Settings(false, 2f, 4f, 2.5f), ref stats);
            Assert.Same(input, shaped);

            var route = new PlayerbotRoute(input, 60f, navigated: true);
            PlayerbotRoute off = PlayerbotPathShaping.Shape(map, route, new PlayerbotOptions { PathShaping = false });
            Assert.Same(route, off);
            Assert.Equal(0, world.TotalQueries);

            // On, the same route comes back as a new navigated route that carries the input's flags.
            var flagged = new PlayerbotRoute(input, 60f, navigated: true) { CompletedQuestReturn = true, Chase = true };
            PlayerbotRoute on = PlayerbotPathShaping.Shape(map, flagged, new PlayerbotOptions());
            Assert.NotSame(flagged, on);
            Assert.True(on.Navigated);
            Assert.True(on.CompletedQuestReturn);
            Assert.True(on.Chase);
            Assert.Equal(input[0], on.Points[0]);
            Assert.Equal(input[^1], on.Points[^1]);
            Assert.True(on.Distance > 0f && float.IsFinite(on.Distance));

            // A route that is not navigated is never shaped.
            var direct = new PlayerbotRoute(input, 60f, navigated: false);
            Assert.Same(direct, PlayerbotPathShaping.Shape(map, direct, new PlayerbotOptions()));
            return true;
        });
    }

    [Fact]
    public async Task PlanningALCorner_ThroughTheNavigationHook_KeepsTheClearanceOfTheShapedRoute()
    {
        await RunAsync(LShape, (world, map, at) => true, planner: async (world, host, session, at) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Vector3 start = at(LStart), end = at(LEnd);
                world.SetWaypoints([start, at(LCorner), end]);
                var options = new PlayerbotOptions { Enabled = true };

                float Closest(PlayerbotRoute route)
                {
                    float m = float.MaxValue;
                    for (int i = 1; i < route.Points.Count - 1; i++)
                        m = MathF.Min(m, Vector2.Distance(route.Points[i].XY(), at(LCorner).XY()));
                    return m;
                }

                player.Relocate(start.X, start.Y, start.Z, 0f, host.World.NowMs);
                Assert.True(PlayerbotNavigation.TryPlan(player, end, new PlayerbotOptions { Enabled = true, PathShaping = false }, out PlayerbotRoute? unshaped));
                Assert.NotNull(unshaped);
                float before = Closest(unshaped!);

                world.ResetCounters();
                player.Relocate(start.X, start.Y, start.Z, 0f, host.World.NowMs);
                Assert.True(PlayerbotNavigation.TryPlan(player, end, options, out PlayerbotRoute? route));
                Assert.NotNull(route);
                _output.WriteLine($"hook: {route!.Points.Count} points (unshaped {unshaped!.Points.Count}); nearest interior point to the wall corner {before:F2} -> {Closest(route):F2} yd; "
                    + $"fake queries for the whole plan {world.TotalQueries}");

                Assert.True(before < 2f - Tolerance, $"the unshaped route already keeps {before:F2} yd; the test proves nothing");
                Assert.True(route.Navigated);
                Assert.False(route.Points.SequenceEqual(unshaped.Points), "the hook did not change the route");
                float clearance = options.CornerClearanceYards;
                Assert.True(Closest(route) >= clearance - Tolerance, $"the planned route passes {Closest(route):F2} yd from the wall corner, expected >= {clearance}");
                AssertInside(world, route.Points);
                AssertSegmentsSafe(player.Map!, route.Points);
                Assert.Equal(end, route.Points[^1]);
                return true;
            });
        });
    }

    [Fact]
    public async Task AZigzagWithManyCorners_StaysWithinTheShapedCornerCap_AndTheMeasuredQueryBudget()
    {
        Rect[] open = [new(0f, 0f, 300f, 300f)];
        await RunAsync(open, (world, map, at) =>
        {
            var input = new List<Vector3>();
            for (int i = 0; i <= 20; i++)
                input.Add(at(new(10f + (6f * i), 100f + ((i % 2) * 6f))));
            Assert.True(input.Count - 2 > 12, "the zigzag needs more than 12 corners");

            world.ResetCounters();
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("zigzag", world, stats, shaped);

            Assert.True(stats.CornersShaped >= 1);
            Assert.True(stats.CornersShaped <= PlayerbotPathShaping.MaxShapedCornersPerPath, $"{stats.CornersShaped} corners shaped");
            Assert.True(stats.Queries <= PlayerbotPathShaping.MaxQueriesPerPath + 40, $"{stats.Queries} estimated queries (the budget plus one chord check)");
            Assert.True(world.TotalQueries <= 2 * PlayerbotPathShaping.MaxQueriesPerPath, $"{world.TotalQueries} counted fake queries for one path");
            Assert.True(shaped.Count <= PlayerbotPathShaping.MaxShapedPoints, $"{shaped.Count} points");
            Assert.Equal(input[0], shaped[0]);
            Assert.Equal(input[^1], shaped[^1]);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);

            // The process-wide totals (other tests shape in parallel, so only growth is asserted) include this run.
            (long pathsBefore, long cornersBefore, _, long queriesBefore, long ticksBefore) = PlayerbotPathShaping.ReadTotals();
            ShapeStats again = default;
            PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref again);
            (long paths, long cornersShaped, _, long queries, long ticks) = PlayerbotPathShaping.ReadTotals();
            Assert.True(paths - pathsBefore >= 1);
            Assert.True(cornersShaped - cornersBefore >= again.CornersShaped);
            Assert.True(queries - queriesBefore >= again.Queries);
            Assert.True(ticks - ticksBefore >= again.ElapsedTicks);
            return true;
        });
    }

    private const float HeadingTolerance = 1e-3f;

    /// <summary>The distance the documented clearance bound is pinned at along the legs (not claimed by the docs, which state it for points).</summary>
    private const float ChordClearanceFloor = 1.5f;

    [Fact]
    public async Task AnSBendWithWallsAtBothInnerCorners_NeverTurnsMoreThanTheHeadingStep_AndTheSecondPushIsCappedByTheFirstArc()
    {
        // East, then north through a connector, then east: two opposite corners 6.4 yd apart, a wall corner on the inside of each.
        // Before the neighbour cap the second corner's push rotated the leg from the first arc's last tangent point and raised that
        // vertex to 0.6071 rad.
        Rect[] region = [new(-1f, -10f, 10f, 0f), new(10f, -10f, 14f, 15f), new(14f, 5f, 26f, 15f)];
        await RunAsync(region, (world, map, at) =>
        {
            Vector3[] input = [at(new(0f, 0f)), at(new(10f, 0f)), at(new(14f, 5f)), at(new(24f, 5f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("heading S bend", world, stats, shaped);
            float turn = MaxTurn(shaped);
            _output.WriteLine($"max turn {turn:F4} rad, neighbour caps {stats.NeighbourCaps}, splits {stats.NeighbourSplits}");

            Assert.True(turn <= PlayerbotPathShaping.MaxHeadingStepRadians + HeadingTolerance, $"max heading change {turn:F4} rad");
            Assert.True(stats.NeighbourCaps >= 1, "the second push was not capped by the first corner's arc");
            Vector2 wall = at(new(14f, 5f)).XY();
            for (int i = 1; i < shaped.Count - 1; i++)
                Assert.True(Vector2.Distance(shaped[i].XY(), wall) >= 2f - Tolerance, $"point {i} {shaped[i]} is {Vector2.Distance(shaped[i].XY(), wall):F2} yd from the wall corner");
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            Assert.Equal(input[0], shaped[0]);
            Assert.Equal(input[^1], shaped[^1]);
            return true;
        });
    }

    [Fact]
    public async Task AShortSBend_InsertsASplitPoint_KeepsTheHeadingStep_AndTheVertexClearance()
    {
        // The connector is 3 yd wide and the legs 4.6 yd long: capping the second push would kill its arc, so a split point one yard
        // along the incoming leg takes the turn instead. Before: 0.6857 rad.
        Rect[] region = [new(-1f, -10f, 10f, 0f), new(10f, -10f, 13f, 15f), new(13f, 3.5f, 26f, 15f)];
        await RunAsync(region, (world, map, at) =>
        {
            Vector3[] input = [at(new(0f, 0f)), at(new(10f, 0f)), at(new(13f, 3.5f)), at(new(24f, 3.5f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("short heading S bend", world, stats, shaped);
            float turn = MaxTurn(shaped);
            Vector2 wall1 = at(new(10f, 0f)).XY(), wall2 = at(new(13f, 3.5f)).XY();
            float chord = NearestApproach(shaped, wall2);
            float vertex = shaped.Skip(1).Take(shaped.Count - 2).Min(p => MathF.Min(Vector2.Distance(p.XY(), wall1), Vector2.Distance(p.XY(), wall2)));
            _output.WriteLine($"max turn {turn:F4} rad, splits {stats.NeighbourSplits}, nearest vertex to a wall corner {vertex:F3} yd, nearest approach of a leg to the second wall corner {chord:F3} yd");

            Assert.True(turn <= PlayerbotPathShaping.MaxHeadingStepRadians + HeadingTolerance, $"max heading change {turn:F4} rad");
            Assert.Equal(1, stats.NeighbourSplits);
            // The documentation states the clearance for the shaper's points (the moved corner, the arc points, the split point), so that is what is asserted.
            for (int i = 1; i < shaped.Count - 1; i++)
                Assert.True(MathF.Min(Vector2.Distance(shaped[i].XY(), wall1), Vector2.Distance(shaped[i].XY(), wall2)) >= 2f - Tolerance, $"point {i} {shaped[i]} is too close to a wall corner");
            // The legs are not covered by that claim: the leg from the split point to the first tangent point passes the wall corner at
            // 1.57 yd. That is pinned as the real bound (a regression of the leg geometry shows here) and reported, not hidden.
            Assert.True(chord >= ChordClearanceFloor, $"a leg passes {chord:F3} yd from the wall corner, the pinned floor is {ChordClearanceFloor}");
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            return true;
        });
    }

    [Fact]
    public async Task AHairpin_UsesEnoughArcPoints_ToKeepTheHeadingStep()
    {
        // A turn of nearly pi: with four interior points the step was theta / 5 > 0.6 (0.6084 rad).
        Rect[] open = [new(-20f, -20f, 40f, 40f)];
        await RunAsync(open, (world, map, at) =>
        {
            Vector3[] input = [at(new(0f, 0f)), at(new(10f, 0f)), at(new(0f, 1f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("hairpin", world, stats, shaped);
            float turn = MaxTurn(shaped);
            _output.WriteLine($"input turn {MaxTurn(input):F3}, shaped max turn {turn:F4} rad over {shaped.Count} points");

            Assert.True(MaxTurn(input) > 3.0f);
            Assert.Equal(1, stats.CornersRounded);
            Assert.True(turn <= PlayerbotPathShaping.MaxHeadingStepRadians + HeadingTolerance, $"max heading change {turn:F4} rad");
            Assert.Equal(input[0], shaped[0]);
            Assert.Equal(input[^1], shaped[^1]);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);
            return true;
        });
    }

    [Fact]
    public async Task AGentleCornerWhosePushWouldWidenItPastTheStep_IsRoundedToo_AndNotWithoutALead()
    {
        // A 0.46 rad corner on a wall corner at the edge of a corridor: not sharper than the step, but its push (a bounded corridor
        // centres it too) turned it to 1.5152 rad before.
        Rect[] region = [new(-1f, -8f, 6f, 0f), new(6f, -8f, 16f, 8f)];
        await RunAsync(region, (world, map, at) =>
        {
            Vector3[] input = [at(new(0f, 0f)), at(new(6f, 0f)), at(new(11f, 2.5f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("gentle corner", world, stats, shaped);
            float turn = MaxTurn(shaped);
            _output.WriteLine($"input turn {MaxTurn(input):F3}, shaped max turn {turn:F4} rad, auto-rounded {stats.AutoRounded}");

            Assert.True(MaxTurn(input) <= PlayerbotPathShaping.MaxHeadingStepRadians);
            Assert.True(turn <= PlayerbotPathShaping.MaxHeadingStepRadians + HeadingTolerance, $"max heading change {turn:F4} rad");
            Assert.True(stats.AutoRounded >= 1, "the widened corner was not rounded");
            Assert.Equal(1, stats.CornersRounded);
            AssertInside(world, shaped);
            AssertSegmentsSafe(map, shaped);

            // With no lead there is no rounding and no heading claim: the machinery is off.
            ShapeStats off = default;
            IReadOnlyList<Vector3> noLead = PlayerbotPathShaping.ShapePoints(map, input, new PlayerbotPathShaping.Settings(true, 2f, 4f, TurnLeadYards: 0f), ref off);
            Assert.Equal(0, off.AutoRounded);
            Assert.Equal(0, off.NeighbourCaps + off.NeighbourSplits + off.SelfCaps + off.FallbackHeading);
            Assert.Equal(0, off.CornersRounded);
            Assert.Equal(3, noLead.Count);
            return true;
        });
    }

    [Fact]
    public async Task ATwoCornerPathInTheSameDirection_IsBudgetBound_NotCappedOrSplit()
    {
        // BUDGET-REACH FOLLOW-UP: east, north, west along a C. With the default query budget the first corner's arc uses up the path
        // (190 estimated queries) and the second corner is left unrounded by a budget stop; the neighbour cap and the split point never
        // run. When the budget reaches the second corner this test should be tightened to the heading bound (design: max 0.5524 rad).
        Rect[] region = [new(0f, 0f, 30f, 8f), new(22f, 0f, 30f, 30f), new(0f, 22f, 30f, 30f)];
        await RunAsync(region, (world, map, at) =>
        {
            Vector3[] input = [at(new(1f, 7.5f)), at(new(22f, 8f)), at(new(22f, 22f)), at(new(1f, 22.5f))];
            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("same-direction pair", world, stats, shaped);
            _output.WriteLine($"budget stops {stats.BudgetStops}, budget fallbacks {stats.FallbackBudget}, caps {stats.NeighbourCaps}, splits {stats.NeighbourSplits}, max turn {MaxTurn(shaped):F4}");

            Assert.Equal(0, stats.NeighbourCaps);
            Assert.Equal(0, stats.NeighbourSplits);
            Assert.True(stats.BudgetStops > 0 || stats.FallbackBudget > 0, "the second corner's outcome is not a budget stop");
            Assert.Equal(1, stats.CornersRounded);
            Assert.Equal(input[0], shaped[0]);
            Assert.Equal(input[^1], shaped[^1]);
            AssertInside(world, shaped);
            return true;
        });
    }

    [Fact]
    public void ThePushThatKeepsTheNeighbourWithinTheHeadingStep_MatchesABruteForceOfTheTurn()
    {
        const float allowedBound = PlayerbotPathShaping.MaxHeadingStepRadians - 1e-3f;
        const float slack = 0.3f;
        int finite = 0, infinite = 0, zero = 0;
        for (int ti = 0; ti <= 12; ti++)
        {
            float theta = 0.4f + (ti * 0.2f);
            for (int k = 0; k <= 24; k++)
            {
                float tauOld = -1.2f + (k * 0.1f);

                // The allowance jumps by 0.3 rad at |tau| = MaxHeadingStepRadians by design; a grid point on that edge lands on either side
                // depending on the libm rounding of cos/sin/atan2, so it is not a stable check.
                if (MathF.Abs(MathF.Abs(tauOld) - PlayerbotPathShaping.MaxHeadingStepRadians) < 1e-3f)
                    continue;

                foreach (float side in new[] { 1f, -1f })
                {
                    // prevPrev -> prev along +x, prev -> v at heading tauOld (10 yd), the corner at v turns by side * theta.
                    var prevPrev = new Vector3(-10f, 0f, 0f);
                    var prev = new Vector3(0f, 0f, 0f);
                    var dirIn = new Vector2(MathF.Cos(tauOld), MathF.Sin(tauOld));
                    var v = new Vector3(10f * dirIn.X, 10f * dirIn.Y, 0f);
                    float outHeading = tauOld + (side * theta);
                    var dirOut = new Vector2(MathF.Cos(outHeading), MathF.Sin(outHeading));
                    Vector2 outward = -Vector2.Normalize(dirOut - dirIn);

                    // The branch comes from the turn of the built points (atan2, as production computes it), not from the loop variable.
                    float builtTurn = MathF.Abs(SignedTurn2(prevPrev.XY(), prev.XY(), v.XY()));
                    float allowed = builtTurn <= PlayerbotPathShaping.MaxHeadingStepRadians ? allowedBound : builtTurn + slack - 1e-3f;
                    float pMax = PlayerbotPathShaping.MaxPushForNeighbour(prevPrev, prev, v, dirIn, outward, theta);
                    string at = $"theta {theta:F2} tauOld {tauOld:F2} (k {k}, built turn {builtTurn:F6}) side {side}: pMax {pMax}";

                    float TurnAtPrev(float push) => Turn2(prevPrev.XY(), prev.XY(), v.XY() + (outward * push));
                    if (float.IsPositiveInfinity(pMax))
                    {
                        infinite++;
                        foreach (float push in new[] { 5f, 50f, 1000f })
                            Assert.True(TurnAtPrev(push) <= allowed + 1e-4f, $"{at}: push {push} turns the neighbour {TurnAtPrev(push):F4} > {allowed:F4}");
                    }
                    else if (pMax == 0f)
                    {
                        zero++;
                        foreach (float push in new[] { 0.01f, 0.5f, 3f })
                            Assert.True(TurnAtPrev(push) > allowed - 1e-4f, $"{at}: push {push} still keeps the neighbour at {TurnAtPrev(push):F4} <= {allowed:F4}");
                    }
                    else
                    {
                        finite++;
                        Assert.True(pMax > 0f, at);
                        float residual = TurnAtPrev(pMax) - allowed;
                        Assert.True(residual >= -1e-4f && residual <= 1e-4f, $"{at}: turn at the cap minus allowed = {residual}");
                        Assert.True(TurnAtPrev(pMax * 0.5f) <= allowed + 1e-4f, $"{at}: half of it already exceeds");
                        Assert.True(TurnAtPrev((pMax * 1.2f) + 0.05f) > allowed, $"{at}: a larger push does not exceed");
                    }
                }
            }
        }

        _output.WriteLine($"grid: {finite} finite caps, {infinite} unbounded, {zero} zero");
        Assert.True(finite > 50 && zero > 0 && infinite > 0, "the grid must exercise all three cases");
    }

    private static float SignedTurn2(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 u = Vector2.Normalize(b - a), w = Vector2.Normalize(c - b);
        return MathF.Atan2((u.X * w.Y) - (u.Y * w.X), Vector2.Dot(u, w));
    }

    private static float Turn2(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 u = Vector2.Normalize(b - a), w = Vector2.Normalize(c - b);
        return MathF.Acos(Math.Clamp(Vector2.Dot(u, w), -1f, 1f));
    }

    [Fact]
    public async Task ASinglePathCostsAFewDozenQueries_AtAnLCorner()
    {
        await RunAsync(LShape, (world, map, at) =>
        {
            Vector3[] input = [at(LStart), at(LCorner), at(LEnd)];
            world.ResetCounters();
            ShapeStats stats = default;
            PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost("L corner cost", world, stats, input);
            Assert.True(world.TotalQueries is > 0 and <= PlayerbotPathShaping.MaxQueriesPerPath, $"{world.TotalQueries} counted fake queries for one corner");
            Assert.True(stats.Probes >= 2, "the shaper must probe the inside and the outside of the turn");
            return true;
        });
    }

    [Theory]
    [InlineData(0.5f, 0f, 0f)]
    [InlineData(5f, 10f, 6f)]
    public void TheDocumentedBoundsOfTheShapingOptions_AreAccepted(float clearance, float centring, float lead)
    {
        var options = new PlayerbotOptions { CornerClearanceYards = clearance, CornerCentringMaxYards = centring, CornerTurnLeadYards = lead };
        options.Validate();
        var settings = PlayerbotPathShaping.Settings.From(options);
        Assert.Equal(new PlayerbotPathShaping.Settings(true, clearance, centring, lead), settings);
        Assert.True(new PlayerbotOptions().PathShaping);
    }

    /// <summary>A floor that is flat except for small raised squares (centre, height): the legs whose floor samples land on one of them have a floor mismatch of that height.</summary>
    private sealed class Bumps
    {
        public List<(Vector2 Centre, float Height)> Items { get; } = [];

        public float Floor(float x, float y)
        {
            float z = PlayerbotPathShapingTests.Floor;
            foreach ((Vector2 c, float h) in Items)
            {
                if (MathF.Abs(x - c.X) <= 0.15f && MathF.Abs(y - c.Y) <= 0.15f) z += h;
            }

            return z;
        }
    }

    /// <summary>The first floor sample the chord check takes on the leg a to b (every 3 yards: the point at 1/n of the way, n = flat length / 3 rounded up).</summary>
    private static Vector2 FirstSample(Vector3 a, Vector3 b)
    {
        int n = (int)MathF.Min(MathF.Ceiling(Vector2.Distance(a.XY(), b.XY()) / 3f), 1000f);
        Assert.True(n >= 2, "the leg is too short to have a floor sample");
        return Vector2.Lerp(a.XY(), b.XY(), 1f / n);
    }

    [Fact]
    public async Task ALegBesideAShapedNeighbour_IsJudgedAgainstTheRealMeshLeg_NotAgainstTheLegOfTheShapedPrevious()
    {
        // An S-bend of two consecutive pushed corners v1 (8, 6) and v2 (14, 10) on a floor with raised squares at the floor samples of
        // three legs: the mesh leg v1 -> v2 (0.9 yd of floor mismatch), the leg v1' -> v2 from the pushed first corner to the mesh's
        // second (1.0), and the leg v1' -> v2' to the pushed second corner (1.2). All fail the strict check (0.75). The leg v1' -> v2'
        // is 0.2 yd worse than the leg before it, within the 0.25 slack of the shaped previous leg, and 0.3 yd worse than the real mesh
        // leg, over the slack: it must not be accepted, and the second corner must not be pushed where that leg ends.
        Rect[] region = [new(0f, 0f, 14f, 6f), new(8f, 0f, 14f, 16f), new(8f, 10f, 40f, 16f)];
        var bumps = new Bumps();
        await RunAsync(region, (world, map, at) =>
        {
            Vector2 offset = at(Vector2.Zero).XY();
            Vector3[] input = [at(new(1f, 5.5f)), at(new(8f, 6f)), at(new(14f, 10f)), at(new(39f, 10.5f))];
            var settings = new PlayerbotPathShaping.Settings(true, 2f, CentringMaxYards: 0f, TurnLeadYards: 0f);

            // On the flat floor both corners are pushed by the full clearance.
            ShapeStats flatStats = default;
            IReadOnlyList<Vector3> flat = PlayerbotPathShaping.ShapePoints(map, input, settings, ref flatStats);
            Assert.Equal(4, flat.Count);
            Vector3 v1Pushed = flat[1], v2Pushed = flat[2];
            Assert.True(Vector2.Distance(v1Pushed.XY(), input[1].XY()) > 1.9f && Vector2.Distance(v2Pushed.XY(), input[2].XY()) > 1.9f, "both corners must be pushed on the flat floor");

            bumps.Items.Add((FirstSample(input[1], input[2]) - offset, 0.9f));
            bumps.Items.Add((FirstSample(v1Pushed, input[2]) - offset, 1.0f));
            bumps.Items.Add((FirstSample(v1Pushed, v2Pushed) - offset, 1.2f));

            ShapeStats m = default;
            float mesh = PlayerbotPathShaping.MeasureLeg(map, input[1], input[2], ref m).MaxFloorMismatchYards;
            float previous = PlayerbotPathShaping.MeasureLeg(map, v1Pushed, input[2], ref m).MaxFloorMismatchYards;
            float shapedLeg = PlayerbotPathShaping.MeasureLeg(map, v1Pushed, v2Pushed, ref m).MaxFloorMismatchYards;
            _output.WriteLine($"floor mismatch: mesh leg {mesh:F2}, leg from the shaped previous {previous:F2}, shaped leg {shapedLeg:F2}");
            Assert.InRange(mesh, 0.89f, 0.91f);
            Assert.InRange(previous, 0.99f, 1.01f);
            Assert.InRange(shapedLeg, 1.19f, 1.21f);
            Assert.False(PlayerbotPathShaping.SegmentSafe(map, input[1], input[2]));
            Assert.False(PlayerbotPathShaping.SegmentSafe(map, v1Pushed, v2Pushed));

            // The contract: against the shaped previous leg the 1.2 would pass (1.0 + 0.25), against the real mesh leg it does not (0.9 + 0.25).
            ShapeStats probe = default;
            Assert.True(PlayerbotPathShaping.SegmentAcceptable(map, v1Pushed, v2Pushed, v1Pushed, input[2], ref probe));
            Assert.False(PlayerbotPathShaping.SegmentAcceptable(map, v1Pushed, v2Pushed, input[1], input[2], ref probe));

            ShapeStats stats = default;
            IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
            LogCost("height step S bend", world, stats, shaped);
            foreach (Vector3 p in shaped)
                Assert.True(Vector2.Distance(p.XY(), v2Pushed.XY()) > 0.05f, $"the output holds {p}, the second corner pushed where its leg is 0.3 yd worse than the mesh leg");
            // The first corner is unaffected: its leg to the mesh's second corner is 0.1 yd worse than the mesh leg (relative contract).
            Assert.True(Vector2.Distance(shaped[1].XY(), v1Pushed.XY()) < 0.05f, $"the first corner moved to {shaped[1]}");
            Assert.True(stats.RelativeSegments >= 1, "the first corner's leg should have been accepted by the relative contract");
            // The second corner is held to what the mesh leg allows: a smaller push (a partial one) or its mesh position, never the full push.
            Assert.True(stats.PartialPushes >= 1 || (stats.Fallbacks >= 1 && Vector2.Distance(shaped[2].XY(), input[2].XY()) < 0.05f),
                "the second corner was neither partially pushed nor reported as a fallback");
            AssertInside(world, shaped);
            return true;
        }, floorAt: (x, y) => bumps.Floor(x, y));
    }

    [Theory]
    [InlineData(39.5f, 39.5f, true)]
    [InlineData(40.5f, 10f, false)]
    [InlineData(10f, 40.5f, false)]
    public async Task ACornerWithALegLongerThanTheShapedLegLimit_IsNotShaped_AndNothingIsProbed(float lenIn, float lenOut, bool shaped)
    {
        // An L with 8 yd wide legs, the corner on the inner wall corner (the string-pulled vertex), legs of the given lengths.
        Rect[] region = [new(0f, 0f, 90f, 8f), new(82f, 0f, 90f, 90f)];
        Assert.Equal(40f, PlayerbotPathShaping.MaxShapedLegYards);
        await RunAsync(region, (world, map, at) =>
        {
            Vector2 corner = new(82f, 8f);
            Vector3[] input = [at(new(corner.X - lenIn, 7.5f)), at(corner), at(new(corner.X + 1f, corner.Y + lenOut))];
            world.ResetCounters();
            ShapeStats stats = default;
            IReadOnlyList<Vector3> result = PlayerbotPathShaping.ShapePoints(map, input, PlayerbotPathShaping.Settings.From(new PlayerbotOptions()), ref stats);
            LogCost($"legs {lenIn} and {lenOut}", world, stats, result);

            if (shaped)
            {
                Assert.NotSame(input, result);
                Assert.Equal(1, stats.CornersConsidered);
                Assert.Equal(1, stats.CornersShaped);
            }
            else
            {
                // Not considered at all: no probe, no fallback of any reason, no budget stop; the list comes back untouched.
                Assert.Same(input, result);
                Assert.Equal(0, stats.CornersConsidered);
                Assert.Equal(0, stats.Probes);
                Assert.Equal(0, stats.Queries);
                Assert.Equal(0, stats.Fallbacks);
                Assert.Equal(0, stats.FallbackBudget);
                Assert.Equal(0, stats.BudgetStops);
                Assert.Equal(0, world.TotalQueries);
            }

            return true;
        });
    }

    [Fact]
    public async Task ARoundedCornerWhoseArcLegIsNotWalkable_StaysAtItsMeshPosition_AndIsCountedAsAnArcFallback()
    {
        // Open ground, a right angle at (30, 0) with 30 yd legs: it is rounded (push 0, an arc from 2.5 yd before the corner). A raised
        // square under the first floor sample of the leg to the arc's first tangent point (27.5, 0) - at 2.75 yd, not at a sample of the
        // mesh leg (3, 6, ...) - makes that leg fail the strict check while the mesh leg passes it, so the arc is rejected.
        Rect[] open = [new(-10f, -10f, 100f, 100f)];
        var bumps = new Bumps();
        await RunAsync(open, (world, map, at) =>
        {
            Vector2 offset = at(Vector2.Zero).XY();
            Vector3[] input = [at(new(0f, 0f)), at(new(30f, 0f)), at(new(30f, 30f))];
            var settings = PlayerbotPathShaping.Settings.From(new PlayerbotOptions());

            ShapeStats clear = default;
            IReadOnlyList<Vector3> rounded = PlayerbotPathShaping.ShapePoints(map, input, settings, ref clear);
            Assert.Equal(1, clear.CornersRounded);
            Assert.Equal(0, clear.FallbackArc);

            bumps.Items.Add((new Vector2(2.75f, 0f), 1.0f));
            Assert.True(PlayerbotPathShaping.SegmentSafe(map, input[0], input[1]), "the mesh leg must pass the strict check");
            Assert.False(PlayerbotPathShaping.SegmentSafe(map, input[0], at(new(27.5f, 0f))), "the leg to the tangent point must fail it");

            ShapeStats stats = default;
            IReadOnlyList<Vector3> result = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
            LogCost("arc fallback", world, stats, result);
            Assert.Same(input, result);
            Assert.Equal(1, stats.CornersConsidered);
            Assert.Equal(0, stats.CornersShaped);
            Assert.Equal(1, stats.Fallbacks);
            Assert.Equal(1, stats.FallbackArc);
            Assert.True(stats.ArcRejected >= 1);
            Assert.Equal(0, stats.FallbackBudget);
            Assert.Equal(0, stats.FallbackRoom + stats.FallbackGround + stats.FallbackSegment);
            return true;
        }, floorAt: (x, y) => bumps.Floor(x, y));
    }

    [Fact]
    public async Task ProbeSweep_OpenPlazaTurnAngles()
    {
        await RunAsync([new Rect(-60f, -60f, 60f, 60f)], (world, map, at) =>
        {
            const float A = PlayerbotPathShaping.MaxHeadingStepRadians;
            var settings = PlayerbotPathShaping.Settings.From(new PlayerbotOptions());
            int over = 0, created = 0, rounded = 0; float worst = 0f; string sample = "";
            float[] legs = [3f, 4f, 6.25f, 10f, 20f];
            foreach (float leg in legs)
            {
                for (float th = 0.62f; th < 3.10f; th += 0.0007f)
                {
                    for (int rot = 0; rot < 3; rot++)
                    {
                        float phi = rot * 0.7371f;
                        Vector2 d0 = new(MathF.Cos(phi), MathF.Sin(phi));
                        Vector2 d1 = new(MathF.Cos(phi + th), MathF.Sin(phi + th));
                        List<Vector3> input = [at(-d0 * leg), at(Vector2.Zero), at(d1 * leg)];
                        ShapeStats stats = default;
                        IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
                        rounded += stats.CornersRounded;
                        for (int j = 1; j < shaped.Count - 1; j++)
                        {
                            if (input.Contains(shaped[j])) continue;
                            created++;
                            float turn = Turn2(shaped[j - 1].XY(), shaped[j].XY(), shaped[j + 1].XY());
                            worst = MathF.Max(worst, turn);
                            if (turn > A + 1e-4f)
                            {
                                over++;
                                if (sample.Length < 2000)
                                    sample += $"leg {leg} th {th:F4} rot {rot} v{j}/{shaped.Count} turn {turn:F5}\n";
                            }
                        }
                    }
                }
            }
            _output.WriteLine($"created {created} rounded {rounded} over {over} worst {worst:F5}\n" + sample);
            Assert.True(created > 0, "Expected created points in sweep");
            Assert.True(rounded > 0, "Expected rounded corners in sweep");
            Assert.True(over == 0, $"Sweep found {over} violations, worst turn {worst:F5} > {A + 1e-4f:F5}\n{sample}");
            return true;
        });
    }

    [Fact]
    public async Task SeededWorldScaleFuzz_RandomGridAndPillarPaths_EndpointsUnchangedAndTurnsBounded()
    {
        float[] gridLines = [-40f, -20f, 0f, 20f, 40f];
        List<Rect> rects = [];
        foreach (float line in gridLines)
        {
            rects.Add(new Rect(-60f, line - 4f, 60f, line + 4f));
            rects.Add(new Rect(line - 4f, -60f, line + 4f, 60f));
        }

        // Open plaza with isolated 8x8 pillar [26..34, 26..34]
        rects.Add(new Rect(8f, 8f, 52f, 26f));
        rects.Add(new Rect(8f, 34f, 52f, 52f));
        rects.Add(new Rect(8f, 26f, 26f, 34f));
        rects.Add(new Rect(34f, 26f, 52f, 34f));

        // Feature regions: S-bends and gentle corner (triggers NeighbourCaps, NeighbourSplits, AutoRounded)
        const float Ox = 100f, Oy = 0f; // Wide S-bend
        rects.Add(new Rect(Ox - 1f, Oy - 10f, Ox + 10f, Oy));
        rects.Add(new Rect(Ox + 10f, Oy - 10f, Ox + 14f, Oy + 15f));
        rects.Add(new Rect(Ox + 14f, Oy + 5f, Ox + 26f, Oy + 15f));

        const float Sx = 100f, Sy = 30f; // Short S-bend
        rects.Add(new Rect(Sx - 1f, Sy - 10f, Sx + 10f, Sy));
        rects.Add(new Rect(Sx + 10f, Sy - 10f, Sx + 13f, Sy + 15f));
        rects.Add(new Rect(Sx + 13f, Sy + 3.5f, Sx + 26f, Sy + 15f));

        const float Gx = 100f, Gy = 60f; // Gentle corner
        rects.Add(new Rect(Gx - 1f, Gy - 8f, Gx + 6f, Gy));
        rects.Add(new Rect(Gx + 6f, Gy - 8f, Gx + 16f, Gy + 8f));

        Rect[] region = rects.ToArray();

        await RunAsync(region, (world, map, at) =>
        {
            const int Seed = 20261028;
            const int TotalPaths = 2000;
            const float A = PlayerbotPathShaping.MaxHeadingStepRadians;
            const float MaxAllowedTurn = A + 1e-4f;

            var rng = new Random(Seed);
            var settings = PlayerbotPathShaping.Settings.From(new PlayerbotOptions());
            ShapeStats totalStats = default;
            int totalEmittedVerticesChecked = 0;
            int totalExceptionsEncountered = 0;

            for (int pathIdx = 0; pathIdx < TotalPaths; pathIdx++)
            {
                List<Vector3> input = GenerateFuzzPath(rng, gridLines, at, pathIdx);
                Assert.True(input.Count >= 3, "Input path must have at least 3 points");

                ShapeStats stats = default;
                IReadOnlyList<Vector3> shaped = PlayerbotPathShaping.ShapePoints(map, input, settings, ref stats);
                totalStats.Add(in stats);

                // Assert endpoints are unchanged
                Assert.Equal(input[0], shaped[0]);
                Assert.Equal(input[^1], shaped[^1]);

                // Assert every emitted vertex turns at most A + 1e-4 unless an exception applies
                for (int j = 1; j < shaped.Count - 1; j++)
                {
                    totalEmittedVerticesChecked++;
                    float turn = Turn2(shaped[j - 1].XY(), shaped[j].XY(), shaped[j + 1].XY());
                    if (turn > MaxAllowedTurn)
                    {
                        totalExceptionsEncountered++;
                        Assert.True(ExceptionApplies(input, shaped, j, A),
                            $"Path {pathIdx} (pattern {pathIdx % 6}) vertex {j} of {shaped.Count} at {shaped[j]} turned {turn:F4} rad > {MaxAllowedTurn:F4} rad without a documented exception (E1/E2).");

                        // The "raised by at most 0.3 rad" clause: an emitted vertex already above A is raised by at most 0.3 radians.
                        for (int k = 1; k < input.Count - 1; k++)
                        {
                            if (input[k] == shaped[j] && k + 1 < input.Count)
                            {
                                float emittedTurn = Turn2(shaped[j - 1].XY(), shaped[j].XY(), input[k + 1].XY());
                                if (emittedTurn > A)
                                    Assert.True(turn <= emittedTurn + 0.3f + 1e-4f, $"Vertex {j} turn {turn:F4} exceeded emittedTurn {emittedTurn:F4} + 0.3 rad");
                                break;
                            }
                        }
                    }
                }
            }

            float exemptedFraction = (float)totalExceptionsEncountered / totalEmittedVerticesChecked;
            _output.WriteLine($"Fuzz completed {TotalPaths} paths ({totalEmittedVerticesChecked} emitted vertices checked, "
                + $"{totalExceptionsEncountered} exceptions validated). Shaped {totalStats.CornersShaped}, Rounded {totalStats.CornersRounded}, "
                + $"NeighbourCaps {totalStats.NeighbourCaps}, Splits {totalStats.NeighbourSplits}, AutoRounded {totalStats.AutoRounded}, "
                + $"Fallbacks {totalStats.Fallbacks}.");
            _output.WriteLine($"Exempted fraction: {exemptedFraction:P2} ({totalExceptionsEncountered}/{totalEmittedVerticesChecked})");

            Assert.InRange(exemptedFraction, 0.001f, 0.35f);
            Assert.True(totalStats.CornersShaped > 0, "Expected corners to be shaped in fuzz test");
            Assert.True(totalStats.CornersRounded > 0, "Expected corners to be rounded in fuzz test");
            Assert.True(totalStats.NeighbourCaps > 0, $"Expected NeighbourCaps > 0, got {totalStats.NeighbourCaps}");
            Assert.True(totalStats.NeighbourSplits > 0, $"Expected NeighbourSplits > 0, got {totalStats.NeighbourSplits}");
            Assert.True(totalStats.AutoRounded > 0, $"Expected AutoRounded > 0, got {totalStats.AutoRounded}");
            return true;
        });
    }

    private static List<Vector3> GenerateFuzzPath(Random rng, float[] gridLines, Func<Vector2, Vector3> at, int pathIdx)
    {
        var local = new List<Vector2>();
        int pattern = pathIdx % 6;

        if (pattern == 0 || pattern == 1)
        {
            // Grid path: walking along corridors, turning at intersections around 12x12 pillars
            int ix = rng.Next(gridLines.Length);
            int iy = rng.Next(gridLines.Length);
            float curX = gridLines[ix];
            float curY = gridLines[iy];
            local.Add(new Vector2(curX, curY));

            int turns = rng.Next(2, 6);
            bool movingX = rng.Next(2) == 0;

            for (int t = 0; t < turns; t++)
            {
                if (movingX)
                {
                    int nextIx = rng.Next(gridLines.Length);
                    while (nextIx == ix) nextIx = rng.Next(gridLines.Length);
                    ix = nextIx;
                    curX = gridLines[ix];
                }
                else
                {
                    int nextIy = rng.Next(gridLines.Length);
                    while (nextIy == iy) nextIy = rng.Next(gridLines.Length);
                    iy = nextIy;
                    curY = gridLines[iy];
                }

                local.Add(new Vector2(curX, curY));
                movingX = !movingX;
            }
        }
        else if (pattern == 2)
        {
            // Pillar path: routing around the 8x8 isolated pillar in plaza [26..34, 26..34]
            float clearance = 1.5f + ((float)rng.NextDouble() * 3.5f);
            float minP = 26f - clearance;
            float maxP = 34f + clearance;

            Vector2[] corners = [new(minP, minP), new(maxP, minP), new(maxP, maxP), new(minP, maxP)];
            int startCorner = rng.Next(4);
            int count = rng.Next(3, 5);
            int step = rng.Next(2) == 0 ? 1 : 3;

            for (int i = 0; i < count; i++)
            {
                Vector2 c = corners[(startCorner + (i * step)) % 4];
                float jx = ((float)rng.NextDouble() - 0.5f) * 1.0f;
                float jy = ((float)rng.NextDouble() - 0.5f) * 1.0f;
                local.Add(new Vector2(c.X + jx, c.Y + jy));
            }
        }
        else if (pattern == 3)
        {
            // Tight / short-leg path: exercises short lead (< 2.5 yd) or tight turns (E1 triggers)
            float baseX = gridLines[rng.Next(1, gridLines.Length - 1)];
            float baseY = gridLines[rng.Next(1, gridLines.Length - 1)];
            float shortLeg = 1.5f + ((float)rng.NextDouble() * 0.7f); // < 2.5 yd, so lead < 1 yd
            float longLeg = 10f + ((float)rng.NextDouble() * 15f);

            bool horizontalFirst = rng.Next(2) == 0;
            float dir1 = rng.Next(2) == 0 ? 1f : -1f;
            float dir2 = rng.Next(2) == 0 ? 1f : -1f;

            if (horizontalFirst)
            {
                local.Add(new Vector2(baseX - (dir1 * shortLeg), baseY));
                local.Add(new Vector2(baseX, baseY));
                local.Add(new Vector2(baseX, baseY + (dir2 * longLeg)));

                // Tail: continue in the same direction along the vertical corridor (never reverses 180 deg)
                if (rng.Next(2) == 0)
                {
                    Vector2 last = local[^1];
                    local.Add(new Vector2(last.X, last.Y + (dir2 * 5f)));
                }
            }
            else
            {
                local.Add(new Vector2(baseX, baseY - (dir1 * longLeg)));
                local.Add(new Vector2(baseX, baseY));
                local.Add(new Vector2(baseX + (dir2 * shortLeg), baseY));

                // Tail: continue in the same direction along the horizontal corridor (never reverses 180 deg)
                if (rng.Next(2) == 0)
                {
                    Vector2 last = local[^1];
                    local.Add(new Vector2(last.X + (dir2 * 5f), last.Y));
                }
            }
        }
        else if (pattern == 4)
        {
            // S-bends near walls: alternating between wide (NeighbourCaps) and short (NeighbourSplits)
            bool isWide = rng.Next(2) == 0;

            if (isWide)
            {
                // Wide S-bend at (Ox, Oy): capping second push -> NeighbourCaps
                const float Ox = 100f, Oy = 0f;
                float startX = (float)rng.NextDouble() * 3f;
                float endX = 20f + ((float)rng.NextDouble() * 4f);
                local.Add(new Vector2(Ox + startX, Oy));
                local.Add(new Vector2(Ox + 10f, Oy));
                local.Add(new Vector2(Ox + 14f, Oy + 5f));
                local.Add(new Vector2(Ox + endX, Oy + 5f));
            }
            else
            {
                // Short S-bend at (Sx, Sy): split point on incoming leg -> NeighbourSplits
                const float Sx = 100f, Sy = 30f;
                float startX = (float)rng.NextDouble() * 3f;
                float endX = 20f + ((float)rng.NextDouble() * 4f);
                local.Add(new Vector2(Sx + startX, Sy));
                local.Add(new Vector2(Sx + 10f, Sy));
                local.Add(new Vector2(Sx + 13f, Sy + 3.5f));
                local.Add(new Vector2(Sx + endX, Sy + 3.5f));
            }
        }
        else
        {
            // Gentle corner at (Gx, Gy): 0.46 rad turn widened past 0.6 rad step by push -> AutoRounded
            const float Gx = 100f, Gy = 60f;
            float startX = (float)rng.NextDouble() * 2f;
            local.Add(new Vector2(Gx + startX, Gy));
            local.Add(new Vector2(Gx + 6f, Gy));
            local.Add(new Vector2(Gx + 11f, Gy + 2.5f));
        }

        return local.Select(at).ToList();
    }

    private static bool ExceptionApplies(
        IReadOnlyList<Vector3> input,
        IReadOnlyList<Vector3> shaped,
        int j,
        float a)
    {
        Vector3 current = shaped[j];

        // Exact match with an input corner (mesh corner kept unchanged)
        for (int k = 1; k < input.Count - 1; k++)
        {
            if (input[k] == current)
            {
                float inputTurn = Turn2(input[k - 1].XY(), input[k].XY(), input[k + 1].XY());
                // (E1) a corner sharper than A whose arc is not possible keeps a sharp turn
                if (inputTurn > a - 1e-4f)
                    return true;

                // (E2) a mesh corner kept unchanged whose turn the previous corner's push widened past A and that cannot be rounded
                if (shaped[j - 1] != input[k - 1])
                    return true;
            }
        }

        return false;
    }

    private void LogCost(string name, RegionWorld world, ShapeStats stats, IReadOnlyList<Vector3> shaped)
        => _output.WriteLine($"{name}: {shaped.Count} points; considered {stats.CornersConsidered} shaped {stats.CornersShaped} rounded {stats.CornersRounded} "
            + $"fallbacks {stats.Fallbacks}; probes {stats.Probes} segments {stats.Segments} estimated queries {stats.Queries}; "
            + $"fake queries: raycasts {world.Raycasts} line-of-sight {world.LosCalls} heights {world.HeightCalls} (total {world.TotalQueries}); "
            + $"{stats.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} ms");

    private static void AssertInside(RegionWorld world, IReadOnlyList<Vector3> points)
    {
        for (int i = 0; i < points.Count; i++)
            Assert.True(world.Contains(points[i].X, points[i].Y), $"point {i} {points[i]} is outside the walkable region");
    }

    private static void AssertSegmentsSafe(Map map, IReadOnlyList<Vector3> points)
    {
        for (int i = 1; i < points.Count; i++)
            Assert.True(PlayerbotPathShaping.SegmentSafe(map, points[i - 1], points[i]), $"segment {i - 1}->{i} {points[i - 1]} -> {points[i]} is not safe");
    }

    private static float NearestApproach(IReadOnlyList<Vector3> points, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 1; i < points.Count; i++)
        {
            Vector2 a = points[i - 1].XY(), b = points[i].XY(), ab = b - a;
            float t = ab.LengthSquared() < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
            best = MathF.Min(best, Vector2.Distance(a + (ab * t), p));
        }

        return best;
    }

    private static float MaxTurn(IReadOnlyList<Vector3> points)
    {
        float max = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            Vector2 a = points[i].XY() - points[i - 1].XY();
            Vector2 b = points[i + 1].XY() - points[i].XY();
            if (a.LengthSquared() < 1e-8f || b.LengthSquared() < 1e-8f) continue;
            max = MathF.Max(max, MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(b)), -1f, 1f)));
        }

        return max;
    }

    /// <summary>
    /// Start a world with <paramref name="rects"/> as the walkable region (translated to the bot's own x/y so that the world around it is
    /// one the host knows) and run <paramref name="body"/> on the world thread with a local-to-world point builder.
    /// </summary>
    private static async Task RunAsync(Rect[] rects, Func<RegionWorld, Map, Func<Vector2, Vector3>, bool> body,
        Func<RegionWorld, WorldTestHost, WorldSession, Func<Vector2, Vector3>, Task>? planner = null,
        Func<float, float, float>? floorAt = null)
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            RegionWorld? world = null;
            Func<Vector2, Vector3>? at = null;
            Map? map = null;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                map = player.Map!;
                var offset = new Vector2(player.X, player.Y);
                // floorAt takes the region's local x/y; the default is the flat floor.
                Func<float, float, float> local = floorAt ?? ((_, _) => Floor);
                world = new RegionWorld(rects, offset, (x, y) => local(x - offset.X, y - offset.Y));
                at = p => new Vector3(offset.X + p.X, offset.Y + p.Y, local(p.X, p.Y));
                WorldCollision.Of(host.World).Install(lineOfSight: world, pathfinder: world);
                return true;
            });

            if (planner is not null)
                await planner(world!, host, session, at!);
            else
                await host.OnWorldAsync(() => body(world!, map!, at!));
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private readonly record struct Rect(float MinX, float MinY, float MaxX, float MaxY);

    /// <summary>
    /// A walkable region that is a union of closed rectangles on a flat floor, and the three services built on it. A mesh raycast walks
    /// the segment and stops where it first leaves the region (Hit, with the fraction and the point of the exit; a start outside the
    /// region is unavailable, as for the real mesh); a chest-height segment is in line of sight only if it never leaves the region.
    /// Every call is counted, so a test measures the queries the shaper really makes.
    /// </summary>
    private sealed class RegionWorld : ILineOfSight, IPathfinder, IMeshPointQuery
    {
        private const float Eps = 1e-4f;
        private readonly Rect[] _rects;
        private readonly Func<float, float, float> _floor;
        private Vector3[] _waypoints = [];

        public RegionWorld(Rect[] rects, Vector2 offset, Func<float, float, float> floor)
        {
            _rects = [.. rects.Select(r => new Rect(r.MinX + offset.X, r.MinY + offset.Y, r.MaxX + offset.X, r.MaxY + offset.Y))];
            _floor = floor;
        }

        public int Raycasts { get; private set; }
        public int LosCalls { get; private set; }
        public int HeightCalls { get; private set; }
        public int TotalQueries => Raycasts + LosCalls + HeightCalls;

        public bool Enabled => true;

        public void ResetCounters() => (Raycasts, LosCalls, HeightCalls) = (0, 0, 0);

        public void SetWaypoints(Vector3[] waypoints) => _waypoints = waypoints;

        public bool Contains(float x, float y)
        {
            foreach (Rect r in _rects)
            {
                if (x >= r.MinX - Eps && x <= r.MaxX + Eps && y >= r.MinY - Eps && y <= r.MaxY + Eps)
                    return true;
            }

            return false;
        }

        /// <summary>The share of the segment walked before it first leaves the region (1: it never does); the start is taken to be inside.</summary>
        private float ExitFraction(float ax, float ay, float bx, float by)
        {
            var ts = new List<float> { 0f, 1f };
            foreach (Rect r in _rects)
            {
                AddCrossings(ts, ax, bx, r.MinX);
                AddCrossings(ts, ax, bx, r.MaxX);
                AddCrossings(ts, ay, by, r.MinY);
                AddCrossings(ts, ay, by, r.MaxY);
            }

            ts.Sort();
            for (int i = 1; i < ts.Count; i++)
            {
                if (ts[i] - ts[i - 1] <= 1e-6f) continue;
                float mid = 0.5f * (ts[i] + ts[i - 1]);
                if (!Contains(ax + ((bx - ax) * mid), ay + ((by - ay) * mid)))
                    return ts[i - 1];
            }

            return 1f;
        }

        private static void AddCrossings(List<float> ts, float a, float b, float edge)
        {
            if (MathF.Abs(b - a) < 1e-9f) return;
            float t = (edge - a) / (b - a);
            if (t > 0f && t < 1f) ts.Add(t);
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Raycasts++;
            if (!Contains(start.X, start.Y)) return MeshRaycastResult.Unavailable(end);
            float t = ExitFraction(start.X, start.Y, end.X, end.Y);
            if (t >= 1f) return new(true, false, 1f, new Vector3(end.X, end.Y, _floor(end.X, end.Y)));
            float hx = start.X + ((end.X - start.X) * t), hy = start.Y + ((end.Y - start.Y) * t);
            return new(true, true, t, new Vector3(hx, hy, _floor(hx, hy)));
        }

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
        {
            LosCalls++;
            return Contains(from.X, from.Y) && ExitFraction(from.X, from.Y, to.X, to.Y) >= 1f;
        }

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        {
            HeightCalls++;
            return _floor(x, y);
        }

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, .. _waypoints.Skip(1)]);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return true;
        }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}

internal static class PathShapingVectorExtensions
{
    public static Vector2 XY(this Vector3 v) => new(v.X, v.Y);
}
