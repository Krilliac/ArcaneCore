using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

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

    /// <summary>
    /// Waits for the pool to finish <paramref name="completed"/> jobs and for every job the map holds to have published its answer: the
    /// pool counts a job a moment before the job turns done, and a worker preempted in between left the next update finding it still
    /// running (CI run 39573816775: the wander leg re-queued and was not claimed within the test's three updates).
    /// </summary>
    private static void WaitForPool(Scene s, long completed)
    {
        Assert.True(SpinWait.SpinUntil(() => s.Jobs!.Totals.Completed + s.Jobs.Totals.Skipped >= completed, TimeSpan.FromSeconds(10)), "path job did not finish");
        Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
    }

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
        WaitForPool(s, 1);

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
        WaitForPool(s, 1);
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
        WaitForPool(s, 2);
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
    public void WithPathJobsFollow_AFollowSearchesOnAWorker_AndTheFollowerSetsOff()
    {
        using Scene s = Start(movementType: 0);
        s.System.Options.Movement.PathJobsFollow = true;
        int worldThread = Environment.CurrentManagedThreadId;
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        s.Player.Relocate(0, 20, s.Player.Z, 0, 0);

        for (int i = 0; i < 200 && s.System.PathJobsApplied == 0; i++)
        {
            s.World.RunTick(50);
            Thread.Sleep(1);
        }

        Assert.True(s.System.PathJobsApplied >= 1, "no follow answer from the pool was applied");
        Assert.NotEmpty(s.Paths.SharedThreads);
        Assert.DoesNotContain(worldThread, s.Paths.SharedThreads);
        Assert.True(s.Wolf.IsMoving, "the follower did not set off on the pool's path");
    }

    [Fact]
    public void WithPathJobsFollow_AFollowerWhoseAnswersLagTheRunningOwner_StillSetsOffAndCatchesUp()
    {
        // A loaded pool answers every follow search two updates late. The owner runs 0.7 yd per 100 ms update (7 yd/s), so each answer
        // is aimed 1.4 yd behind the owner's latest follow point, past PathJobFollowTolerance. Before the fix every such answer was
        // dropped as stale and asked again, so the follower stood still for as long as the owner kept running (the WSG ramp flake:
        // 57 of 58 follow jobs stale, the pet never moved).
        using Scene s = Start(movementType: 0);
        s.System.Options.Movement.PathJobsFollow = true;
        s.Paths.Gate.Reset();
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        float startX = s.Wolf.X;
        float startY = s.Wolf.Y;

        float y = 0f;
        for (int i = 0; i < 40; i++)
        {
            y += 0.7f;
            s.Player.Relocate(0, y, s.Player.Z, MathF.PI / 2f, 0);
            s.World.RunTick(100);
            if (i % 2 == 1)
            {
                // The answer lands between this update and the next one.
                s.Paths.Gate.Set();
                Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
                s.Paths.Gate.Reset();
            }
        }

        float covered = Distance2D(s.Wolf, startX, startY);
        Assert.True(s.System.PathJobsApplied >= 1, $"no lagging follow answer was applied (submitted {s.System.PathJobsSubmitted}, stale {s.System.PathJobsStale})");
        Assert.True(covered >= 10f, $"the follower covered only {covered:F1} yd while its owner ran {y:F1} yd");

        // The owner stops: the follower closes up on the follow point.
        s.Paths.Gate.Set();
        for (int i = 0; i < 60; i++)
        {
            s.World.RunTick(100);
            Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        }

        Assert.True(Distance2D(s.Wolf, s.Player) <= 4f, $"the follower ended {Distance2D(s.Wolf, s.Player):F1} yd from its owner");
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
    public void AChaseSearchesOnAWorker_AndTheChaserSetsOff()
    {
        using Scene s = Start(movementType: 0);
        int worldThread = Environment.CurrentManagedThreadId;
        s.Player.Relocate(25, 0, s.Player.Z, 0, 0);
        s.Wolf.Motion.MoveChase(s.Player);

        for (int i = 0; i < 200 && s.System.PathJobsApplied == 0; i++)
        {
            s.World.RunTick(50);
            Thread.Sleep(1);
        }

        Assert.True(s.System.PathJobsApplied >= 1, "no chase answer from the pool was applied");
        Assert.NotEmpty(s.Paths.SharedThreads);
        Assert.DoesNotContain(worldThread, s.Paths.SharedThreads);
        Assert.Equal(0, s.Paths.SyncCalls);
        Assert.True(s.Wolf.IsMoving, "the chaser did not set off on the pool's path");
    }

    [Fact]
    public void WithPathJobsFollow_AFormationFollowerSearchesOnAWorker_AndSetsOff()
    {
        var group = new ArcaneCore.Kernel.WorldData.SpawnGroups.SpawnGroupDefinition
        {
            Id = 1,
            Name = "TestFormation",
            Type = ArcaneCore.Kernel.WorldData.SpawnGroups.SpawnGroupType.Creature,
            Members = [new(1, 0, 0), new(2, 1, 0)],
            Formation = new ArcaneCore.Kernel.WorldData.SpawnGroups.SpawnGroupFormation(1, 4, 0, 0, 0, "TestFormation"),
        };
        var content = new CreatureContent(
            [Template()],
            [Spawn(1, WolfEntry, 0, 0) with { Orientation = 0f }, Spawn(2, WolfEntry, -4, 0) with { Orientation = 0f }],
            [], [], [])
        {
            SpawnGroups = new ArcaneCore.Kernel.WorldData.SpawnGroups.SpawnGroupCatalog([group]),
        };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        var paths = new RecordingPathfinder();
        WorldCollision.Of(world).Install(pathfinder: paths);
        using var pool = new NavJobSystem(threads: 2);
        system.PathJobs = pool;
        system.Options.Movement.PathJobsFollow = true;
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Creature leader = system.FindBySpawnGuid(1)!;
            Creature follower = system.FindBySpawnGuid(2)!;
            int worldThread = Environment.CurrentManagedThreadId;

            system.MoveTo(leader, 20, 0, leader.Z, run: false, finalOrientation: null);
            for (int i = 0; i < 200 && (!follower.IsMoving || system.PathJobsApplied < 2); i++)
            {
                world.RunTick(50);
                Thread.Sleep(1);
            }

            Assert.True(follower.IsMoving, "the formation follower did not set off on the pool's path");
            Assert.True(system.PathJobsApplied >= 2, $"expected both leader and follower jobs to apply, got {system.PathJobsApplied}");
            Assert.NotEmpty(paths.SharedThreads);
            Assert.DoesNotContain(worldThread, paths.SharedThreads);
            Assert.Equal(0, paths.SyncCalls);
        }
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

    [Fact]
    public void AFinishedJobResult_WaitsForANextUpdate_WhenThePathTimeIsSpent_AndIsNotLost()
    {
        using Scene s = Start(movementType: 1);
        s.Paths.Gate.Reset();
        for (int i = 0; i < 300 && s.System.PathJobsSubmitted == 0; i++)
        {
            s.World.RunTick(100);
        }

        Assert.Equal(1, s.System.PathJobsSubmitted);
        s.Paths.Gate.Set();
        WaitForPool(s, 1);

        // An update whose path time is already spent (the searches and drapes before it) applies no result: the job stays with its creature.
        s.System.PathBudget = new CreaturePathBudget(MaxSearches: 64, MaxNodes: 16384, MaxMilliseconds: 0);
        for (int i = 0; i < 6; i++)
        {
            s.World.RunTick(50);
        }

        Assert.False(s.Wolf.IsMoving, "the result was applied with no path time left");
        Assert.Equal(0, s.System.PathJobsApplied);
        Assert.Equal(1, s.System.PathJobsInFlight);
        Assert.True(s.System.DeferredPathSearches >= 1);

        s.System.PathBudget = CreaturePathBudget.Default;
        for (int i = 0; i < 6 && !s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(50);
        }

        Assert.True(s.Wolf.IsMoving, "the deferred result was lost");
        Assert.Equal(1, s.System.PathJobsApplied);
        Assert.Equal(0, s.System.PathJobsInFlight);
    }

    [Fact]
    public void AnUngatedDrape_IsTimedInsideTheGate_SoTheWaitForAnotherMapIsNotChargedToThePathBudget()
    {
        const int HoldMs = 50;
        using var world = new WorldRuntime(
            new WorldRuntimeOptions
            {
                UpdateCompressionThreshold = 0,
                AutosaveIntervalMs = 0,
                CommandTimeBudgetMs = 0,
                Parallel = new ParallelMapOptions { MapUpdateThreads = 2, Ungated = "CreatureMapSystem", CreatureSplinePass = true },
            },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0, movementType: 1, wander: 5)]);
        (_, _, CreatureMapSystem system) = CreateAiSystem(content, world: world);
        var paths = new RecordingPathfinder();
        WorldCollision.Of(world).Install(pathfinder: paths);
        using var pool = new NavJobSystem(threads: 2);
        system.PathJobs = pool;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        world.GetMap(1, 0); // a second map: the maps phase runs on the map threads (and the serial gate is real) only with two or more
        using Scene s = new(world, system, player, Assert.Single(system.Creatures), paths, pool);

        // The wander leg is searched on the pool, held there until the test lets it finish.
        s.Paths.Gate.Reset();
        for (int i = 0; i < 300 && s.System.PathJobsSubmitted == 0; i++)
        {
            s.World.RunTick(100);
        }

        Assert.Equal(1, s.System.PathJobsSubmitted);
        s.Paths.Gate.Set();
        WaitForPool(s, 1);

        // Another thread holds the serial gate for HoldMs each time the maps phase runs (as another map's gated work would), queued
        // behind this map's own gated stages: its next hold starts as the map leaves the gate for the ungated wander pass.
        using var stop = new CancellationTokenSource();
        var holder = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!s.World.IsParallelPhase)
                {
                    Thread.Yield();
                    continue;
                }

                using (s.World.EnterSerial())
                {
                    Thread.Sleep(HoldMs);
                }
            }
        })
        { IsBackground = true };
        holder.Start();
        long ticksOfApplyingUpdate = -1;
        bool ranUngated = false;
        try
        {
            for (int i = 0; i < 20 && ticksOfApplyingUpdate < 0; i++)
            {
                long applied = s.System.PathJobsApplied;
                s.World.RunTick(50);
                if (s.System.PathJobsApplied > applied)
                {
                    ticksOfApplyingUpdate = s.System.PathTicksThisTick;
                    ranUngated = ((IMapUpdateBreakdown)s.System).DescribeLastUpdate().Contains(" wanderers outside the gate", StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            stop.Cancel();
            holder.Join();
        }

        Assert.True(ticksOfApplyingUpdate >= 0, "the finished path job was never applied");
        Assert.True(ranUngated, "the result was not applied by a wanderer outside the gate");
        double chargedMs = ticksOfApplyingUpdate * 1000.0 / Stopwatch.Frequency;
        Assert.True(chargedMs < HoldMs / 2.0, $"the drape was charged {chargedMs:F1} ms of path time, including the wait for the serial gate ({HoldMs} ms hold)");
    }
}
