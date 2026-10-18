using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Creature searches on the path-job pool (<c>Creatures:Movement:PathJobs</c>, multithreading plan W5): eligible moves search on a
/// worker and start on a later update with the worker's path; a request that changed meanwhile drops the old answer; chase/follow and
/// every move with the setting off stay on the map's thread.
/// </summary>
public sealed class CreaturePathJobTests
{
    /// <summary>A thread-safe pathfinder that answers a three-point path and records which thread searched.</summary>
    private sealed class RecordingPathfinder : IPathfinder, ISharedPathSearch
    {
        private int _syncCalls;

        public ConcurrentBag<int> SharedThreads { get; } = [];

        public ManualResetEventSlim Gate { get; } = new(true);

        public int SyncCalls => Volatile.Read(ref _syncCalls);

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Interlocked.Increment(ref _syncCalls);
            PathSearchCounters.RecordSearch(1, 1, capped: false);
            return Answer(start, end);
        }

        public bool CanSearchShared(uint mapId) => true;

        public PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options)
        {
            Gate.Wait(TimeSpan.FromSeconds(10));
            SharedThreads.Add(Environment.CurrentManagedThreadId);
            PathSearchCounters.RecordSearch(1, 1, capped: false);
            return Answer(start, end);
        }

        private static PathResult Answer(Vector3 start, Vector3 end) => new(PathType.Normal, [start, (start + end) * 0.5f, end]);
    }

    private sealed record Scene(WorldRuntime World, CreatureMapSystem System, Player Player, Creature Wolf, RecordingPathfinder Paths, NavJobSystem? Jobs)
        : IDisposable
    {
        public void Dispose()
        {
            Jobs?.Dispose();
            World.Dispose();
        }
    }

    private static Scene Start(byte movementType, bool jobs = true)
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0, movementType: movementType, wander: 5)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        var paths = new RecordingPathfinder();
        WorldCollision.Of(world).Install(pathfinder: paths);
        NavJobSystem? pool = jobs ? new NavJobSystem(threads: 2) : null;
        if (pool is not null)
        {
            system.PathJobs = pool;
        }

        (Player player, _) = AddPlayer(world, 1, 0, 0);
        return new Scene(world, system, player, Assert.Single(system.Creatures), paths, pool);
    }

    private static void WaitForPool(NavJobSystem jobs, long completed)
        => Assert.True(SpinWait.SpinUntil(() => jobs.Totals.Completed + jobs.Totals.Skipped >= completed, TimeSpan.FromSeconds(10)), "path job did not finish");

    [Fact]
    public void AWanderingCreature_SearchesOnAWorker_AndWalksTheWorkersPath()
    {
        using Scene s = Start(movementType: 1);
        int worldThread = Environment.CurrentManagedThreadId;

        for (int i = 0; i < 300 && !s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(100);
            Thread.Sleep(1);
        }

        Assert.True(s.Wolf.IsMoving, "the wolf never started a wander leg");
        Assert.NotEmpty(s.Paths.SharedThreads);
        Assert.DoesNotContain(worldThread, s.Paths.SharedThreads);
        Assert.Equal(0, s.Paths.SyncCalls);
        Assert.True(s.System.PathJobsApplied >= 1);
    }

    [Fact]
    public void AWanderLegOnAWorker_IsClaimedOnTheNextFewUpdates_NotAfterTheBudgetBackOff()
    {
        using Scene s = Start(movementType: 1);
        s.Paths.Gate.Reset();
        for (int i = 0; i < 300 && s.System.PathJobsSubmitted == 0; i++)
        {
            s.World.RunTick(100);
        }

        Assert.Equal(1, s.System.PathJobsSubmitted);
        Assert.Equal(1, s.System.PathJobsPending);
        s.Paths.Gate.Set();
        WaitForPool(s.Jobs!, 1);

        // RandomMovementGenerator.PathJobRetryMs (100 ms), not NoPathRetryMinMs (1 s): three 50 ms updates are enough.
        for (int i = 0; i < 3 && !s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(50);
        }

        Assert.True(s.Wolf.IsMoving, "the finished wander leg was not claimed within 150 ms");
        Assert.Equal(1, s.System.PathJobsApplied);
        Assert.Equal(0, s.System.PathJobsInFlight);
    }

    [Fact]
    public void AScriptedMove_WaitsForItsJob_ThenWalksToTheSameDestination()
    {
        using Scene s = Start(movementType: 0);
        s.Paths.Gate.Reset();

        s.System.MoveTo(s.Wolf, 15, 0, 83.5f, run: false, finalOrientation: null);
        Assert.False(s.Wolf.IsMoving);
        Assert.Equal(1, s.System.DeferredMoveCount);
        Assert.Equal(1, s.System.PathJobsInFlight);

        s.World.RunTick(50);
        Assert.False(s.Wolf.IsMoving);
        Assert.True(s.System.PathJobWaits >= 1);

        s.Paths.Gate.Set();
        WaitForPool(s.Jobs!, 1);
        s.World.RunTick(50);

        Assert.True(s.Wolf.IsMoving);
        Assert.Equal(1, s.System.PathJobsApplied);
        Assert.Equal(0, s.System.DeferredMoveCount);
        Run(s.World, 8000);
        Assert.True(Distance2D(s.Wolf, 15, 0) < 1f, $"wolf at {s.Wolf.X:F1},{s.Wolf.Y:F1}");
        Assert.Equal(0, s.Paths.SyncCalls);
    }

    [Fact]
    public void ANewDestination_DropsTheOldJob_AndTheCreatureGoesToTheNewOne()
    {
        using Scene s = Start(movementType: 0);
        s.Paths.Gate.Reset();

        s.System.MoveTo(s.Wolf, 15, 0, 83.5f, run: false, finalOrientation: null);
        s.System.MoveTo(s.Wolf, 5, 12, 83.5f, run: false, finalOrientation: null);
        Assert.Equal(1, s.System.PathJobsStale);
        Assert.Equal(1, s.System.PathJobsInFlight);

        s.Paths.Gate.Set();
        WaitForPool(s.Jobs!, 2);
        s.World.RunTick(50);
        Assert.True(s.Wolf.IsMoving);
        Run(s.World, 8000);
        Assert.True(Distance2D(s.Wolf, 5, 12) < 1f, $"wolf at {s.Wolf.X:F1},{s.Wolf.Y:F1}");
    }

    [Fact]
    public void AFollowStaysOnTheMapThread()
    {
        using Scene s = Start(movementType: 0);
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        s.Player.Relocate(0, 20, s.Player.Z, 0, 0);

        Run(s.World, 2000);

        Assert.True(s.Paths.SyncCalls > 0);
        Assert.Empty(s.Paths.SharedThreads);
        Assert.Equal(0, s.System.PathJobsSubmitted);
    }

    [Fact]
    public void WithTheSettingOff_EverySearchStaysOnTheMapThread()
    {
        using Scene s = Start(movementType: 1, jobs: false);

        Run(s.World, 15000);

        Assert.Null(s.System.PathJobs);
        Assert.True(s.Paths.SyncCalls > 0);
        Assert.Empty(s.Paths.SharedThreads);
        Assert.Equal(0, s.System.PathJobsSubmitted);
    }

    [Fact]
    public void ADespawnedCreaturesJob_IsSweptAway()
    {
        using Scene s = Start(movementType: 0);
        s.Paths.Gate.Reset();
        s.System.MoveTo(s.Wolf, 15, 0, 83.5f, run: false, finalOrientation: null);
        Assert.Equal(1, s.System.PathJobsInFlight);

        s.Wolf.Health = 0; // dead: the sweep drops its job
        s.World.RunTick(1100);
        s.World.RunTick(100);
        s.Paths.Gate.Set();

        Assert.Equal(0, s.System.PathJobsInFlight);
    }
}
