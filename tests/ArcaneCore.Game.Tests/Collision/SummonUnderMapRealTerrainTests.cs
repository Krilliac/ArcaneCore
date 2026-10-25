using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.ClassSpells.Totems;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Summons and pets that end up below a floor, on real extracted data (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, see <see cref="RealTerrainDataTests"/>).
/// Three live creature/under-map reports: a Healing Stream Totem at the edge of the Karazhan crypt floor (map 0), a Searing Totem placed in
/// a hole in the Bael'dun Keep floor (map 1, entry 7402) and a warlock imp 6.6 yd under the Warsong Gulch Alliance tunnel floor (map 489).
/// Coordinates are the logged positions; the floor heights are read from the navmesh.
/// </summary>
public sealed class SummonUnderMapRealTerrainTests(ITestOutputHelper output)
{
    private const float KaraX = -11195.9f;
    private const float KaraY = -2144.3f;
    private const float KaraZ = 22.8f;

    private const uint Wsg = 489u;
    private const float WsgTunnelY = 1460.9f;

    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    /// <summary>Installs the real vmaps and navmesh on <paramref name="world"/> and loads the tiles around the point (plus the terrain tiles).</summary>
    private static void InstallRealCollision(WorldRuntime world, uint mapId, float x, float y)
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                vmaps.LoadTile(mapId, tx + dx, ty + dy);
                nav.LoadTile(mapId, tx + dx, ty + dy);
                world.GetMap(mapId).Terrain.GetTile(tx + dx, ty + dy);
            }
        }
    }

    private static bool HasFloor(MapCollision collision, float x, float y, float z, float tolerance)
        => collision.TryGetMeshFloor(x, y, z, tolerance, tolerance, out _);

    private static bool IsUnderMap(MapCollision collision, float x, float y, float z)
        => collision.ClassifyUnderMap(x, y, z, 8f).Kind == UnderMapKind.UnderMap;

    private static float TunnelFloor(MapCollision collision, float x, float y)
    {
        for (float h = 300f; h < 356f; h += 0.5f)
        {
            if (collision.TryGetMeshFloor(x, y, h, 0.5f, 0.5f, out float m))
            {
                return m;
            }
        }

        return float.NaN;
    }

    private TotemTestKit KaraKit(out Player shaman, float x, float y, float z, float orientation = 0f)
    {
        var kit = new TotemTestKit(terrainDirectory: Root);
        InstallRealCollision(kit.World, 0u, x, y);
        (shaman, _) = kit.AddPlayer(1, x, y);
        shaman.Orientation = orientation;
        shaman.Relocate(x, y, z, orientation, 0);
        return kit;
    }

    [RealTerrainTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void SafeClosePoint_ForAShamanJustPastTheCryptFloorEdge_NeverReturnsAPointUnderTheMap(int octant)
    {
        // Entry 3909's logged point: nothing under it, the Karazhan crypt floor starts 1 yd to the east and to the south-east; the west is a void.
        using TotemTestKit kit = KaraKit(out Player shaman, KaraX, KaraY, KaraZ);
        MapCollision collision = shaman.Map!.Collision;
        Assert.True(IsUnderMap(collision, KaraX, KaraY, KaraZ), "the logged point itself is under the map (precondition)");

        float angle = octant * (MathF.PI / 4f);
        (float x, float y, float z) = SummonService.SafeClosePoint(shaman, kit.Creatures, 0.5f, 2f, angle);

        output.WriteLine($"octant {octant}: ({x:F2}, {y:F2}, {z:F2})");
        Assert.False(IsUnderMap(collision, x, y, z), $"({x:F2}, {y:F2}, {z:F2}) classifies under the map");
        Assert.True(HasFloor(collision, x, y, z, 0.6f), $"no navmesh floor within 0.6 yd of ({x:F2}, {y:F2}, {z:F2})");
    }

    [RealTerrainTheory]
    [InlineData(0f)]
    [InlineData(1.5707964f)]
    [InlineData(3.1415927f)]
    [InlineData(4.712389f)]
    public void HealingStreamTotem_SummonedByAShamanAtTheCryptFloorEdge_IsNotUnderTheMap(float orientation)
    {
        using TotemTestKit kit = KaraKit(out Player shaman, KaraX, KaraY, KaraZ, orientation);
        MapCollision collision = shaman.Map!.Collision;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(shaman, TotemTestKit.WaterTotemSummon));

        Creature totem = Assert.IsType<Creature>(kit.Totems.GetTotem(shaman, TotemSlot.Water));
        output.WriteLine($"orientation {orientation:F2}: totem ({totem.X:F2}, {totem.Y:F2}, {totem.Z:F2})");
        Assert.False(IsUnderMap(collision, totem.X, totem.Y, totem.Z), "the totem is under the map");
        Assert.True(HasFloor(collision, totem.X, totem.Y, totem.Z, 0.6f), "no navmesh floor under the totem");
    }

    [RealTerrainTheory]
    [InlineData(6f, -6f)]
    [InlineData(8f, -9f)]
    public void SafeClosePoint_ForAnOwnerStandingWellInsideTheCryptFloor_KeepsTheOrdinaryPlacement(float dx, float dy)
    {
        float ownerX = KaraX + dx;
        float ownerY = KaraY + dy;
        using TotemTestKit kit = KaraKit(out Player shaman, ownerX, ownerY, KaraZ);
        MapCollision collision = shaman.Map!.Collision;
        Assert.True(collision.TryGetMeshFloor(ownerX, ownerY, KaraZ, 1f, 1f, out float floor), "the owner's point is on the navmesh floor (precondition)");
        shaman.Relocate(ownerX, ownerY, floor, 0f, 0);

        for (int octant = 0; octant < 8; octant++)
        {
            float angle = octant * (MathF.PI / 4f);
            (float ex, float ey) = SummonService.ClosePoint(shaman, 0.5f, 2f, angle);
            (float x, float y, float z) = SummonService.SafeClosePoint(shaman, kit.Creatures, 0.5f, 2f, angle);

            Assert.Equal(ex, x, 3);
            Assert.Equal(ey, y, 3);
            Assert.InRange(MathF.Abs(z - floor), 0f, 1.5f);
            Assert.False(IsUnderMap(collision, x, y, z));
        }
    }

    [RealTerrainFact]
    public void SafeClosePoint_ForAnOwnerBesideTheSearingTotemHole_NeverReturnsAPointInTheHole()
    {
        // Regression guard (not a reproduction: the owner's point was already the fallback before): entry 7402's spawn (-4057.6, -2397.5, 126.2)
        // sits in a 6x8 yd hole of the Bael'dun Keep floor; the owner stands on the floor 5 yd west of it.
        const float HoleX = -4057.6f;
        const float HoleY = -2397.5f;
        const uint Kalimdor = 1u;
        using WorldRuntime world = TestWorld.CreateRuntime();
        InstallRealCollision(world, Kalimdor, HoleX, HoleY);
        Map map = world.GetMap(Kalimdor);
        MapCollision collision = map.Collision;
        Assert.True(IsUnderMap(collision, HoleX, HoleY, 126.2f), "the logged spawn point is under the map (precondition)");

        float ownerX = HoleX - 5f;
        float ownerY = HoleY - 1f;
        Assert.True(collision.TryGetMeshFloor(ownerX, ownerY, 126.2f, 1f, 1f, out float floor), "the owner's point is on the navmesh floor (precondition)");
        Player owner = TestWorld.CreatePlayer(2, ownerX, ownerY, new FakeSession(2), Kalimdor);
        world.AddPlayer(owner);
        world.RunTick(0);
        owner.Relocate(ownerX, ownerY, floor, 0f, 0);
        var creatures = new CreatureMapSystem(map, Content([Template(949201)], []), random: new Random(1));

        int slotsInTheHole = 0;
        for (int octant = 0; octant < 8; octant++)
        {
            (float rx, float ry) = SummonService.ClosePoint(owner, 0.5f, 2f, octant * (MathF.PI / 4f));
            if (IsUnderMap(collision, rx, ry, floor))
            {
                slotsInTheHole++;
            }
        }

        Assert.True(slotsInTheHole > 0, "at least one raw slot point lands in the hole, so the guard is exercised (precondition)");

        for (int octant = 0; octant < 8; octant++)
        {
            (float x, float y, float z) = SummonService.SafeClosePoint(owner, creatures, 0.5f, 2f, octant * (MathF.PI / 4f));
            Assert.False(IsUnderMap(collision, x, y, z), $"octant {octant}: ({x:F2}, {y:F2}, {z:F2}) is in the hole");
        }
    }

    [RealTerrainFact]
    public void SafeClosePoint_ForAnOwnerStandingInTheSearingTotemHole_NeverReturnsAPointInTheHole()
    {
        // Reproduces entry 7402: the caster itself is in the hole, so the owner-point fallback used to put the totem at z=126.2.
        const float HoleX = -4057.6f;
        const float HoleY = -2397.5f;
        const uint Kalimdor = 1u;
        using WorldRuntime world = TestWorld.CreateRuntime();
        InstallRealCollision(world, Kalimdor, HoleX, HoleY);
        Map map = world.GetMap(Kalimdor);
        MapCollision collision = map.Collision;
        Assert.True(IsUnderMap(collision, HoleX, HoleY, 126.2f), "the owner's point is under the map (precondition)");

        Player owner = TestWorld.CreatePlayer(2, HoleX, HoleY, new FakeSession(2), Kalimdor);
        world.AddPlayer(owner);
        world.RunTick(0);
        owner.Relocate(HoleX, HoleY, 126.2f, 0f, 0);
        var creatures = new CreatureMapSystem(map, Content([Template(949201)], []), random: new Random(1));

        for (int octant = 0; octant < 8; octant++)
        {
            (float x, float y, float z) = SummonService.SafeClosePoint(owner, creatures, 0.5f, 2f, octant * (MathF.PI / 4f));
            output.WriteLine($"octant {octant}: ({x:F2}, {y:F2}, {z:F2})");
            Assert.False(IsUnderMap(collision, x, y, z), $"octant {octant}: ({x:F2}, {y:F2}, {z:F2}) is in the hole");
        }
    }

    [RealTerrainFact]
    public void AnImp_6Point6YardsUnderTheWsgTunnelFloor_IsLiftedOntoTheFloor_AndReportedOnce()
    {
        // Logged position of two different warlock imps: terrain 358.3, ceiling 351.4, tunnel floor 342.6, creature at 335.9.
        const uint Entry = 949210;
        const float X = 1448.2f;
        const float Z = 335.9f;
        using WorldRuntime world = TestWorld.CreateRuntime();
        InstallRealCollision(world, Wsg, X, WsgTunnelY);
        Map map = world.GetMap(Wsg);
        float floor = TunnelFloor(map.Collision, X, WsgTunnelY);
        output.WriteLine($"tunnel floor at the imp's column: {floor:F2}");
        Assert.InRange(floor, 342.4f, 342.8f);

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "creature/under-map" && n.Subject.StartsWith($"creature entry {Entry} ", StringComparison.Ordinal));
        var system = new CreatureMapSystem(map, Content([Template(Entry)], [Spawn(1, Entry, X, WsgTunnelY, Z, Wsg)]), random: new Random(1));
        map.AddUpdater(system);
        world.AddPlayer(TestWorld.CreatePlayer(1, X, WsgTunnelY, new FakeSession(1), Wsg));
        world.RunTick(0);
        Run(world, 2_000);

        Creature imp = Assert.Single(system.Creatures);
        output.WriteLine($"imp ended at ({imp.X:F2}, {imp.Y:F2}, {imp.Z:F2}); notices {capture.Notices.Count}");
        Assert.InRange(imp.Z, floor - 0.5f, floor + 0.5f);
        Assert.Single(capture.Notices);
    }

    [RealTerrainFact]
    public void AnImpThatAMoverKeepsPuttingBackUnderTheTunnelFloor_IsLiftedEachTime_AndReportedOnce()
    {
        // Whatever put the imp under the floor may do it again: every re-placement is healed at the next sample, but the episode stays
        // open until a sample sees the creature on the floor, so a mover that undoes every lift does not turn one report into one per sample.
        const uint Entry = 949210;
        const float X = 1448.2f;
        const float Z = 335.9f;
        using WorldRuntime world = TestWorld.CreateRuntime();
        InstallRealCollision(world, Wsg, X, WsgTunnelY);
        Map map = world.GetMap(Wsg);
        float floor = TunnelFloor(map.Collision, X, WsgTunnelY);

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "creature/under-map" && n.Subject.StartsWith($"creature entry {Entry} ", StringComparison.Ordinal));
        var system = new CreatureMapSystem(map, Content([Template(Entry)], [Spawn(1, Entry, X, WsgTunnelY, Z, Wsg)]), random: new Random(1));
        map.AddUpdater(system);
        world.AddPlayer(TestWorld.CreatePlayer(1, X, WsgTunnelY, new FakeSession(1), Wsg));
        world.RunTick(0);
        Creature imp = Assert.Single(system.Creatures);

        int lifts = 0;
        for (int i = 0; i < 240; i++)
        {
            world.RunTick(100);
            if (imp.Z > floor - 0.5f)
            {
                lifts++;
                imp.Relocate(X, WsgTunnelY, Z, imp.Orientation, 0);
            }
        }

        output.WriteLine($"lifts {lifts}, notices {capture.Notices.Count}");
        Assert.True(lifts >= 4, "the imp was put back under the floor and lifted again several times");
        Assert.Single(capture.Notices);
    }

    [RealTerrainTheory]
    [InlineData(1390f, 1470f, 0.7f, 100u, false)]
    [InlineData(1390f, 1470f, 0.7f, 100u, true)]
    [InlineData(1470f, 1390f, 0.7f, 100u, false)]
    [InlineData(1470f, 1390f, 0.7f, 100u, true)]
    [InlineData(1390f, 1470f, 7f, 1000u, false)]
    [InlineData(1390f, 1470f, 7f, 1000u, true)]
    [InlineData(1390f, 1470f, 25f, 2000u, false)]
    [InlineData(1390f, 1470f, 25f, 2000u, true)]
    public void ACreatureFollowingItsOwnerAlongTheWsgTunnelRamp_StaysOnTheFloor_AndIsNeverLifted(float fromX, float toX, float ownerStep, uint tickMs, bool pathJobs)
    {
        const uint Entry = 949211;
        using WorldRuntime world = TestWorld.CreateRuntime();
        InstallRealCollision(world, Wsg, 1450f, 1460f);
        Map map = world.GetMap(Wsg);
        MapCollision collision = map.Collision;

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "creature/under-map" && n.Subject.StartsWith($"creature entry {Entry} ", StringComparison.Ordinal));
        var options = new CreatureOptions();
        options.Movement.PathJobs = pathJobs;
        options.Movement.PathJobsFollow = pathJobs;
        float startZ = TunnelFloor(collision, fromX - 3f, WsgTunnelY);
        var system = new CreatureMapSystem(map, Content([Template(Entry) with { InhabitType = 1 }], [Spawn(1, Entry, fromX - 3f, WsgTunnelY, startZ, Wsg)]), options, random: new Random(1), aiServices: new CreatureAiServices());
        using NavJobSystem? jobs = pathJobs ? new NavJobSystem(threads: 2) : null;
        system.PathJobs = jobs;
        map.AddUpdater(system);
        Player owner = TestWorld.CreatePlayer(1, fromX, WsgTunnelY, new FakeSession(1), Wsg);
        float direction = MathF.Sign(toX - fromX);
        owner.Relocate(fromX, WsgTunnelY, TunnelFloor(collision, fromX, WsgTunnelY) + 0.05f, direction > 0 ? 0f : MathF.PI, 0);
        map.AddPlayer(owner);
        world.RunTick(0);
        Creature pet = Assert.Single(system.Creatures);
        pet.Motion.MoveFollow(owner, 2f, MathF.PI * 0.75f);

        float worst = 0f;
        int samples = 0;
        float ownerX = fromX;
        for (int i = 0; i < 400; i++)
        {
            if ((direction > 0 && ownerX < toX) || (direction < 0 && ownerX > toX))
            {
                ownerX += direction * ownerStep;
                owner.Relocate(ownerX, WsgTunnelY, TunnelFloor(collision, ownerX, WsgTunnelY) + 0.05f, direction > 0 ? 0f : MathF.PI, 0);
            }

            world.RunTick(tickMs);
            float floor = TunnelFloor(collision, pet.X, pet.Y);
            if (float.IsFinite(floor))
            {
                worst = MathF.Max(worst, MathF.Abs(pet.Z - floor));
                samples++;
            }
        }

        output.WriteLine($"owner {fromX}->{toX} step {ownerStep} tick {tickMs} pathJobs {pathJobs}: pet ended ({pet.X:F1}, {pet.Y:F1}, {pet.Z:F2}), worst |z - tunnel floor| {worst:F2}");
        Assert.True(samples > 0, "the pet was sampled over the tunnel floor at least once");
        float toOwner = MathF.Sqrt(((pet.X - owner.X) * (pet.X - owner.X)) + ((pet.Y - owner.Y) * (pet.Y - owner.Y)));
        Assert.True(toOwner <= 6f, $"the pet followed its owner to the end of the ramp (ended {toOwner:F1} yd away)");
        Assert.InRange(worst, 0f, 1.25f);
        Assert.Empty(capture.Notices);
    }

    [RealTerrainFact]
    public void APathFromFlagRoomToFlagRoom_DrapedForACreature_NeverPassesUnderTheAllianceTunnelFloor()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        InstallRealCollision(world, Wsg, 1230f, 1460f); // 3x3 tiles cover both flag rooms
        MapCollision collision = world.GetMap(Wsg).Collision;
        var start = new Vector3(916.0f, 1434.8f, 345.4f);
        PathResult path = collision.FindPath(start, new Vector3(1540.0f, 1481.0f, 352.7f));
        Assert.True(path.HasPath);
        List<Vector3> draped = GroundHeight.DrapePath(
            collision,
            start,
            [.. path.Points.Skip(1)],
            step: CreatureMapSystem.GroundSampleYards,
            tolerance: CreatureMapSystem.GroundToleranceYards,
            query: GroundHeight.Walker,
            modelTolerance: CreatureMapSystem.GroundModelToleranceYards,
            cornersOnMesh: true);
        List<Vector3> uncapped = GroundHeight.DrapePath(
            collision,
            start,
            [.. path.Points.Skip(1)],
            step: CreatureMapSystem.GroundSampleYards,
            tolerance: CreatureMapSystem.GroundToleranceYards,
            maxPoints: 100000,
            query: GroundHeight.Walker,
            modelTolerance: CreatureMapSystem.GroundModelToleranceYards,
            cornersOnMesh: true);
        Assert.True(uncapped.Count > 32, $"the path must need more than the 32-point cap or the test proves nothing (uncapped {uncapped.Count})");
        Assert.True(draped.Count <= 32, $"the capped drape stays within the cap (got {draped.Count})");
        Assert.Contains(draped, p => p.X < 1385f);
        Assert.Contains(draped, p => p.X > 1490f);
        float worst = float.NegativeInfinity;
        int inWindow = 0;
        Vector3 prev = start;
        foreach (Vector3 p in draped)
        {
            for (float t = 0f; t <= 1f; t += 0.01f)
            {
                Vector3 s = Vector3.Lerp(prev, p, t);
                if (s.X is < 1385f or > 1490f || MathF.Abs(s.Y - 1461f) > 3f)
                {
                    continue;
                }

                float floor = TunnelFloor(collision, s.X, s.Y);
                if (float.IsFinite(floor))
                {
                    worst = MathF.Max(worst, floor - s.Z);
                    inWindow++;
                }
            }

            prev = p;
        }

        output.WriteLine($"{draped.Count} points (uncapped {uncapped.Count}); {inWindow} tunnel samples; worst depth under the tunnel floor {worst:F2}");
        Assert.True(inWindow >= 100, $"the draped path was sampled through the tunnel (only {inWindow} samples with a tunnel floor)");
        Assert.InRange(worst, -10f, 1.25f); // HEAD measured: 7.06 at x 1439.6
    }
}
