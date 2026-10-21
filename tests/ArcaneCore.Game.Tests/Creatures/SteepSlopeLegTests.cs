using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// vmangos keeps the path search of random, confused, fear and flee movement off steep slopes: <c>MOVE_EXCLUDE_STEEP_SLOPES</c>
/// (RandomMovementGenerator.cpp:52, read by MoveSplineInit.h:129-130) and <c>PathFinder::ExcludeSteepSlopes</c>
/// (ConfusedMovementGenerator.cpp:79, FearMovementGenerator.cpp:38, FleeingMovementGenerator.cpp:56) set the exclude flags of the
/// filter its <c>findPath</c> searches with, while every other creature path excludes nothing (PathInfo::createFilter,
/// PathFinder.cpp:657-677). The start and end polygons are located by the include flags alone (getPolyByLocation,
/// PathFinder.cpp:145-157), so a creature standing on a slope still paths off it. Before this only the point choice and the flee
/// raycast kept off steep slopes, and the leg to a point beyond a slope walked straight up it.
/// </summary>
public sealed class SteepSlopeLegTests
{
    private const uint MapId = 0;

    /// <summary>4×4 cells of 5 yd over world [0, 20)²; the cells at X 10–15, Y 0–15 are a steep slope, open at the top (Y 15–20).</summary>
    private static CellTile Slope() => new(0, 0, 0, 0, 4, 4, Size: 5)
    {
        Flags = (i, j) => i == 2 && j < 3 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground,
    };

    private static bool OnSlope(float x, float y) => x > 10.05f && x < 14.95f && y > -0.05f && y < 14.95f;

    /// <summary>4×4 cells of 10 yd over world [0, 40)²; the one cell at X 10–20, Y 10–20 is a steep slope, its centre 5 yd from flat ground.</summary>
    private static CellTile SlopePatch() => new(0, 0, 0, 0, 4, 4, Size: 10)
    {
        Flags = (i, j) => i == 1 && j == 1 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground,
    };

    private static readonly Vector3 WestOfSlope = new(7, 5, 0);
    private static readonly Vector3 EastOfSlope = new(17, 5, 0);

    private sealed class Scene : IDisposable
    {
        private readonly NavMeshFixture _fixture = new();

        public Scene(CellTile tile, MovementGeneratorType motion, Vector3 spawn, float wander = 15f)
        {
            _fixture.WriteParams(MapId);
            _fixture.WriteTile(MapId, 31, 31, tile);
            var nav = new NavMeshPathfinder(_fixture.Directory);
            Assert.True(nav.LoadTile(MapId, 31, 31));
            byte movementType = motion == MovementGeneratorType.Random ? (byte)1 : (byte)0;
            CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, spawn.X, spawn.Y, z: spawn.Z, movementType: movementType, wander: wander)]);
            (World, _, Creatures) = CreateAiSystem(content);
            WorldCollision.Of(World).Install(pathfinder: nav);
            (Player player, _) = AddPlayer(World, 1, -30, 0); // off the tile, out of the way of every leg
            Wolf = Assert.Single(Creatures.Creatures);

            if (motion == MovementGeneratorType.Confused)
            {
                Wolf.UnitFlags |= UnitFlags.Confused;
            }
            else if (motion == MovementGeneratorType.Fleeing)
            {
                CcState.RememberFearSource(Wolf, player);
                Wolf.UnitFlags |= UnitFlags.Fleeing;
            }

            for (int i = 0; i < 10 && Wolf.Motion.CurrentType != motion; i++)
            {
                World.RunTick(50);
            }

            Assert.Equal(motion, Wolf.Motion.CurrentType);
            Assert.False(Wolf.Combat.IsInCombat);
        }

        public WorldRuntime World { get; }

        public CreatureMapSystem Creatures { get; }

        public Creature Wolf { get; }

        /// <summary>The oracle for a snapped height here: no terrain or model in this world, so the ground is the mesh surface less its bias, plus the stand offset.</summary>
        public void AssertOnTheMeshGround(IEnumerable<Vector3> points)
        {
            Map map = World.GetMap(MapId);
            foreach (Vector3 p in points)
            {
                Assert.True(map.Collision.TryGetMeshFloor(p.X, p.Y, p.Z, 2f, 2f, out float mesh), $"no mesh under {p}");
                Assert.Equal(mesh - GroundHeight.MeshBias + GroundHeight.StandOffset, p.Z, 0.02f);
            }
        }

        /// <summary>Stand the creature still at <paramref name="at"/>, under the same motion, for a leg planned from there.</summary>
        public void StandAt(Vector3 at)
        {
            Creatures.StopMoving(Wolf);
            Wolf.Relocate(at.X, at.Y, at.Z, 0, 0);
        }

        public void Dispose()
        {
            World.Dispose();
            _fixture.Dispose();
        }
    }

    /// <summary>The first point of the leg from <paramref name="start"/> through <paramref name="corners"/> that lies on the slope, sampled every 0.25 yd.</summary>
    private static Vector3? FirstPointOnSlope(Vector3 start, IReadOnlyList<Vector3> corners)
    {
        Vector3 from = start;
        foreach (Vector3 to in corners)
        {
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / 0.25f));
            for (int s = 0; s <= steps; s++)
            {
                var p = Vector3.Lerp(from, to, (float)s / steps);
                if (OnSlope(p.X, p.Y))
                {
                    return p;
                }
            }

            from = to;
        }

        return null;
    }

    [Theory]
    [InlineData(MovementGeneratorType.Random)]
    [InlineData(MovementGeneratorType.Confused)]
    [InlineData(MovementGeneratorType.Fleeing)]
    public void ALegToAPointBeyondASteepSlope_GoesRoundIt(MovementGeneratorType motion)
    {
        using var scene = new Scene(Slope(), motion, WestOfSlope);
        scene.StandAt(WestOfSlope);

        PlannedPath plan = scene.Creatures.PlanPath(scene.Wolf, EastOfSlope);

        Assert.Equal(PathOutcome.Mesh, plan.Outcome);
        // This world has a mesh and no terrain or models, so the leg's ground is the mesh less its bias (GroundHeight.MeshBias), not the mesh's
        // own height: every point is checked against the mesh, and the ends in the plane.
        scene.AssertOnTheMeshGround(plan.Corners);
        Assert.True(Vector2.Distance(new Vector2(EastOfSlope.X, EastOfSlope.Y), new Vector2(plan.Corners[^1].X, plan.Corners[^1].Y)) <= 0.1f, $"the {motion} leg ends at {plan.Corners[^1]}");
        Assert.Null(FirstPointOnSlope(WestOfSlope, plan.Corners));
        Assert.True(plan.Corners.Count >= 2, $"the {motion} leg is a straight line to {plan.Corners[^1]}");
    }

    [Fact]
    public void APathOfAnyOtherMotion_StillCrossesTheSlope()
    {
        // vmangos excludes nothing by default (PathFinder.cpp:657-677): the same leg of an idle creature (a scripted move) is the
        // straight line, which also shows that the slope stands in the way of the legs above.
        using var scene = new Scene(Slope(), MovementGeneratorType.Idle, WestOfSlope);

        PlannedPath plan = scene.Creatures.PlanPath(scene.Wolf, EastOfSlope);

        Assert.Equal(PathOutcome.Mesh, plan.Outcome);
        Assert.NotNull(FirstPointOnSlope(WestOfSlope, plan.Corners));
    }

    [Theory]
    [InlineData(MovementGeneratorType.Random)]
    [InlineData(MovementGeneratorType.Confused)]
    [InlineData(MovementGeneratorType.Fleeing)]
    public void ACreatureStandingOnASteepSlope_StillPathsOffIt(MovementGeneratorType motion)
    {
        // The creature stands in the middle of a steep polygon, 5 yd from any flat one: vmangos locates its start by the include flags
        // alone (getPolyByLocation, PathFinder.cpp:145-157) and Detour's findPath filters only the polygons it enters
        // (DetourNavMeshQuery.cpp:1037), so the leg leaves the slope. Locating the start with the slope excluded finds no polygon at all.
        var onSlope = new Vector3(15, 15, 0);
        var flat = new Vector3(35, 15, 0);
        using var scene = new Scene(SlopePatch(), motion, new Vector3(5, 5, 0), wander: 5f);
        scene.StandAt(onSlope);

        PlannedPath plan = scene.Creatures.PlanPath(scene.Wolf, flat);

        Assert.Equal(PathOutcome.Mesh, plan.Outcome);
        Assert.True(Vector2.Distance(new Vector2(flat.X, flat.Y), new Vector2(plan.Corners[^1].X, plan.Corners[^1].Y)) <= 0.1f, $"the {motion} leg ends at {plan.Corners[^1]}");
        scene.AssertOnTheMeshGround(plan.Corners);
    }

    [Fact]
    public void AWanderer_NeverWalksOntoTheSlope_WhileItWandersBothSidesOfIt()
    {
        // The 15 yd wander circle round (7, 7) holds mesh points on both sides of the slope; a point beyond it is reached round its
        // open top end. 5 minutes of 50 ms ticks.
        using var scene = new Scene(Slope(), MovementGeneratorType.Random, new Vector3(7, 7, 0));
        object? last = null;
        int legs = 0;
        int eastLegs = 0;
        for (int i = 0; i < 6_000; i++)
        {
            scene.World.RunTick(50);
            Assert.False(OnSlope(scene.Wolf.X, scene.Wolf.Y), $"the wanderer walked onto the slope at ({scene.Wolf.X}, {scene.Wolf.Y})");
            if (scene.Wolf.Spline is { } spline && !ReferenceEquals(spline, last))
            {
                last = spline;
                legs++;
                eastLegs += spline.EndX > 15f ? 1 : 0;
            }
        }

        Assert.True(legs >= 10, $"only {legs} legs were walked");
        Assert.True(eastLegs >= 3, $"only {eastLegs} legs went beyond the slope, so it was never in the way");
    }
}
