using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

public sealed class CreatureWalkOverheadTests
{
    private sealed class CountingPathfinder : IPathfinder
    {
        public int Calls { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            PathSearchCounters.RecordSearch(1, 1, capped: false);
            return new PathResult(PathType.Normal, [start, end]);
        }
    }

    [Fact]
    public void SplinePass_SkipsInactiveCreaturesInPass2_AndPreservesDormantSplinePauseAndWalkCounts()
    {
        var spawns = new List<CreatureSpawn>
        {
            Spawn(1, WolfEntry, 150f, 150f),
            Spawn(2, WolfEntry, 155f, 150f),
        };
        for (uint i = 3; i <= 12; i++)
        {
            spawns.Add(Spawn(i, WolfEntry, -1500f + (i * 2f), -1500f));
        }

        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(Content([Template()], spawns));
        using (world)
        {
            world.Options.Maps.GridUnload = false;
            world.Options.Parallel.CreatureSplinePass = true;

            Player activePlayer = TestWorld.CreatePlayer(1, 150f, 150f, new FakeSession(1));
            Player remoteLoader = TestWorld.CreatePlayer(2, -1500f, -1500f, new FakeSession(2));
            world.AddPlayer(activePlayer);
            world.AddPlayer(remoteLoader);

            Assert.Equal(12, system.Creatures.Count);
            Creature dormantMover = system.Creatures.Single(c => c.Spawn?.Guid == 3);
            system.MovePath(dormantMover, [new Vector3(-1450f, -1500f, dormantMover.Z), new Vector3(-1400f, -1500f, dormantMover.Z)], false, SplineFacing.None);
            world.RunTick(50);

            float frozenX = dormantMover.X;
            CreatureSpline spline = Assert.IsType<CreatureSpline>(dormantMover.Spline);
            uint frozenElapsed = spline.ElapsedMs(system.ClockMs);

            // Move remoteLoader back to the active hub so the 10 remote creatures are in inactive cells:
            remoteLoader.Relocate(150f, 150f, remoteLoader.Z, 0f, 0);
            long skippedBefore = system.SkippedInactiveCreatures;

            for (int tick = 0; tick < 5; tick++)
            {
                world.RunTick(1000);
            }

            Assert.Equal(skippedBefore + (10 * 5), system.SkippedInactiveCreatures);
            Assert.Equal(frozenElapsed, spline.ElapsedMs(system.ClockMs));
            Assert.False(spline.IsFinished(system.ClockMs));
            Assert.Equal(frozenX, spline.PositionAt(system.ClockMs).X);
            Assert.True(dormantMover.IsFrozenOutsideActiveCells);

            foreach (Creature active in system.Creatures.Where(c => c.Spawn?.Guid is 1 or 2))
            {
                Assert.False(active.IsFrozenOutsideActiveCells);
            }

            string breakdown = ((IMapUpdateBreakdown)system).DescribeLastUpdate();
            Assert.Contains("over 2/12 active", breakdown);
            Assert.Contains(", spline pass ", breakdown);
        }
    }

    [Fact]
    public void FairDeferralStartIndex_UsesCachedScratchIndex_AndHandlesDespawnedDeferredCreature()
    {
        var spawns = new List<CreatureSpawn>();
        for (uint i = 1; i <= 6; i++)
        {
            spawns.Add(Spawn(i, WolfEntry, 15f, i * 1.5f));
        }

        var content = new CreatureContent([Template(configure: t => t.AIName = RecorderName)], spawns, [], [], [], CreatureAiContent.Empty);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(
            content,
            new CreatureAiServices { Hostility = new AlwaysHostile(), Factory = RecorderFactory() });
        using (world)
        {
            var paths = new CountingPathfinder();
            WorldCollision.Of(world).Install(pathfinder: paths);
            (Player player, _) = AddPlayer(world, 1, 0f, 0f);

            // Initial tick populates _scratchCreatures and stamps ScratchIndex 0..5:
            world.RunTick(50);
            List<Creature> wolves = system.Creatures.OrderBy(c => c.ScratchIndex).ToList();
            Assert.Equal(6, wolves.Count);
            for (int i = 0; i < wolves.Count; i++)
            {
                Assert.Equal(i, wolves[i].ScratchIndex);
            }

            // Budget of 2 searches per tick; pull all 6 wolves so wolves[0..1] move and wolves[2] is _pathFirstDeferred:
            system.PathBudget = new CreaturePathBudget(MaxSearches: 2, MaxNodes: long.MaxValue);
            foreach (Creature wolf in wolves)
            {
                map.Combat.DealDamage(player, wolf, 1, direct: false);
            }

            Assert.True(wolves[0].IsMoving);
            Assert.True(wolves[1].IsMoving);
            Assert.False(wolves[2].IsMoving);

            // Next tick starts at wolves[2] via O(1) ScratchIndex lookup, serving wolves[2] and wolves[3]; wolves[4] becomes _pathFirstDeferred:
            world.RunTick(50);
            Assert.True(wolves[2].IsMoving);
            Assert.True(wolves[3].IsMoving);
            Assert.False(wolves[4].IsMoving);
            Assert.False(wolves[5].IsMoving);

            // Despawn the deferred creature (wolves[4]) before the next tick:
            system.Despawn(wolves[4]);
            Assert.Equal(-1, wolves[4].ScratchIndex);

            // Next tick rebuilds _scratchCreatures, resets start safely to 0 without throwing, and serves the remaining wolf (wolves[5]):
            world.RunTick(50);
            Assert.True(wolves[5].IsMoving);
            List<Creature> remaining = system.Creatures.OrderBy(c => c.ScratchIndex).ToList();
            Assert.Equal(5, remaining.Count);
            for (int i = 0; i < remaining.Count; i++)
            {
                Assert.Equal(i, remaining[i].ScratchIndex);
            }
        }
    }

    [Fact]
    public void GridCellId_And_IsGridActiveObject_StayInSyncAcrossAddRelocateSetActiveAndRemove()
    {
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(Content([Template()], []));
        using (world)
        {
            world.Options.Maps.GridUnload = false;
            Player player = TestWorld.CreatePlayer(1, 0f, 0f, new FakeSession(1));
            world.AddPlayer(player);

            var creature = new Creature(900, Template(), spawn: null, new CreatureContent([Template()], [], [], [], []), new Random(1));
            creature.SetPosition(100f, 200f, 0f, 0f);
            Assert.Equal(-1, creature.GridCellId);
            Assert.False(creature.IsGridActiveObject);
            Assert.False(map.Grids.IsActive(creature));

            map.AddObject(creature);
            CellCoord initialCell = GridDefines.ComputeCellCoord(100f, 200f);
            int expectedInitialId = (initialCell.Y * GridDefines.TotalNumberOfCellsPerMap) + initialCell.X;
            Assert.Equal(expectedInitialId, creature.GridCellId);
            Assert.False(creature.IsGridActiveObject);
            Assert.False(map.Grids.IsActive(creature));

            // Relocate to another cell far from the player:
            creature.SetPosition(1200f, -800f, 0f, 0f);
            CellCoord movedCell = GridDefines.ComputeCellCoord(1200f, -800f);
            int expectedMovedId = (movedCell.Y * GridDefines.TotalNumberOfCellsPerMap) + movedCell.X;
            Assert.NotEqual(expectedInitialId, expectedMovedId);
            Assert.Equal(expectedMovedId, creature.GridCellId);

            map.Grids.MarkActiveCells();
            Assert.False(map.Grids.IsInActiveCell(creature));

            // SetActive(true) marks IsGridActiveObject and activates cells around the creature:
            map.SetActive(creature, true);
            Assert.True(creature.IsGridActiveObject);
            Assert.True(map.Grids.IsActive(creature));
            map.Grids.MarkActiveCells();
            Assert.True(map.Grids.IsInActiveCell(creature));

            // SetActive(false) clears IsGridActiveObject:
            map.SetActive(creature, false);
            Assert.False(creature.IsGridActiveObject);
            Assert.False(map.Grids.IsActive(creature));

            // Re-activate and then remove from map: both GridCellId and IsGridActiveObject reset, and IsInActiveCell falls back to coordinates:
            map.SetActive(creature, true);
            Assert.True(creature.IsGridActiveObject);
            map.RemoveObject(creature);
            Assert.Equal(-1, creature.GridCellId);
            Assert.False(creature.IsGridActiveObject);
            Assert.False(map.Grids.IsActive(creature));

            // Move the unindexed object near the player at (0, 0) and verify the id < 0 coordinate fallback in IsInActiveCell:
            creature.SetPosition(5f, 5f, 0f, 0f);
            map.Grids.MarkActiveCells();
            Assert.True(map.Grids.IsInActiveCell(creature));
            Assert.NotNull(system);
        }
    }

    [Fact]
    public void DiagnosticsOff_SkipsPerCreatureTimestamps_WhileDiagnosticsOn_ReportsSlowestCreature()
    {
        var content = new CreatureContent([Template()], [Spawn(1, WolfEntry, 15f, 0f)], [], [], [], CreatureAiContent.Empty);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0f, 0f);

            map.Update(100, diagnostics: null);
            Assert.False(map.IsCollectingDiagnostics);
            string withoutDiag = ((IMapUpdateBreakdown)system).DescribeLastUpdate();
            Assert.Contains("creatures ", withoutDiag);
            Assert.Contains("over 1/1 active", withoutDiag);
            Assert.DoesNotContain("slowest creature entry", withoutDiag);

            MapUpdateDiagnostics diagnostics = map.ReusableDiagnostics();
            map.Update(100, diagnostics);
            Assert.False(map.IsCollectingDiagnostics);
            string withDiag = ((IMapUpdateBreakdown)system).DescribeLastUpdate();
            Assert.Contains($"slowest creature entry {WolfEntry} ", withDiag);
        }
    }
}
