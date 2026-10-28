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
            Assert.True(shapedTurn <= PlayerbotPathShaping.MaxHeadingStepRadians + 0.02f,
                $"max heading change {shapedTurn:F3} rad exceeds {PlayerbotPathShaping.MaxHeadingStepRadians} + 0.02");
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
            Assert.True(shapedTurn <= PlayerbotPathShaping.MaxHeadingStepRadians + 0.02f, $"max heading change {shapedTurn:F3} rad");
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
            Assert.True(MaxTurn(shaped) <= PlayerbotPathShaping.MaxHeadingStepRadians + 0.02f, $"max heading change {MaxTurn(shaped):F3} rad");
            // The clearance is the corner's (and the arc's apex's) from the wall corner; the two tangent points sit on the legs toward the
            // corner and are closer by design (the leg from the start runs nearer the wall corner than its end does).
            Assert.True(shaped.Count >= 5, $"expected the tangent points and at least one arc point, got {shaped.Count} points");
            for (int i = 2; i < shaped.Count - 2; i++)
                Assert.True(Vector2.Distance(shaped[i].XY(), at(LCorner).XY()) >= 2f - Tolerance, $"arc point {i} {shaped[i]} is too close to the wall corner");
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
            Assert.True(MaxTurn(shaped) <= PlayerbotPathShaping.MaxHeadingStepRadians + 0.02f);
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
        Func<RegionWorld, WorldTestHost, WorldSession, Func<Vector2, Vector3>, Task>? planner = null)
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
                world = new RegionWorld(rects, offset, Floor);
                at = local => new Vector3(offset.X + local.X, offset.Y + local.Y, Floor);
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
        private readonly float _floor;
        private Vector3[] _waypoints = [];

        public RegionWorld(Rect[] rects, Vector2 offset, float floor)
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
            if (t >= 1f) return new(true, false, 1f, new Vector3(end.X, end.Y, _floor));
            return new(true, true, t, new Vector3(start.X + ((end.X - start.X) * t), start.Y + ((end.Y - start.Y) * t), _floor));
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
            return _floor;
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
