using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// A creature that cannot swim (<c>InhabitType</c> 1, the classic-db value of most beasts) chasing a target that swims off a shore, on the
/// real navmesh (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, see <see cref="RealTerrainDataTests"/>). The water polygons under the target are not in
/// the walker's filter, and the 3 yd endpoint column found nothing else, so every such chase answered NoPath: the creature froze where it
/// stood and reported <c>creature/path-failed</c> (live 2026-10-16: 669 of the 687 distinct chase failures had the target in water). cmangos
/// looks for the destination's polygon in a 10 yd box (<c>FarPolySearchBound</c>) and walks the incomplete path to its nearest point
/// (<c>PathFinder::BuildPolyPath</c> farFromPoly); the chase then marks the target unreachable
/// (<c>ChaseMovementGenerator::DispatchSplineToPosition</c> / <c>IsReachablePositionToTarget</c>) and the unreachable-target evade runs.
/// The coordinates are live anomaly reports.
/// </summary>
public sealed class ChaseIntoWaterRealTerrainTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    /// <summary>Live path-failed chases (entry, map, chaser, target in water): Mangy Wolf in Elwynn, Durotar Tiger and Bloodtalon Taillasher on the Durotar coast, Prowler in Redridge.</summary>
    public static TheoryData<uint, uint, float, float, float, float, float, float> LiveWaterChases => new()
    {
        { 525u, 0u, -9534.8f, -388.4f, 58.4f, -9525.7f, -401.3f, 53.5f },
        { 3121u, 1u, -1088.7f, -5106.1f, -0.5f, -1083.0f, -5103.4f, -2.4f },
        { 3122u, 1u, -1033.4f, -5346.5f, 0.9f, -1030.6f, -5330.8f, -1.3f },
        { 118u, 0u, -9727.1f, -1066.6f, 35.5f, -9735.8f, -1058.4f, 33.3f },
    };

    private static NavMeshPathfinder LoadNav(uint mapId, float x, float y)
    {
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
        for (int i = tx - 1; i <= tx + 1; i++)
        {
            for (int j = ty - 1; j <= ty + 1; j++)
            {
                nav.LoadTile(mapId, i, j);
            }
        }

        return nav;
    }

    private static float Distance2D(Vector3 a, Vector3 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    [RealTerrainFact]
    public void AWalkerChasingASwimmer_RunsToTheWatersEdge_AndCallsItUnreachable_WithoutAPathFailure()
    {
        // Live: "creature/path-failed creature entry 525 spawn 80766: No path for Chase from (-9534.8, -388.4, 58.4) to (-9525.7, -401.3, 53.5) on map 0".
        const uint entry = 949201;
        var spawn = new Vector3(-9534.8f, -388.4f, 58.4f);
        var swimmer = new Vector3(-9525.7f, -401.3f, 53.5f);

        CreatureTemplate walker = Template(entry) with { InhabitType = 1 };
        CreatureContent content = new([walker], [Spawn(1, entry, spawn.X, spawn.Y, spawn.Z)], [], [], [], CreatureAiContent.Empty);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using WorldRuntime owned = world;
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(pathfinder: LoadNav(0, spawn.X, spawn.Y));

        (Player player, _) = AddPlayer(world, 1, swimmer.X, swimmer.Y);
        player.Relocate(swimmer.X, swimmer.Y, swimmer.Z, 0, 0);
        world.RunTick(0);
        Creature wolf = Assert.Single(system.Creatures);

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));
        float before = Distance2D(new Vector3(wolf.X, wolf.Y, wolf.Z), swimmer);
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.Equal(MovementGeneratorType.Chase, wolf.Motion.CurrentType);

        Run(world, 2_500);

        var at = new Vector3(wolf.X, wolf.Y, wolf.Z);
        float after = Distance2D(at, swimmer);
        output.WriteLine($"chaser {spawn} -> {at}: {before:F1} yd -> {after:F1} yd from the swimmer at {swimmer}; reachable {wolf.Motion.IsReachable}");
        Assert.True(Distance2D(spawn, at) > 3f, $"the chaser stayed at {at}: it found no path towards the water");
        Assert.True(after < before - 3f, $"the chaser did not close in: {before:F1} -> {after:F1} yd");
        Assert.False(wolf.Motion.IsReachable);
        Assert.DoesNotContain(capture.Notices, n => n.Key == "creature/path-failed");
    }

    [RealTerrainTheory]
    [MemberData(nameof(LiveWaterChases))]
    public void ChaseOptions_FindAPartialPathTowardsATargetInWater(uint entry, uint mapId, float sx, float sy, float sz, float tx, float ty, float tz)
    {
        var start = new Vector3(sx, sy, sz);
        var target = new Vector3(tx, ty, tz);
        NavMeshPathfinder nav = LoadNav(mapId, sx, sy);
        var plain = new PathOptions { Mover = PathMover.FromInhabit(1) };
        var chase = plain with { WideEndSearch = true };

        // The walker's own endpoint search finds nothing under a swimmer (the live NoPath); a swimmer's finds the water.
        Assert.Equal(PathType.NoPath, nav.FindPath(mapId, start, target, plain).Type);
        Assert.True(nav.FindPath(mapId, start, target, new PathOptions { Mover = PathMover.FromInhabit(3) }).HasPath);

        PathResult path = nav.FindPath(mapId, start, target, chase);
        output.WriteLine($"entry {entry}: {path.Type}, {path.Points.Count} points, end {path.End}, {Distance2D(path.End, target):F1} yd short of {target}");
        Assert.True(path.HasPath);
        Assert.NotEqual(0, (int)(path.Type & PathType.Incomplete));
        Assert.True(Distance2D(path.End, target) < Distance2D(start, target), "the partial path does not lead towards the target");
    }

    /// <summary>
    /// Live path-failed chases after the wide end search (2026-10-18): the swimmer is past the 10 yd box, so even the chase options find
    /// no polygon a walker may use (cmangos and vmangos answer NOPATH here too).
    /// </summary>
    public static TheoryData<float, float, float, float, float, float> FarWaterChases => new()
    {
        { -1344.8f, -5419.1f, -0.5f, -1359.4f, -5407.4f, -4.6f },
        { -810.7f, -5398.9f, -0.6f, -815.4f, -5415.2f, -6.7f },
    };

    [RealTerrainTheory]
    [MemberData(nameof(FarWaterChases))]
    public void AWalkerWhoseTargetSwimsOutOfItsReach_IsNotAPathFailure(float sx, float sy, float sz, float tx, float ty, float tz)
    {
        const uint entry = 949202;
        var spawn = new Vector3(sx, sy, sz);
        var swimmer = new Vector3(tx, ty, tz);
        NavMeshPathfinder nav = LoadNav(1, spawn.X, spawn.Y);
        Assert.Equal(PathType.NoPath, nav.FindPath(1, spawn, swimmer, new PathOptions { Mover = PathMover.FromInhabit(1), WideEndSearch = true }).Type);

        CreatureTemplate walker = Template(entry) with { InhabitType = 1 };
        CreatureContent content = new([walker], [Spawn(1, entry, spawn.X, spawn.Y, spawn.Z, mapId: 1)], [], [], [], CreatureAiContent.Empty);
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        Map map = world.GetMap(1, 0);
        var system = new CreatureMapSystem(map, content, null, random: new Random(1), aiServices: new CreatureAiServices { Hostility = new AlwaysHostile() });
        map.AddUpdater(system);
        WorldCollision.Of(world).Install(pathfinder: nav);
        Assert.NotEqual(LiquidStatus.NoWater, map.GetLiquidStatus(swimmer.X, swimmer.Y, swimmer.Z, LiquidTypeFlags.AllLiquids, out _) & (LiquidStatus.InWater | LiquidStatus.UnderWater));

        Player player = TestWorld.CreatePlayer(1, swimmer.X, swimmer.Y, new FakeSession(1));
        player.MapId = 1;
        player.Relocate(swimmer.X, swimmer.Y, swimmer.Z, 0, 0);
        map.AddPlayer(player);
        world.RunTick(0);
        Creature tiger = Assert.Single(system.Creatures);

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));
        map.Combat.DealDamage(player, tiger, 1, direct: false);
        Run(world, 2_500);

        Assert.DoesNotContain(capture.Notices, n => n.Key == "creature/path-failed");
    }
}
