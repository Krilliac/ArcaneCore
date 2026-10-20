using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.Locomotion;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="GroundHeight"/> on synthetic ground: a flat one-tile terrain (<see cref="HazardKit"/>, ground at z 20) with scripted model and
/// mesh services, and a model-only world for the leg tests. The same helper on the real extraction is checked in
/// <c>GroundSnapRealTerrainTests</c> (gated on <c>ARCANECORE_TEST_TERRAIN_DIR</c>).
/// </summary>
public sealed class GroundHeightTests
{
    private const float X = HazardKit.X;
    private const float Y = HazardKit.Y;
    private const float Ground = HazardKit.Ground;

    /// <summary>Model floors from a function of (x, y); null where the function has none.</summary>
    private sealed class ModelFloors(Func<float, float, float?> floorAt) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => floorAt(x, y) is { } floor && floor <= z && floor >= z - maxSearchDistance ? floor : null;

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
    private static HazardKit Kit(LiquidTypeFlags? liquid)
    {
        var kit = new HazardKit(liquid);
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
        float z = HazardKit.Level - 5f;

        var bottom = new GroundQuery(Up: 10f, Down: 80f, LiquidMode.Bottom, true, true, false);
        Assert.True(GroundHeight.TrySnap(collision, X, Y, z, bottom, out float floor, out GroundSource floorSource));
        Assert.Equal(Ground + GroundHeight.StandOffset, floor, 0.001f);
        Assert.Equal(GroundSource.Terrain, floorSource);

        var surface = bottom with { Liquid = LiquidMode.Surface };
        Assert.True(GroundHeight.TrySnap(collision, X, Y, z, surface, out float top, out GroundSource topSource));
        Assert.Equal(HazardKit.Level, top, 0.001f);
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
    public void TheCapOnPoints_CoarsensTheStep_InsteadOfCuttingTheLegShort()
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
}
