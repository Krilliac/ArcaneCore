using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.GridTerrain;

[Collection("World tick load")]
public sealed class ActiveCellReactivationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(16, 128, false)]
    [InlineData(1, 512, true)]
    public void ReturningPlayersDoNotCompleteDormantPatrolSplines(int hubs, int perHub, bool navmesh)
    {
        string? terrain = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
        // Same real Northshire endpoints as WorldTickLoadTests; the 512 overlapping patrols are a stress fixture.
        Vector3 origin = navmesh ? new(-8949.95f, -132.493f, 83.5312f) : new(150, 150, 83.5f);
        Vector3 destination = navmesh ? new(-8902.59f, -162.606f, 82.0223f) : origin + new Vector3(80, 0, 0);
        IPathfinder backend = StraightLinePathfinder.Instance;
        if (navmesh && terrain is not null)
        {
            var mesh = new NavMeshPathfinder(Path.Combine(terrain, "mmaps"));
            (int x, int y) = TerrainTile.TileOf(origin.X, origin.Y)!.Value;
            Assert.True(mesh.LoadTile(0, x, y));
            PathResult probe = mesh.FindPath(0, origin, destination);
            Assert.Equal(0, (int)(probe.Type & (PathType.NoPath | PathType.NotUsingPath)));
            backend = mesh;
        }
        // Synthetic patrols, not invented world content. Long first legs are in progress when players leave.
        var spawns = new List<CreatureSpawn>();
        var nodes = new List<(uint, CreatureWaypoint)>();
        for (int h = 0; h < hubs; h++)
        for (int i = 0; i < perHub; i++)
        {
            uint id = (uint)(h * perHub + i + 1);
            Vector3 offset = new(h * 533.33333f, navmesh ? 0 : i % 8, 0);
            Vector3 from = origin + offset, to = destination + offset;
            spawns.Add(Spawn(id, WolfEntry, from.X, from.Y, movementType: 2) with { Z = from.Z });
            nodes.Add((id, new(1, to.X, to.Y, to.Z, 100, 0)));
            nodes.Add((id, new(2, from.X, from.Y, from.Z, 100, 0)));
        }

        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(Content([Template()], spawns, nodes));
        using (world)
        {
            world.UseManualClock();
            world.Options.Maps.GridUnload = false; // isolate cell activation from grid reload / disk I/O
            var paths = new CountingPaths(backend);
            WorldCollision.Of(world).Install(pathfinder: paths);
            var players = new List<Player>();
            for (int h = 0; h < hubs; h++)
            {
                Player player = TestWorld.CreatePlayer((uint)h + 1, h * 533.33333f + origin.X, origin.Y, new FakeSession());
                players.Add(player);
                world.AddPlayer(player);
            }

            // Every patrol must be mid-leg before the players leave. On a real mesh the per-update path budget
            // (CreaturePathBudget.Default, 64 A* searches) defers the rest of the 512 first legs to later updates;
            // only straight-line "searches" cost nothing. Tick until the deferred legs have all started.
            int warmupTicks = 0;
            do
            {
                world.RunTick(50);
                warmupTicks++;
            }
            while (warmupTicks < 64 && system.Creatures.Any(c => c.Spline is null));
            output.WriteLine($"warm-up: {warmupTicks} tick(s), deferred first legs={system.DeferredPathSearches}");
            Assert.Equal(hubs * perHub, system.Creatures.Count);
            Assert.All(system.Creatures, c => Assert.NotNull(c.Spline));
            var positions = system.Creatures.ToDictionary(c => c.Guid, c => c.X);
            var splines = system.Creatures.ToDictionary(c => c.Guid, c => c.Spline!.Id);
            foreach (Player player in players) player.Relocate(player.X, origin.Y - 300, player.Z, 0, 0);
            world.RunTick(600_000); // ten minutes dormant, one explicit manual-clock step
            Assert.All(system.Creatures, c => Assert.Equal(positions[c.Guid], c.X));
            long loadedBeforeReturn = map.Grids.Counters.Loaded;
            foreach (Player player in players) player.Relocate(player.X, origin.Y, player.Z, 0, 0);

            paths.Calls = 0;
            paths.Micros = 0;
            long simulation = 0, creatureUs = 0;
            long loads = 0;
            long start = Stopwatch.GetTimestamp();
            world.RunTick(50, (_, d) =>
            {
                simulation = d.SimulationMicros;
                creatureUs = d.Updaters.Single(u => ReferenceEquals(u.Updater, system)).Micros;
                loads = d.Grids.Loaded;
            });
            output.WriteLine($"reactivation: backend={backend.GetType().Name}, creatures={system.Creatures.Count}, hubs={hubs}, tick={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3}ms, simulation={simulation / 1000d:F3}ms, creatures={creatureUs / 1000d:F3}ms, path queries={paths.Calls}, path time={paths.Micros / 1000d:F3}ms, grid loads={loads}");
            Assert.Equal(0, loads);
            Assert.Equal(loadedBeforeReturn, map.Grids.Counters.Loaded); // includes the player relocations before RunTick
            Assert.Equal(0, paths.Calls); // RED: every dormant patrol falsely arrives and requests its next path
            Assert.All(system.Creatures, c =>
            {
                Assert.Equal(splines[c.Guid], c.Spline!.Id);
                Assert.InRange(MathF.Abs(c.X - positions[c.Guid]), 0.001f, c.WalkSpeed * .06f);
                Assert.Equal(0u, Assert.IsType<WaypointMovementGenerator>(c.Motion.Default).LastReachedPoint);
            });
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveObjectsAndOptionOffKeepAdvancingSplines(bool activeObject)
    {
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(
            Content([Template()], [Spawn(1, WolfEntry, 150, 150)]));
        using (world)
        {
            world.Options.Maps.UpdateActiveCellsOnly = activeObject;
            Player player = TestWorld.CreatePlayer(1, 150, 150, new FakeSession());
            world.AddPlayer(player);
            Creature creature = Assert.Single(system.Creatures);
            if (activeObject) map.SetActive(creature, true);
            system.MoveTo(creature, 230, 150, creature.Z, false, null);
            player.Relocate(150, -150, player.Z, 0, 0);
            world.RunTick(1000);
            Assert.InRange(creature.X, 151, 155);
            Assert.True(creature.IsMoving);
            Assert.Equal(0, system.SkippedInactiveCreatures);
        }
    }

    [Fact]
    public void DormantSplineReadersAndStopUseTheFrozenPosition()
    {
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(
            Content([Template()], [Spawn(1, WolfEntry, 150, 150)]));
        using (world)
        {
            world.Options.Maps.GridUnload = false;
            Player player = TestWorld.CreatePlayer(1, 150, 150, new FakeSession());
            world.AddPlayer(player);
            Creature creature = Assert.Single(system.Creatures);
            system.MovePath(creature, [new(180, 150, creature.Z), new(230, 150, creature.Z)], false, SplineFacing.None);
            world.RunTick(50);
            float x = creature.X;
            CreatureSpline spline = creature.Spline!;
            uint elapsed = spline.ElapsedMs(system.ClockMs);
            player.Relocate(150, -150, player.Z, 0, 0);
            for (int i = 0; i < 10; i++) world.RunTick(1000);
            Assert.Equal(elapsed, spline.ElapsedMs(system.ClockMs));
            Assert.False(spline.IsFinished(system.ClockMs));
            Assert.Equal(x, spline.PositionAt(system.ClockMs).X);
            Assert.Equal(2, spline.RemainingPoints(system.ClockMs).Count);
            system.StopMoving(creature);
            Assert.Equal(x, creature.X);
            Assert.Null(creature.Spline);
        }
    }

    [Fact]
    public void ExpiredRespawnIsNotDelayedByFrozenMovementTime()
    {
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(
            Content([Template()], [Spawn(1, WolfEntry, 150, 150, respawnSeconds: 10)]));
        using (world)
        {
            world.Options.Maps.GridUnload = false;
            Player player = TestWorld.CreatePlayer(1, 150, 150, new FakeSession());
            world.AddPlayer(player);
            Creature creature = Assert.Single(system.Creatures);
            system.KillCreature(creature);
            world.RunTick(50);
            player.Relocate(150, -150, player.Z, 0, 0);
            world.RunTick(600_000);
            Assert.Equal(CreatureDeathState.Corpse, creature.DeathState);
            player.Relocate(150, 150, player.Z, 0, 0);
            world.RunTick(50);
            Assert.Equal(CreatureDeathState.Dead, creature.DeathState);
            world.RunTick(50);
            Assert.Equal(CreatureDeathState.Alive, creature.DeathState);
        }
    }

    private sealed class CountingPaths(IPathfinder backend) : IPathfinder
    {
        public bool Enabled => true;
        public int Calls { get; set; }
        public long Micros { get; set; }
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            long begin = Stopwatch.GetTimestamp();
            PathResult result = backend.FindPath(mapId, start, end, options);
            Micros += (long)(Stopwatch.GetElapsedTime(begin).TotalMilliseconds * 1000);
            return result;
        }
    }
}
