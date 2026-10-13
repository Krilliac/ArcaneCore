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
}
