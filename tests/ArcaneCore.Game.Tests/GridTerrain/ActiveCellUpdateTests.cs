using System.Diagnostics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureTestKit;

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
    public void ACorpseOutsideTheActiveCellsWaitsAndItsDecayCatchesUpWhenAPlayerReturns()
    {
        // Decay 2 s, respawn 10 min: only the caught-up decay (update_diff), not a due respawn, can remove the corpse at once.
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 2 };
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0, respawnSeconds: 600)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content, options);
        using (world)
        {
            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            world.AddPlayer(player);
            world.RunTick(50);
            Creature wolf = Assert.Single(system.Creatures);
            system.KillCreature(wolf);
            world.RunTick(100);
            player.Relocate(-240, 0, 83.5f, 0, 0); // same grid, 260 yards from the corpse
            for (int i = 0; i < 100; i++) world.RunTick(100); // 10 s
            Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState); // not updated: still lying there

            player.Relocate(0, 0, 83.5f, 0, 0);
            world.RunTick(100); // WorldObject::UpdateHelper::UpdateRealTime: the ~10 s since its last update decay the corpse at once
            Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);
            Assert.False(wolf.IsAlive); // and it waits for its 10-minute respawn
        }
    }

    [Fact]
    public void ACorpsesSkinningTimerCatchesUpWhenAPlayerReturns()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 60 };
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0, respawnSeconds: 600)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content, options);
        using (world)
        {
            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            world.AddPlayer(player);
            world.RunTick(50);
            Creature wolf = Assert.Single(system.Creatures);
            system.KillCreature(wolf);
            world.RunTick(100);
            wolf.SkinningForOthersMs = 5_000; // the looter's skinning window (Creature.cpp:911-914)
            player.Relocate(-240, 0, 83.5f, 0, 0);
            for (int i = 0; i < 100; i++) world.RunTick(100); // 10 s away
            Assert.Equal(5_000u, wolf.SkinningForOthersMs);

            player.Relocate(0, 0, 83.5f, 0, 0);
            world.RunTick(100);
            Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState);
            Assert.Equal(0u, wolf.SkinningForOthersMs); // the 10 s away count, not the one 100 ms tick
        }
    }

    /// <summary>
    /// vmangos Creature.cpp:683-699 (Visibility.ForceActiveObjects, default true): a LARGE_AOI (static flag 0x80000000), GIGANTIC_AOI
    /// (vmangos extra 0x100) or INFINITE_AOI (0x200) creature is an active object; mangos-classic Creature.cpp:205 does the same for
    /// CREATURE_EXTRA_FLAG_ACTIVE (0x1000, classic-db's dialect). It is updated far from players and activates the cells around it.
    /// </summary>
    [Theory]
    [InlineData(0x80000000u, 0u, ArcaneCore.Kernel.WorldData.Creatures.CreatureExtraFlagsDialect.CMangos)]
    [InlineData(0u, 0x100u, ArcaneCore.Kernel.WorldData.Creatures.CreatureExtraFlagsDialect.VMangos)]
    [InlineData(0u, 0x200u, ArcaneCore.Kernel.WorldData.Creatures.CreatureExtraFlagsDialect.VMangos)]
    [InlineData(0u, 0x1000u, ArcaneCore.Kernel.WorldData.Creatures.CreatureExtraFlagsDialect.CMangos)]
    public void AnAoiOrActiveFlaggedCreatureIsAnActiveObject_AndItsNeighbourIsUpdatedToo(uint staticFlags, uint extraFlags,
        ArcaneCore.Kernel.WorldData.Creatures.CreatureExtraFlagsDialect dialect)
    {
        const uint Big = 300;
        CreatureContent content = Content(
            [Template(), Template(Big, b => { b.StaticFlags1 = staticFlags; b.ExtraFlags = extraFlags; b.ExtraFlagsDialect = dialect; })],
            [Spawn(1, Big, 250, 0, movementType: 1, wander: 10), Spawn(2, WolfEntry, 255, 5, movementType: 1, wander: 10)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            Creature big = Assert.Single(system.Creatures, c => c.Entry == Big);
            Creature neighbour = Assert.Single(system.Creatures, c => c.Entry == WolfEntry);
            world.RunTick(50);
            Assert.True(map.Grids.IsActive(big));
            bool bigMoved = false, neighbourMoved = false;
            for (int i = 0; i < 600 && !(bigMoved && neighbourMoved); i++)
            {
                world.RunTick(100);
                bigMoved |= big.X != 250 || big.Y != 0;
                neighbourMoved |= neighbour.X != 255 || neighbour.Y != 5;
            }

            Assert.True(bigMoved, "the flagged creature was not updated");
            Assert.True(neighbourMoved, "the flagged creature did not activate the cells around it");
        }
    }

    [Fact]
    public void AnUnflaggedVmangosDialectGiganticBitInCmangosDialectIsNotActive()
    {
        // 0x100 is GIGANTIC_AOI only in vmangos' extra flags; classic-db (cmangos dialect) gives that bit another meaning.
        const uint Other = 301;
        CreatureContent content = Content(
            [Template(Other, b => { b.ExtraFlags = 0x100; b.ExtraFlagsDialect = ArcaneCore.Kernel.WorldData.Creatures.CreatureExtraFlagsDialect.CMangos; })],
            [Spawn(1, Other, 250, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            world.RunTick(50);
            Assert.False(map.Grids.IsActive(Assert.Single(system.Creatures)));
        }
    }

    /// <summary>
    /// vmangos Map::AddToActive (Maps/Map.cpp:1925-1950) pins only the active object's own grid, and its cell update visits with
    /// SetNoCreate (Map.cpp:736): an active creature 30 yards from a grid border does not load the grid across it.
    /// </summary>
    [Fact]
    public void AnActiveNonPlayerAtAGridEdgeDoesNotLoadTheNeighbouringGrid()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        // The first x past 0 where the grid changes: the border between grid g and grid h.
        ArcaneCore.Game.Maps.Grid.GridCoord g = ArcaneCore.Game.Maps.Grid.GridDefines.ComputeGridCoord(0, 0);
        float edge = 0;
        while (ArcaneCore.Game.Maps.Grid.GridDefines.ComputeGridCoord(edge, 0) == g) edge += 1;
        ArcaneCore.Game.Maps.Grid.GridCoord h = ArcaneCore.Game.Maps.Grid.GridDefines.ComputeGridCoord(edge, 0);
        world.AddPlayer(TestWorld.CreatePlayer(1, edge - 300, 0, new FakeSession())); // loads g only
        var unit = new TestUnit(1, edge - 30, 0);
        map.AddObject(unit);
        Assert.False(map.Grids.IsGridLoaded(h));

        map.SetActive(unit, true);
        world.RunTick(50);
        Assert.True(map.Grids.IsActive(unit));
        Assert.False(map.Grids.IsGridLoaded(h)); // a player there would load it (LoadMapCellsAround)

        unit.SetPosition(edge - 5, 0, 0, 0); // still in g, 5 yards from h
        world.RunTick(50);
        Assert.False(map.Grids.IsGridLoaded(h));
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
