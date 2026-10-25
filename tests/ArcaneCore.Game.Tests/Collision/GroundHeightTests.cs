using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.Locomotion;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="GroundHeight"/> on synthetic ground: a flat one-tile terrain (<see cref="HazardTestKit"/>, ground at z 20) with scripted model and
/// mesh services, and a model-only world for the leg tests. The same helper on the real extraction is checked in
/// <c>GroundSnapRealTerrainTests</c> (gated on <c>ARCANECORE_TEST_TERRAIN_DIR</c>).
/// </summary>
public sealed class GroundHeightTests
{
    private const float X = HazardTestKit.X;
    private const float Y = HazardTestKit.Y;
    private const float Ground = HazardTestKit.Ground;

    /// <summary>Model floors from a function of (x, y); null where the function has none.</summary>
    private sealed class ModelFloors(Func<float, float, float?> floorAt) : ILineOfSight
    {
        private int _calls;

        /// <summary>Model height questions asked so far.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        {
            Interlocked.Increment(ref _calls);
            return floorAt(x, y) is { } floor && floor <= z && floor >= z - maxSearchDistance ? floor : null;
        }

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    /// <summary>Several model surfaces over every point (a floor, a roof, an upper storey): the highest one at or under the probe, as the real tree answers.</summary>
    private sealed class StackedFloors(params float[] surfaces) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        {
            float? best = null;
            foreach (float surface in surfaces)
            {
                if (surface <= z && surface >= z - maxSearchDistance && (best is null || surface > best))
                {
                    best = surface;
                }
            }

            return best;
        }

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    /// <summary>A pathfinder whose mesh surface is a function of (x, y); no paths.</summary>
    private sealed class MeshFloors(Func<float, float, float> surfaceAt) : IPathfinder, IMeshFloorQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, end]);

        public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height)
        {
            height = surfaceAt(x, y);
            return height >= z - below && height <= z + above;
        }
    }

    /// <summary>The flat tile with its terrain in memory, as the grid of a player nearby would have it: a drape reads no terrain tile from disk.</summary>
    private static HazardTestKit Kit(LiquidTypeFlags? liquid)
    {
        var kit = new HazardTestKit(liquid);
        kit.Map.Terrain.GetTile(31, 32);
        return kit;
    }

    private static MapCollision ModelOnly(WorldRuntime world, Func<float, float, float?> floorAt)
    {
        WorldCollision.Of(world).Install(lineOfSight: new ModelFloors(floorAt));
        for (int tx = 31; tx <= 33; tx++)
        {
            for (int ty = 31; ty <= 33; ty++)
            {
                world.GetMap(0).Terrain.GetTile(tx, ty); // the tiles around the origin, in memory
            }
        }

        return world.GetMap(0).Collision;
    }

    [Fact]
    public void TheDefaultQuery_IsTheWalkerQuery_AndDefaultIsNot()
    {
        var q = new GroundQuery();
        Assert.Equal(2f, q.Up);
        Assert.Equal(4f, q.Down);
        Assert.Equal(LiquidMode.Bottom, q.Liquid);
        Assert.True(q.PreferTerrainOverProps);
        Assert.True(q.AllowMeshFallback);
        Assert.False(q.LoadTiles);
        Assert.Equal(q, GroundHeight.Walker);
        Assert.Equal(0f, default(GroundQuery).Up);
    }

    [Theory]
    [InlineData(20.3f)]   // a mesh height 0.3 above the ground
    [InlineData(19.0f)]   // below it: snapped up
    [InlineData(22.5f)]   // above the probe start (z + Up is above the ground): snapped down
    public void ATerrainPoint_SnapsToTheGroundPlusTheStandOffset_BothWays(float z)
    {
        using var kit = Kit(liquid: null);
        MapCollision collision = kit.Map.Collision;

        Assert.True(GroundHeight.TrySnap(collision, X, Y, z, new GroundQuery(), out float snapped, out GroundSource source));

        Assert.Equal(Ground + GroundHeight.StandOffset, snapped, 0.001f);
        Assert.Equal(GroundSource.Terrain, source);
    }

    [Fact]
    public void AFloorInAnotherStorey_IsNotTaken_AndTheHeightIsKept()
    {
        using var kit = Kit(liquid: null);
        MapCollision collision = kit.Map.Collision;

        // 10 yd over the ground: the ground is 10 yd below, outside Down = 4. 3 yd under it: the probe (z + 2) is still under the terrain.
        Assert.False(GroundHeight.TrySnap(collision, X, Y, Ground + 10f, new GroundQuery(), out float high, out GroundSource highSource));
        Assert.Equal(Ground + 10f, high);
        Assert.Equal(GroundSource.None, highSource);
        Assert.Equal(new Vector3(X, Y, Ground - 3f), GroundHeight.SnapOrKeep(collision, new Vector3(X, Y, Ground - 3f), new GroundQuery()));
        Assert.True(GroundHeight.TrySnap(collision, X, Y, Ground + 10f, new GroundQuery(Up: 2f, Down: 12f, LiquidMode.Bottom, true, true, false), out float reach, out _));
        Assert.Equal(Ground + GroundHeight.StandOffset, reach, 0.001f);
    }

    [Fact]
    public void NonFinitePoints_AreKept()
    {
        using var kit = Kit(liquid: null);
        var q = new GroundQuery();

        Assert.False(GroundHeight.TrySnap(kit.Map.Collision, float.NaN, Y, 0f, q, out _, out GroundSource source));
        Assert.Equal(GroundSource.None, source);
        Vector3 bad = new(X, Y, float.PositiveInfinity);
        Assert.Equal(bad, GroundHeight.SnapOrKeep(kit.Map.Collision, bad, q));
    }

    [Fact]
    public void AModelTopOverTheWalkableGround_IsAProp_UntilTheUnitStandsOnIt()
    {
        using var kit = Kit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: new ModelFloors((_, _) => Ground + 1.0f)); // a doodad 1 yd over the ground
        MapCollision collision = kit.Map.Collision;
        Assert.Equal(Ground + 1.0f, collision.GetHeight(X, Y, Ground + 0.1f), 0.001f); // the raw floor query takes the prop

        var props = new GroundQuery();
        var anyModel = new GroundQuery(2f, 4f, LiquidMode.Bottom, PreferTerrainOverProps: false, true, false);

        // Standing on the ground beside it: the terrain.
        Assert.True(GroundHeight.TrySnap(collision, X, Y, Ground + 0.1f, props, out float onGround, out GroundSource groundSource));
        Assert.Equal(Ground + GroundHeight.StandOffset, onGround, 0.001f);
        Assert.Equal(GroundSource.Terrain, groundSource);

        // The same point without the guard: the prop top (what GetHeight says).
        Assert.True(GroundHeight.TrySnap(collision, X, Y, Ground + 0.1f, anyModel, out float onProp, out GroundSource propSource));
        Assert.Equal(Ground + 1.0f + GroundHeight.StandOffset, onProp, 0.001f);
        Assert.Equal(GroundSource.Model, propSource);

        // Standing on the model already (z at its top): the model is the floor even with the guard.
        Assert.True(GroundHeight.TrySnap(collision, X, Y, Ground + 1.1f, props, out float standing, out GroundSource standingSource));
        Assert.Equal(Ground + 1.0f + GroundHeight.StandOffset, standing, 0.001f);
        Assert.Equal(GroundSource.Model, standingSource);

        // A model top barely over the terrain (0.3 yd, a kerb or a step) is a floor, not a prop.
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: new ModelFloors((_, _) => Ground + 0.3f));
        Assert.True(GroundHeight.TrySnap(collision, X, Y, Ground + 0.1f, props, out float kerb, out _));
        Assert.Equal(Ground + 0.3f + GroundHeight.StandOffset, kerb, 0.001f);
    }

    [Fact]
    public void WithNoTerrainOrModel_TheMeshFloorLessItsBiasIsTheFallback_AndOnlyThen()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision.Of(world).Install(pathfinder: new MeshFloors((_, _) => 50.2f));
        MapCollision collision = world.GetMap(0).Collision;

        Assert.True(GroundHeight.TrySnap(collision, 3000f, 3000f, 50.4f, new GroundQuery(), out float snapped, out GroundSource source));
        Assert.Equal(GroundSource.Mesh, source);
        Assert.Equal(50.2f - GroundHeight.MeshBias + GroundHeight.StandOffset, snapped, 0.001f);

        var noMesh = new GroundQuery(2f, 4f, LiquidMode.Bottom, true, AllowMeshFallback: false, false);
        Assert.False(GroundHeight.TrySnap(collision, 3000f, 3000f, 50.4f, noMesh, out float kept, out GroundSource keptSource));
        Assert.Equal(50.4f, kept);
        Assert.Equal(GroundSource.None, keptSource);

        // A mesh surface in another storey is no floor either.
        Assert.False(GroundHeight.TrySnap(collision, 3000f, 3000f, 70f, new GroundQuery(), out _, out _));
    }

    [Fact]
    public void AMeshSurfaceUpToFourYardsOver_IsTheFallback_WhenTheModelsAndTerrainHaveNoFloorInTheWindow()
    {
        // A chord across an arched cave floor runs up to 3.2 yd under the floor (Grizzled Den): a model probe from z + 2 starts below it,
        // the terrain is 90 yd above. The mesh surface is walkable, so it is looked for to 4 yd over the point.
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision.Of(world).Install(pathfinder: new MeshFloors((_, _) => 83.5f));
        MapCollision collision = world.GetMap(0).Collision;

        Assert.True(GroundHeight.TrySnap(collision, 3000f, 3000f, 80.5f, new GroundQuery(), out float snapped, out GroundSource source));

        Assert.Equal(GroundSource.Mesh, source);
        Assert.Equal(83.5f - GroundHeight.MeshBias + GroundHeight.StandOffset, snapped, 0.001f);
        Assert.False(GroundHeight.TrySnap(collision, 3000f, 3000f, 79f, new GroundQuery(), out _, out _)); // 4.5 yd over: another floor
    }

    [Fact]
    public void ATerrainFloorInAnotherStorey_GivesWayToTheMeshSurfaceInTheWindow()
    {
        using var kit = Kit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(pathfinder: new MeshFloors((_, _) => Ground + 8f)); // a deck 8 yd over the terrain
        MapCollision collision = kit.Map.Collision;

        Assert.True(GroundHeight.TrySnap(collision, X, Y, Ground + 9f, new GroundQuery(), out float snapped, out GroundSource source));

        Assert.Equal(GroundSource.Mesh, source);
        Assert.Equal(Ground + 8f - GroundHeight.MeshBias + GroundHeight.StandOffset, snapped, 0.001f);
    }

    [Fact]
    public void TheMeshFallbackIsNotUsed_WhereTheTerrainAnswers()
    {
        using var kit = Kit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(pathfinder: new MeshFloors((_, _) => Ground + 0.4f));

        Assert.True(GroundHeight.TrySnap(kit.Map.Collision, X, Y, Ground + 0.4f, new GroundQuery(), out float snapped, out GroundSource source));

        Assert.Equal(GroundSource.Terrain, source);
        Assert.Equal(Ground + GroundHeight.StandOffset, snapped, 0.001f);
    }

    [Fact]
    public void ALiquidPoint_SnapsToTheBottomForAWalker_AndToTheSurfaceForASwimmer()
    {
        using var kit = Kit(LiquidTypeFlags.Water); // ground 20, water surface 100
        MapCollision collision = kit.Map.Collision;
        float z = HazardTestKit.Level - 5f;

        var bottom = new GroundQuery(Up: 10f, Down: 80f, LiquidMode.Bottom, true, true, false);
        Assert.True(GroundHeight.TrySnap(collision, X, Y, z, bottom, out float floor, out GroundSource floorSource));
        Assert.Equal(Ground + GroundHeight.StandOffset, floor, 0.001f);
        Assert.Equal(GroundSource.Terrain, floorSource);

        var surface = bottom with { Liquid = LiquidMode.Surface };
        Assert.True(GroundHeight.TrySnap(collision, X, Y, z, surface, out float top, out GroundSource topSource));
        Assert.Equal(HazardTestKit.Level, top, 0.001f);
        Assert.Equal(GroundSource.LiquidSurface, topSource);
    }

    [Fact]
    public void AFlatLeg_GainsNoPoints_ButItsCornersAreSnapped()
    {
        using var kit = Kit(liquid: null);
        var start = new Vector3(X, Y, Ground + 0.1f);
        Vector3[] corners = [new(X + 20f, Y, Ground + 0.3f), new(X + 20f, Y + 25f, Ground + 0.22f)];

        List<Vector3> draped = GroundHeight.DrapePath(kit.Map.Collision, start, corners);

        Assert.Equal(2, draped.Count);
        Assert.All(draped, p => Assert.Equal(Ground + GroundHeight.StandOffset, p.Z, 0.001f));
        Assert.Equal(corners[^1].X, draped[^1].X);
        Assert.Equal(corners[^1].Y, draped[^1].Y);
    }

    private static float Hill(float x) => 1.2f * MathF.Sin(x / 6f);

    [Fact]
    public void ALongLegOverRollingGround_IsSplitWhereTheChordMissesAndNowhereElse()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        MapCollision collision = ModelOnly(world, (x, _) => Hill(x));
        var start = new Vector3(0f, 0f, Hill(0f));
        var end = new Vector3(60f, 0f, Hill(60f));

        List<Vector3> draped = GroundHeight.DrapePath(collision, start, [end]);

        Assert.InRange(draped.Count, 5, 32);
        Assert.Equal(end.X, draped[^1].X);
        Assert.Equal(end.Y, draped[^1].Y);
        float worst = WorstChordMiss(draped, start);
        Assert.True(worst < 0.25f, $"the sent polyline misses the ground by up to {worst:F2} yd");
        // The chord alone (a corner snapped, nothing inserted) misses by many yards.
        Assert.True(WorstChordMiss([end with { Z = Hill(60f) + GroundHeight.StandOffset }], start) > 1.0f);
    }

    private static float WorstChordMiss(IReadOnlyList<Vector3> polyline, Vector3 start)
    {
        float worst = 0f;
        Vector3 prev = start;
        foreach (Vector3 p in polyline)
        {
            int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(new(prev.X, prev.Y), new(p.X, p.Y)) / 0.5f));
            for (int j = 1; j <= n; j++)
            {
                Vector3 m = Vector3.Lerp(prev, p, j / (float)n);
                worst = MathF.Max(worst, MathF.Abs(m.Z - (Hill(m.X) + GroundHeight.StandOffset)));
            }

            prev = p;
        }

        return worst;
    }

    [Fact]
    public void TheCapOnPoints_KeepsTheCornersAndTheMostDeviatingPoints_InsteadOfCuttingTheLegShort()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        MapCollision collision = ModelOnly(world, (x, _) => Hill(x));
        var start = new Vector3(0f, 0f, Hill(0f));
        Vector3[] corners = [new(120f, 0f, Hill(120f)), new(240f, 0f, Hill(240f))];

        List<Vector3> few = GroundHeight.DrapePath(collision, start, corners, maxPoints: 7);
        List<Vector3> many = GroundHeight.DrapePath(collision, start, corners, maxPoints: 32);

        Assert.True(few.Count <= 7, $"{few.Count} points");
        Assert.True(many.Count <= 32, $"{many.Count} points");
        Assert.True(many.Count > few.Count);
        Assert.Equal(240f, few[^1].X);
        Assert.Equal(240f, many[^1].X);
        Assert.Contains(few, p => p.X == 120f);
    }

    /// <summary>A gently rippled leg (0.4 yd amplitude) with one ramp in it: level up to x = 100, then 3 yd of rise over 12 yd, then level at 3.</summary>
    private static float Ramp(float x) => Math.Clamp((x - 100f) * 0.25f, 0f, 3f) + (0.4f * MathF.Sin(x / 5f));

    private static float WorstMiss(IReadOnlyList<Vector3> polyline, Vector3 start, Func<float, float> ground)
    {
        float worst = 0f;
        Vector3 prev = start;
        foreach (Vector3 p in polyline)
        {
            int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(new(prev.X, prev.Y), new(p.X, p.Y)) / 0.5f));
            for (int j = 1; j <= n; j++)
            {
                Vector3 m = Vector3.Lerp(prev, p, j / (float)n);
                worst = MathF.Max(worst, MathF.Abs(m.Z - (ground(m.X) + GroundHeight.StandOffset)));
            }

            prev = p;
        }

        return worst;
    }

    [Fact]
    public void ALegWithOneRampAndSmallRipples_CappedBelowItsUncappedCount_StillKeepsTheRamp()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        MapCollision collision = ModelOnly(world, (x, _) => Ramp(x));
        var start = new Vector3(0f, 0f, Ramp(0f));
        Vector3[] corners = [new(200f, 0f, Ramp(200f))];

        List<Vector3> uncapped = GroundHeight.DrapePath(collision, start, corners, maxPoints: 1000);
        const int cap = 8;
        List<Vector3> capped = GroundHeight.DrapePath(collision, start, corners, maxPoints: cap);

        Assert.True(uncapped.Count > cap, $"the cap must bite or the test proves nothing (uncapped {uncapped.Count})");
        Assert.True(capped.Count <= cap, $"{capped.Count} points");
        Assert.Equal(200f, capped[^1].X);
        Assert.Equal(Ramp(200f) + GroundHeight.StandOffset, capped[^1].Z, 0.001f);
        // The bends at the foot (x = 100) and the top (x = 112) of the ramp stay sampled: a point close to each.
        Assert.Contains(capped, p => MathF.Abs(p.X - 100f) <= 4.01f);
        Assert.Contains(capped, p => MathF.Abs(p.X - 112f) <= 4.01f);
        float worst = WorstMiss(capped, start, Ramp);
        Assert.True(worst < 1.0f, $"the capped polyline misses the ramp by up to {worst:F2} yd (uncapped {uncapped.Count} points, capped {capped.Count})");
    }

    /// <summary>A pathfinder whose mesh is walkable everywhere at a surface that is a function of (x, y): a walk to a point answers that height.</summary>
    private sealed class MeshWalker(Func<float, float, float> surfaceAt) : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, end]);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(true, false, 1f, new Vector3(end.X, end.Y, surfaceAt(end.X, end.Y)));
    }

    [Fact]
    public void ASampleTheWalkerWindowMisses_IsLookedForWhereTheMeshWalkArrives()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        // The model floor is 0, except over x 50..70 where it is a trench 10 yd down: the chord (at 0) is 10 yd over it, beyond the 4 yd the
        // walker looks down. The mesh walk from the last sample arrives at the trench floor, so the sample is found there.
        static float Floor(float x) => x is > 50f and < 70f ? -10f : 0f;
        MapCollision collision = ModelOnly(world, (x, _) => Floor(x));
        WorldCollision.Of(world).Install(pathfinder: new MeshWalker((x, _) => Floor(x)));
        var start = new Vector3(0f, 0f, 0f);
        Vector3[] corners = [new(120f, 0f, 0f)];

        List<Vector3> draped = GroundHeight.DrapePath(collision, start, corners);

        Assert.Contains(draped, p => p.X is > 50f and < 70f && MathF.Abs(p.Z - (-10f + GroundHeight.StandOffset)) < 0.001f);
    }

    [Fact]
    public void ASampleInAnotherStorey_InsertsNothing_AndNoPointJumpsAFloor()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        // Ground at 0 except a floor 12 yd up around x = 20 (a bridge deck, a ceiling slab): the chord at x = 20 is nowhere near it.
        MapCollision collision = ModelOnly(world, (x, _) => x is > 18f and < 22f ? 12f : 0f);
        var start = new Vector3(0f, 0f, 0f);
        var end = new Vector3(40f, 0f, 0f);

        List<Vector3> draped = GroundHeight.DrapePath(collision, start, [end]);

        Assert.Single(draped);
        Assert.Equal(GroundHeight.StandOffset, draped[0].Z, 0.001f);
        Assert.All(draped, p => Assert.True(p.Z < 1f));
    }

    [Fact]
    public void ALegWithNoGroundData_IsReturnedAsGiven()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        MapCollision collision = world.GetMap(0).Collision;
        var start = new Vector3(3000f, 3000f, 10f);
        Vector3[] corners = [new(3030f, 3000f, 12f), new(3060f, 3030f, 14f)];

        List<Vector3> draped = GroundHeight.DrapePath(collision, start, corners);

        Assert.Equal(corners, draped);
    }

    [Fact]
    public void ARoofTwoYardsOverTheFloor_IsNotTakenForTheFloor_UnderAMeshCorner()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        // A walkable floor at 0 and a roof at 2.3. A mesh corner stands 0.6 over the floor (Recast rounds up, p95 +0.55): a probe from its
        // plain 2 yd window starts at 2.6 and takes the roof; the floor of a corner is looked for from at most MeshCeilingYards above it.
        WorldCollision.Of(world).Install(lineOfSight: new StackedFloors(0f, 2.3f));
        MapCollision collision = world.GetMap(0).Collision;
        PreloadAroundOrigin(world);
        var start = new Vector3(0f, 0f, GroundHeight.StandOffset); // standing on the floor
        Vector3 corner = new(20f, 0f, 0.6f); // a mesh corner 0.6 over it

        List<Vector3> asMesh = GroundHeight.DrapePath(collision, start, [corner], cornersOnMesh: true);
        List<Vector3> asAuthored = GroundHeight.DrapePath(collision, start, [corner], cornersOnMesh: false);

        Assert.Equal(GroundHeight.StandOffset, asMesh[^1].Z, 0.001f);
        Assert.Equal(2.3f + GroundHeight.StandOffset, asAuthored[^1].Z, 0.001f); // the lift the guard is for
        Assert.All(asMesh, p => Assert.True(p.Z < 1f, $"a point at {p.Z} is on the roof"));
    }

    [Fact]
    public void AMeshCornerUnderATerrainThatRisesAboveIt_StillTakesTheTerrain()
    {
        // The mesh dips under the terrain by up to 0.3 yd on a slope (measured): the corner guard must not push that point down to the mesh.
        using var kit = Kit(liquid: null);
        MapCollision collision = kit.Map.Collision;
        var corner = new Vector3(X, Y, Ground - 0.4f);

        List<Vector3> draped = GroundHeight.DrapePath(collision, corner, [corner with { X = X + 10f }], cornersOnMesh: true);

        Assert.Equal(Ground + GroundHeight.StandOffset, draped[^1].Z, 0.001f);
    }

    [Fact]
    public void WhetherAModelFloorOverTheTerrainIsAProp_IsDecidedByTheNearerEndsHeight_NotTheInterpolatedOne()
    {
        // Terrain at 20, a model floor at 21 (a WMO floor stepped onto). A sample looked for near an interpolated 19.85 (the chord rises from a
        // lower floor) is closer to the terrain than to the floor, and the interpolated height would call the floor a prop; the end it is
        // next to stands on that floor (21.05), so the floor it is.
        using var kit = Kit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: new ModelFloors((_, _) => 21f));
        MapCollision collision = kit.Map.Collision;

        Assert.True(GroundHeight.TrySnap(collision, X, Y, 19.85f, 19.85f, GroundHeight.Walker, out float asProp, out GroundSource propSource));
        Assert.Equal(GroundSource.Terrain, propSource);
        Assert.Equal(Ground + GroundHeight.StandOffset, asProp, 0.001f);

        Assert.True(GroundHeight.TrySnap(collision, X, Y, 19.85f, 21.05f, GroundHeight.Walker, out float asFloor, out GroundSource floorSource));
        Assert.Equal(GroundSource.Model, floorSource);
        Assert.Equal(21f + GroundHeight.StandOffset, asFloor, 0.001f);
    }

    [Fact]
    public void ALegThatAlreadyFillsTheCap_IsReturnedWithItsSnappedCornersOnly_WithoutSampling()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var floors = new ModelFloors((x, _) => Hill(x));
        WorldCollision.Of(world).Install(lineOfSight: floors);
        MapCollision collision = world.GetMap(0).Collision;
        PreloadAroundOrigin(world);
        var start = new Vector3(0f, 0f, Hill(0f));
        Vector3[] corners = [new(120f, 0f, Hill(120f)), new(240f, 0f, Hill(240f))];

        List<Vector3> draped = GroundHeight.DrapePath(collision, start, corners, maxPoints: 2);

        Assert.Equal(2, draped.Count);
        Assert.True(floors.Calls <= 4, $"{floors.Calls} model questions for two corners: the cap was not honoured before sampling");
    }

    /// <summary>The terrain tiles around the origin in memory, as the grids of a player nearby would have them: a drape reads no terrain tile from disk.</summary>
    private static void PreloadAroundOrigin(WorldRuntime world)
    {
        for (int tx = 31; tx <= 33; tx++)
        {
            for (int ty = 31; ty <= 33; ty++)
            {
                world.GetMap(0).Terrain.GetTile(tx, ty);
            }
        }
    }

    [Fact]
    public void AModelFloorOverTheTerrain_IsAPropUntilTheLegReachesIt_ForTheSamplesNextToTheSampleBeforeThem()
    {
        // Terrain at 20, a model floor at 21 from 5 yd on, a leg from 20.05 to a corner on that floor (21.05). A sample is judged by the height of
        // the sample before it (or the corner, when that is nearer), which is on the terrain: a floor 1 yd over it is a prop beside the path, so
        // the samples keep the terrain and the leg steps up at its corner. Judged by the chord's height (20.55 at 9 yd) the floor would be
        // nearer than the terrain and the sample at 9 yd would stand on it, a step the real data does not show (props beside paths).
        using var kit = Kit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: new ModelFloors((x, _) => x > X + 5f ? 21f : null));
        MapCollision collision = kit.Map.Collision;
        var start = new Vector3(X, Y, Ground + GroundHeight.StandOffset);
        Vector3[] corners = [new(X + 10f, Y, 21f + GroundHeight.StandOffset)];

        List<Vector3> draped = GroundHeight.DrapePath(collision, start, corners, step: 1f, tolerance: 0.05f);

        Assert.Contains(draped, p => MathF.Abs(p.X - (X + 9f)) < 0.01f && MathF.Abs(p.Z - (Ground + GroundHeight.StandOffset)) < 0.01f);
        Assert.Equal(21f + GroundHeight.StandOffset, draped[^1].Z, 0.001f);
    }
}
