using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// The per-update creature path-search budget (<see cref="CreaturePathBudget"/>): a flood of chase requests in one tick runs at most
/// the budgeted searches, the rest are deferred to later updates (chase keeps what it was doing), deterministically and in full.
/// </summary>
public sealed class CreaturePathBudgetTests
{
    private const int Wolves = 40;

    /// <summary>A pathfinder that counts calls and reports <paramref name="nodesPerSearch"/> A* nodes to the search counters per call.</summary>
    private sealed class CountingPathfinder(int nodesPerSearch) : IPathfinder
    {
        public int Calls { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            PathSearchCounters.RecordSearch(nodesPerSearch, nodesPerSearch, capped: false);
            return new PathResult(PathType.Normal, [start, end]);
        }
    }

    private sealed record Crowd(WorldRuntime World, CreatureMapSystem System, Player Player, CountingPathfinder Paths, IReadOnlyList<Creature> Wolves)
        : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Crowd Start(CreaturePathBudget budget, int nodesPerSearch = 0)
    {
        var spawns = new List<CreatureSpawn>();
        for (uint i = 0; i < Wolves; i++)
        {
            spawns.Add(Spawn(i + 1, WolfEntry, 15f, ((int)i - (Wolves / 2)) * 0.5f));
        }

        var content = new CreatureContent([Template(configure: t => t.AIName = RecorderName)], spawns, [], [], [], CreatureAiContent.Empty);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile(), Factory = RecorderFactory() });
        system.PathBudget = budget;
        var paths = new CountingPathfinder(nodesPerSearch);
        WorldCollision.Of(runtime).Install(pathfinder: paths);
        (Player player, _) = AddPlayer(runtime, 1, 0, 0);
        return new Crowd(runtime, system, player, paths, system.Creatures.ToList());
    }

    /// <summary>All wolves are pulled in the same update (no tick between), the flood.</summary>
    private static void PullAll(Crowd c)
    {
        foreach (Creature wolf in c.Wolves)
        {
            c.World.GetMap(0, 0).Combat.DealDamage(c.Player, wolf, 1, direct: false);
        }
    }

    [Fact]
    public void AFloodOfChasePulls_RunsOnlyTheBudgetedSearches_InOneTick()
    {
        using Crowd c = Start(new CreaturePathBudget(MaxSearches: 8, MaxNodes: long.MaxValue));

        PullAll(c);

        Assert.Equal(8, c.Paths.Calls);
        Assert.Equal(8, c.System.PathSearchesThisTick);
        Assert.Equal(Wolves - 8, c.System.DeferredPathSearches);
        Assert.Equal(8, c.Wolves.Count(w => w.IsMoving));
        Assert.All(c.Wolves, w => Assert.Equal(MovementGeneratorType.Chase, w.Motion.CurrentType)); // deferred wolves still chase
    }

    [Fact]
    public void EveryLaterUpdate_StaysWithinTheBudget_AndTheDeferredWolvesAllGetTheirPath()
    {
        using Crowd c = Start(new CreaturePathBudget(MaxSearches: 8, MaxNodes: long.MaxValue));
        PullAll(c);

        int ticks = 0;
        while (c.Wolves.Any(w => !w.IsMoving) && ticks < 20)
        {
            int before = c.Paths.Calls;
            c.World.RunTick(50);
            ticks++;
            Assert.True(c.Paths.Calls - before <= 8, $"tick {ticks} ran {c.Paths.Calls - before} searches");
            Assert.True(c.System.PathSearchesThisTick <= 8);
        }

        Assert.All(c.Wolves, w => Assert.True(w.IsMoving));
        Assert.Equal((Wolves - 8) / 8, ticks); // 40 wolves, 8 per update, the first 8 already served: deterministic drain
    }

    [Fact]
    public void WithAMovingTarget_EveryChaserStillGetsAPath_WithinABoundedNumberOfUpdates()
    {
        // The target moves 1 yd (beyond TargetMoveTolerance) every update, so served chasers ask again at every recheck and keep
        // competing for the budget; a fixed creature order would serve the same early 16 for good and starve the rest.
        using Crowd c = Start(new CreaturePathBudget(MaxSearches: 8, MaxNodes: long.MaxValue));
        PullAll(c);
        var everMoved = new HashSet<Creature>(c.Wolves.Where(w => w.IsMoving));

        int ticks = 0;
        while (everMoved.Count < Wolves && ticks < 30)
        {
            c.Player.Relocate(0, ticks + 1, c.Player.Z, 0, 0);
            int before = c.Paths.Calls;
            c.World.RunTick(50);
            ticks++;
            Assert.True(c.Paths.Calls - before <= 8, $"tick {ticks} ran {c.Paths.Calls - before} searches");
            everMoved.UnionWith(c.Wolves.Where(w => w.IsMoving));
        }

        Assert.Equal(Wolves, everMoved.Count);
        Assert.True(ticks <= 3 * (Wolves / 8), $"took {ticks} updates"); // 5 rounds of 8; three updates per round is generous
    }

    [Fact]
    public void ADeferredFollower_DoesNotInformItsAi_OnEveryDeferredUpdate()
    {
        using Crowd c = Start(new CreaturePathBudget(MaxSearches: 0, MaxNodes: long.MaxValue));
        Creature wolf = c.Wolves[0];
        var ai = Assert.IsType<RecorderAI>(wolf.AI);
        wolf.Motion.MoveFollow(c.Player, 3f, 0f);

        c.World.RunTick(50);
        c.World.RunTick(50);

        Assert.DoesNotContain(ai.Calls, call => call.StartsWith("inform:Follow", StringComparison.Ordinal));
        Assert.True(c.System.DeferredPathSearches > 0);
    }

    [Fact]
    public void WithoutABudget_TheSameFloodRunsOneSearchPerChaser()
    {
        using Crowd c = Start(CreaturePathBudget.Unlimited);

        PullAll(c);

        Assert.Equal(Wolves, c.Paths.Calls);
        Assert.Equal(0, c.System.DeferredPathSearches);
    }

    [Fact]
    public void TheNodeBudget_StopsNewSearches_OnceCrossed()
    {
        // 1000 nodes per search, budget 2500: searches start at 0, 1000 and 2000 nodes spent, the fourth sees 3000 and waits.
        using Crowd c = Start(new CreaturePathBudget(MaxSearches: int.MaxValue, MaxNodes: 2500), nodesPerSearch: 1000);

        PullAll(c);

        Assert.Equal(3, c.Paths.Calls);
        Assert.Equal(3000, c.System.PathNodesThisTick);
        Assert.Equal(Wolves - 3, c.System.DeferredPathSearches);
    }

    [Fact]
    public void ANonDeferrableRequest_OverBudget_IsDeferred_NeverTheStraightLine()
    {
        using Crowd c = Start(new CreaturePathBudget(MaxSearches: 1, MaxNodes: long.MaxValue));
        Creature wolf = c.Wolves[0];

        IReadOnlyList<Vector3> first = c.System.FindPath(wolf, new Vector3(1, 2, 3));
        IReadOnlyList<Vector3> second = c.System.FindPath(wolf, new Vector3(4, 5, 6));

        Assert.Equal(1, c.Paths.Calls);
        Assert.Empty(second); // an empty path: the caller asks again on a later update
        Assert.Equal(0, c.System.DirectFallbackPaths);
        Assert.Equal(1, c.System.DeferredPathSearches);
        Assert.NotEmpty(first);

        c.World.RunTick(50); // a new update restores the budget
        c.System.FindPath(wolf, new Vector3(7, 8, 9));
        Assert.Equal(2, c.Paths.Calls);
    }

    private sealed class CountingSamplerPathfinder : IPathfinder, IMeshPointQuery
    {
        public int PathCalls { get; private set; }
        public int SampleCalls { get; private set; }
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            PathCalls++;
            PathSearchCounters.RecordSearch(0, 0, capped: false);
            return new PathResult(PathType.Normal, [start, end]);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            SampleCalls++;
            point = center + new Vector3(3f, 0f, 0f);
            return true;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(true, false, 1f, end);
    }

    [Fact]
    public void ARandomWanderer_DoesNotSampleMeshPoint_WhenPathBudgetIsExhausted_AndBacksOffBeforeRetrying()
    {
        var content = new CreatureContent(
            [Template()],
            [Spawn(1, WolfEntry, 10f, 10f, movementType: 1, wander: 5f)],
            [], [], [], CreatureAiContent.Empty);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (runtime)
        {
            system.PathBudget = new CreaturePathBudget(MaxSearches: 0, MaxNodes: long.MaxValue);
            var nav = new CountingSamplerPathfinder();
            WorldCollision.Of(runtime).Install(pathfinder: nav);
            AddPlayer(runtime, 1, 0, 0);

            // Advance until the initial random wander timer expires and tries to move:
            int ticks = 0;
            while (system.DeferredPathSearches == 0 && ticks < 200)
            {
                runtime.RunTick(100);
                ticks++;
            }

            Assert.Equal(1, system.DeferredPathSearches);
            Assert.Equal(0, nav.SampleCalls);
            Assert.Equal(0, nav.PathCalls);

            // During the next 900 ms (9 x 100 ms ticks), the 1000-1500 ms backoff prevents re-hammering every tick:
            for (int i = 0; i < 9; i++)
            {
                runtime.RunTick(100);
            }

            Assert.Equal(1, system.DeferredPathSearches);
            Assert.Equal(0, nav.SampleCalls);
        }
    }

    [Fact]
    public void CombatAndHomeMovement_ClaimPathBudgetAheadOfRandomWander_InTheSameTick()
    {
        var spawns = new List<CreatureSpawn>();
        // Spawn 10 random wanderers first in creature iteration order:
        for (uint i = 1; i <= 10; i++)
        {
            spawns.Add(Spawn(i, WolfEntry, 50f + i * 5f, 50f, movementType: 1, wander: 6f));
        }
        // Spawn 2 combat/home wolves after the wanderers:
        spawns.Add(Spawn(11, WolfEntry, 15f, 0f, movementType: 0));
        spawns.Add(Spawn(12, WolfEntry, 15f, 2f, movementType: 0));

        var content = new CreatureContent([Template(configure: t => t.AIName = RecorderName)], spawns, [], [], [], CreatureAiContent.Empty);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(
            content,
            new CreatureAiServices { Factory = RecorderFactory() });
        using (runtime)
        {
            var nav = new CountingSamplerPathfinder();
            WorldCollision.Of(runtime).Install(pathfinder: nav);
            (Player player, _) = AddPlayer(runtime, 1, 0, 0);

            List<Creature> all = system.Creatures.ToList();
            Creature chaser = all.Single(c => c.Spawn?.Guid == 11);
            Creature homer = all.Single(c => c.Spawn?.Guid == 12);

            // Put chaser into Chase and homer into Home while budget is 0 so both need a path on the next tick,
            // and let the 10 wanderers' initial timers and backoff expire so all 10 want to wander on the next tick too:
            system.PathBudget = new CreaturePathBudget(MaxSearches: 0, MaxNodes: long.MaxValue);
            for (int i = 0; i < 140; i++)
            {
                runtime.RunTick(100);
            }

            runtime.GetMap(0, 0).Combat.DealDamage(player, chaser, 1, direct: false);
            homer.Motion.MoveTargetedHome(new CreatureHome(30f, 2f, 0f, 0f));
            Assert.False(chaser.IsMoving);
            Assert.False(homer.IsMoving);

            // Now grant a budget of exactly 2 searches for the next 100 ms tick:
            system.PathBudget = new CreaturePathBudget(MaxSearches: 2, MaxNodes: long.MaxValue);
            int pathCallsBefore = nav.PathCalls;
            runtime.RunTick(100);

            // Pass 1 serves Chase and Home first; Pass 2 defers the Random wanderers without sampling the navmesh:
            Assert.Equal(2, nav.PathCalls - pathCallsBefore);
            Assert.True(chaser.IsMoving, "Chase movement should claim budget in pass 1 ahead of Random wander");
            Assert.True(homer.IsMoving, "Home movement should claim budget in pass 1 ahead of Random wander");
            Assert.Equal(0, nav.SampleCalls);
        }
    }

    [Fact]
    public void CrowdedRandomWanderPass_CapsRandomSearchesAtEight_AndBacksOffFourToTenSeconds()
    {
        var spawns = new List<CreatureSpawn>(300);
        for (uint i = 1; i <= 300; i++)
        {
            spawns.Add(Spawn(i, WolfEntry, 10f + (i % 20), 10f + (i / 20), movementType: 1, wander: 5f));
        }

        var content = new CreatureContent([Template()], spawns, [], [], [], CreatureAiContent.Empty);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (runtime)
        {
            system.PathBudget = new CreaturePathBudget(MaxSearches: 64, MaxNodes: long.MaxValue);
            var nav = new CountingSamplerPathfinder();
            WorldCollision.Of(runtime).Install(pathfinder: nav);
            AddPlayer(runtime, 1, 15f, 15f);

            // Advance 1000 ms in one tick so all 300 random wanderers' initial 1 s timer expires together:
            runtime.RunTick(1000);

            Assert.Equal(8, nav.PathCalls);
            Assert.Equal(8, nav.SampleCalls);
            Assert.Equal(300 - 8, system.DeferredPathSearches);

            // Over the next 3.5 seconds (35 x 100 ms), none of the 292 deferred wanderers re-hammer because crowded backoff is 4-10 s:
            long deferredAfterBurst = system.DeferredPathSearches;
            for (int i = 0; i < 35; i++)
            {
                runtime.RunTick(100);
            }

            Assert.Equal(deferredAfterBurst, system.DeferredPathSearches);
        }
    }
}

