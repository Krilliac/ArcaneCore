using System.Diagnostics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// vmangos Map::UpdateCells → UpdateActiveCellsSynch (Maps/Map.cpp:815-857): on a continent only the cells within the grid
/// activation distance of a player or active object (MarkCellsAroundObject, Cell::CalculateCellArea) are visited, so a creature
/// elsewhere in a loaded grid is not updated. The live 2026-10-11 map 1 spent 53 ms of simulation on 14 players ("moved 1173"):
/// every creature of every loaded grid ran its AI and movement each tick.
/// </summary>
public sealed class ActiveCellUpdateTests(ITestOutputHelper output)
{
    [Fact]
    public void AWandererFarFromEveryPlayerStandsStill_OneNearAPlayerWanders()
    {
        CreatureContent content = Content([Template()],
            [Spawn(1, WolfEntry, 20, 0, movementType: 1, wander: 10), Spawn(2, WolfEntry, 250, 0, movementType: 1, wander: 10)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession())); // loads the centre grid, which holds both
            world.RunTick(50);
            Creature near = Assert.Single(system.Creatures, c => c.Spawn!.Guid == 1);
            Creature far = Assert.Single(system.Creatures, c => c.Spawn!.Guid == 2);
            (float fx, float fy) = (far.X, far.Y);
            bool nearMoved = false;
            for (int i = 0; i < 600; i++)
            {
                world.RunTick(100);
                nearMoved |= near.X != 20 || near.Y != 0;
            }

            Assert.True(nearMoved, "the wanderer beside the player never moved");
            Assert.Equal((fx, fy), (far.X, far.Y));
            Assert.True(system.SkippedInactiveCreatures >= 600);
            Assert.True(map.UpdatesActiveCellsOnly);
        }
    }

    [Fact]
    public void ACorpseOutsideTheActiveCellsWaitsAndCatchesUpWhenAPlayerReturns()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 2 };
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0, respawnSeconds: 5)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content, options);
        using (world)
        {
            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            world.AddPlayer(player);
            world.RunTick(50);
            Creature wolf = Assert.Single(system.Creatures);
            system.KillCreature(wolf);
            player.Relocate(-240, 0, 83.5f, 0, 0); // same grid, 260 yards from the corpse
            for (int i = 0; i < 100; i++) world.RunTick(100); // 10 s: corpse decay (2 s) and respawn (5 s) both due
            Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState); // not updated: still lying there

            player.Relocate(0, 0, 83.5f, 0, 0);
            world.RunTick(100); // WorldObject::UpdateHelper::UpdateRealTime: the 10 s since the last update decay the corpse
            Assert.NotEqual(CreatureDeathState.Corpse, wolf.DeathState);
            for (int i = 0; i < 5 && !wolf.IsAlive; i++) world.RunTick(100); // the respawn time (wall clock) has long passed
            Assert.True(wolf.IsAlive);
        }
    }

    [Fact]
    public void AnActiveObjectIsUpdatedAnywhere_AndTheOptionOffUpdatesEveryCreature()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 250, 0, movementType: 1, wander: 10)]);
        foreach (bool optionOff in new[] { false, true })
        {
            (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);
            using (world)
            {
                world.Options.Maps.UpdateActiveCellsOnly = !optionOff;
                world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
                world.RunTick(50);
                Creature far = Assert.Single(system.Creatures);
                if (!optionOff) map.SetActive(far, true); // vmangos Map::AddToActive
                long skippedBefore = system.SkippedInactiveCreatures; // the first tick ran before it was made active
                bool moved = false;
                for (int i = 0; i < 600 && !moved; i++)
                {
                    world.RunTick(100);
                    moved = far.X != 250 || far.Y != 0;
                }

                Assert.True(moved, optionOff ? "with the option off every creature is updated" : "an active object is updated");
                Assert.Equal(skippedBefore, system.SkippedInactiveCreatures);
            }
        }
    }

    /// <summary>
    /// Reproducible before/after: 4 000 wandering creatures spread over the loaded centre grid, two players in one corner (the live
    /// map 1 shape: few players, many loaded grids). 200 ticks with the option off (every creature, before) and on (vmangos).
    /// </summary>
    [Fact]
    public void MeasureCreatureUpdatesWithFewPlayers()
    {
        double Run(bool activeCellsOnly, out long skipped, out int loaded)
        {
            var random = new Random(5875);
            var spawns = Enumerable.Range(1, 4000).Select(i => Spawn((uint)i, WolfEntry,
                -260 + (float)random.NextDouble() * 520, -260 + (float)random.NextDouble() * 520, movementType: 1, wander: 8)).ToArray();
            (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(Content([Template()], spawns));
            using (world)
            {
                world.Options.Maps.UpdateActiveCellsOnly = activeCellsOnly;
                world.AddPlayer(TestWorld.CreatePlayer(1, -240, -240, new FakeSession()));
                world.AddPlayer(TestWorld.CreatePlayer(2, -230, -240, new FakeSession()));
                for (int i = 0; i < 20; i++) world.RunTick(100); // warm up
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) world.RunTick(100);
                skipped = system.SkippedInactiveCreatures;
                loaded = system.Creatures.Count; // the spawns of the players' grid (the rest of the 4 000 lie in grids nobody loaded)
                return clock.Elapsed.TotalMilliseconds / 200;
            }
        }

        double before = Run(false, out _, out int loaded);
        double after = Run(true, out long skipped, out _);
        output.WriteLine($"{loaded} wandering creatures loaded around 2 players in a corner of their grid: {before:F2} ms/tick updating every "
            + $"creature, {after:F2} ms/tick updating the active cells ({skipped / 220.0:F0} creature updates skipped a tick)");
        // Two players in a corner activate a few dozen of the grid's 256 cells: most of the grid's creatures stand still.
        Assert.True(skipped > 220L * loaded / 2, $"only {skipped} of {220L * loaded} updates skipped");
    }
}
