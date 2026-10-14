using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

public sealed class CreatureSplineSmoothingTests
{
    private sealed class ClearMeshQuery : IMeshPointQuery
    {
        public bool BlockRaycast { get; init; }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return true;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => BlockRaycast
                ? new MeshRaycastResult(Available: true, Hit: true, Fraction: 0.5f, Point: Vector3.Lerp(start, end, 0.5f))
                : new MeshRaycastResult(Available: true, Hit: false, Fraction: 1.0f, Point: end);
    }

    private sealed class MeshCornerPathfinder(IMeshPointQuery mesh, params Vector3[] corners) : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, .. corners, end]);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
            => mesh.TryGetRandomPointAround(mapId, center, radius, random, options, out point);

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => mesh.Raycast(mapId, start, end, options);
    }

    private sealed class HillFloorObstacles(Func<float, float, float> heightAt) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => heightAt(x, y);

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private static Creature MakeCreature(uint counter, float x = 0f, float y = 0f, float z = 0f)
    {
        var tpl = Template();
        var spawn = Spawn(counter, WolfEntry, x, y, z);
        var content = Content([tpl], [spawn]);
        return new Creature(counter, tpl, spawn, content, new Random((int)counter));
    }

    [Fact]
    public void CreatureSpline_BlendsOrientationAcrossCorners_AndFinishesOnFinalLegHeading()
    {
        Creature wolf = MakeCreature(1, x: 0f, y: 0f, z: 0f);

        // Two 10-yard legs: (0,0) -> (10,0) along +X (0 rad), then (10,0) -> (10,10) along +Y (pi/2 rad).
        Vector3 corner = new(10f, 0f, 0f);
        Vector3 dest = new(10f, 10f, 0f);
        CreatureSpline spline = wolf.StartSpline([corner, dest], run: true, SplineFacing.None, splineId: 1, clockMs: 0);

        Assert.Equal(0f, wolf.Orientation, 3);

        // Near the end of leg 0 (95% of leg 0 => 47.5% of total duration), orientation begins turning smoothly toward pi/4.
        long nearCornerBefore = (long)(spline.DurationMs * 0.475f);
        wolf.AdvanceSpline(nearCornerBefore, 100);
        Assert.InRange(wolf.Orientation, 0.05f, MathF.PI / 4f + 0.01f);

        // At the exact corner (50% of total duration), orientation is the bisector pi/4.
        long atCorner = spline.DurationMs / 2;
        wolf.AdvanceSpline(atCorner, 200);
        Assert.Equal(MathF.PI / 4f, wolf.Orientation, 2);

        // Past the corner blend window on leg 1 (80% of total duration), orientation tracks leg 1 (+Y = pi/2).
        long onSecondLeg = (long)(spline.DurationMs * 0.80f);
        wolf.AdvanceSpline(onSecondLeg, 300);
        Assert.Equal(MathF.PI / 2f, wolf.Orientation, 3);

        // On completion with no explicit FinalOrientation, the creature faces the final leg's heading (pi/2), not leg 0's (0).
        Assert.True(wolf.AdvanceSpline(spline.DurationMs, 400));
        Assert.Equal(MathF.PI / 2f, wolf.Orientation, 3);
    }

    [Fact]
    public void CreatureSplineSmoothing_CutsSharpCornersWithQuadraticBezier_AndPreservesDestination()
    {
        var mesh = new ClearMeshQuery();
        var start = new Vector3(0f, 0f, 10f);
        var corners = new List<Vector3>
        {
            new(12f, 0f, 10f),
            new(12f, 12f, 10f),
        };

        List<Vector3> smoothed = CreatureSplineSmoothing.SmoothCorners(
            0u,
            start,
            corners,
            mesh,
            static (_, _, _, _) => 10f);

        // 1 interior corner replaced by 3 Bezier points + 1 destination = 4 points.
        Assert.Equal(4, smoothed.Count);
        Assert.Equal(corners[^1], smoothed[^1]);
        Assert.All(smoothed.Take(3), pt =>
        {
            Assert.True(pt.X < 12f && pt.X > 8f);
            Assert.True(pt.Y > 0f && pt.Y < 4f);
        });
    }

    [Fact]
    public void CreatureSplineSmoothing_BlockedRaycast_KeepsOriginalSharpCorner()
    {
        var blockedMesh = new ClearMeshQuery { BlockRaycast = true };
        var start = new Vector3(0f, 0f, 10f);
        var corners = new List<Vector3>
        {
            new(12f, 0f, 10f),
            new(12f, 12f, 10f),
        };

        List<Vector3> result = CreatureSplineSmoothing.SmoothCorners(
            0u,
            start,
            corners,
            blockedMesh,
            static (_, _, _, _) => 10f);

        Assert.Equal(2, result.Count);
        Assert.Equal(corners[0], result[0]);
        Assert.Equal(corners[1], result[1]);
    }

    [Fact]
    public void CreatureSplineSmoothing_VaryAndSmooth_SeparatesDifferentCreaturesAlongCorridor()
    {
        Creature c1 = MakeCreature(101, x: 0f, y: 0f, z: 10f);
        Creature c2 = MakeCreature(202, x: 0f, y: 0f, z: 10f);
        var mesh = new ClearMeshQuery();
        var start = new Vector3(0f, 0f, 10f);
        var baseCorners = new List<Vector3>
        {
            new(10f, 0f, 10f),
            new(20f, 2f, 10f),
            new(30f, 0f, 10f),
        };

        List<Vector3> path1 = CreatureSplineSmoothing.VaryAndSmooth(
            c1, start, [.. baseCorners], mesh, static (_, _, _, _) => 10f, default, allowVariation: true);
        List<Vector3> path2 = CreatureSplineSmoothing.VaryAndSmooth(
            c2, start, [.. baseCorners], mesh, static (_, _, _, _) => 10f, default, allowVariation: true);

        Assert.Equal(baseCorners[^1], path1[^1]);
        Assert.Equal(baseCorners[^1], path2[^1]);
        Assert.NotEqual(path1[1], path2[1]);
    }

    [Fact]
    public void PlayerCharge_OverHillCrest_SubdividesTerrainContour_AndTracksActiveSpline()
    {
        const uint ChargeSpell = 940100;
        using var kit = new SpellTestKit(
        [
            Spell(ChargeSpell, Effect(SpellEffectName.Charge, 0, SpellImplicitTarget.UnitEnemy)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            },
        ]);

        const float baseZ = 83.5f;
        // A hill crest peaking at +1.5 yards in the middle of the charge (x = 8..10).
        float HillHeight(float x, float y) => baseZ + (1.5f * MathF.Sin(Math.Clamp(x / 16f, 0f, 1f) * MathF.PI));

        var clearMesh = new ClearMeshQuery();
        WorldCollision.Of(kit.World).Install(
            new HillFloorObstacles(HillHeight),
            new MeshCornerPathfinder(clearMesh, new Vector3(8f, 0f, HillHeight(8f, 0f))));

        (Player caster, _) = kit.AddPlayer(1, 0f, 0f);
        (Player target, _) = kit.AddPlayer(2, 20f, 0f);

        kit.System.CastSpell(caster, ChargeSpell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        // ActiveSpline is recorded on the player's LocomotionState and can be sampled during flight.
        CreatureSpline? active = caster.Locomotion.ActiveSpline;
        Assert.NotNull(active);
        Assert.True(active!.Points.Count >= 2);
        Assert.Contains(active.Points, p => p.Z > baseZ + 0.5f);

        (float midX, _, float midZ, _) = caster.Locomotion.SampleSpline(caster);
        Assert.Equal(0f, midX, 2);
        Assert.Equal(baseZ, midZ, 2);

        caster.Locomotion.AdvanceSpline(active.DurationMs / 2);
        (midX, _, midZ, _) = caster.Locomotion.SampleSpline(caster);
        Assert.True(midX > 3f && midX < 15f);
        Assert.True(midZ > baseZ + 0.4f);

        // Advancing past duration clears ActiveSpline.
        caster.Locomotion.AdvanceSpline(active.DurationMs);
        Assert.Null(caster.Locomotion.ActiveSpline);
    }

    [Fact]
    public void TaxiFlight_CatmullRomInterpolation_SmoothlyCurvesAndTurnsAlongMultiWaypointHop()
    {
        TaxiFlightSystem.Waypoint[] segments =
        [
            new(0f, 0f, 10f),
            new(20f, 0f, 20f),
            new(20f, 20f, 30f),
            new(0f, 20f, 20f),
        ];

        // On interior C-curve segment 1 ((20,0,20) -> (20,20,30)), Catmull-Rom bows outward to X = 22.5 at t = 0.5.
        TaxiFlightSystem.Waypoint mid = TaxiFlightSystem.EvaluateCatmullRom(segments, 1, 0.5f, 0f, out float midHeading);
        Assert.Equal(22.5f, mid.X, 2);
        Assert.Equal(10f, mid.Y, 2);
        Assert.Equal(26.25f, mid.Z, 2);
        Assert.Equal(MathF.PI / 2f, midHeading, 2);
    }

    [Fact]
    public void CreatureSplineSmoothing_OnElevatedBridgeAboveTerrain_PreservesNavMeshZ()
    {
        var mesh = new ClearMeshQuery();
        // Creature is on a bridge/porch at Z = 10.6f while raw terrain below is at Z = 10.0f (delta 0.6f > 0.4f).
        var start = new Vector3(0f, 0f, 10.6f);
        var corners = new List<Vector3>
        {
            new(12f, 0f, 10.6f),
            new(12f, 12f, 10.6f),
        };

        List<Vector3> smoothed = CreatureSplineSmoothing.SmoothCorners(
            0u,
            start,
            corners,
            mesh,
            static (_, _, _, _) => 10.0f);

        Assert.Equal(4, smoothed.Count);
        Assert.All(smoothed, pt => Assert.Equal(10.6f, pt.Z, 3));
    }
}
