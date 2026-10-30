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
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Creature searches on the path-job pool (<c>Creatures:Movement:PathJobs</c>, multithreading plan W5): eligible moves search on a
/// worker and start on a later update with the worker's path; a request that changed meanwhile drops the old answer, except that a
/// follow or chase answer that only lags its moving target is still walked when its path ends closer; follow (without PathJobsFollow)
/// and every move with the setting off stay on the map's thread.
/// </summary>
public sealed class CreaturePathJobTests(ITestOutputHelper output)
{
    /// <summary>A thread-safe pathfinder that answers a three-point path and records which thread searched.</summary>
    private class RecordingPathfinder : IPathfinder, ISharedPathSearch
    {
        private int _syncCalls;

        public ConcurrentBag<int> SharedThreads { get; } = [];

        public ManualResetEventSlim Gate { get; } = new(true);

        /// <summary>When set, the worker's answer (start, destination) instead of the three-point path.</summary>
        public Func<Vector3, Vector3, PathResult>? SharedAnswer { get; set; }

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
            return SharedAnswer?.Invoke(start, end) ?? Answer(start, end);
        }

        private static PathResult Answer(Vector3 start, Vector3 end) => new(PathType.Normal, [start, (start + end) * 0.5f, end]);
    }

    /// <summary>
    /// A <see cref="RecordingPathfinder"/> that also answers mesh walks (<see cref="IMeshPointQuery"/>), so a map counts as having
    /// navigation: <see cref="Walk"/> answers each <c>Raycast</c> (by default the walk reaches its end on the same surface). It has no
    /// random mesh point.
    /// </summary>
    private sealed class MeshRecordingPathfinder : RecordingPathfinder, IMeshPointQuery
    {
        public Func<Vector3, Vector3, MeshRaycastResult> Walk { get; set; } = (_, end) => new MeshRaycastResult(true, false, 1f, end);

        public int Walks { get; private set; }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Walks++;
            return Walk(start, end);
        }
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

    private static Scene Start(byte movementType, bool jobs = true, RecordingPathfinder? pathfinder = null)
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0, movementType: movementType, wander: 5)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        RecordingPathfinder paths = pathfinder ?? new RecordingPathfinder();
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

    /// <summary>What a run behind a moving target looked like (<see cref="RunBehindARunningTarget"/>).</summary>
    private sealed record RunReport(int StandingOutOfPlace, long AppliedWhileMoving, long Submitted, long Applied, long Stale, int MostInFlight)
    {
        public override string ToString()
            => $"stood out of place {StandingOutOfPlace} updates, applied while moving {AppliedWhileMoving}, submitted {Submitted}, applied {Applied}, stale {Stale}, most in flight {MostInFlight}";
    }

    /// <summary>
    /// The wolf is already running behind the player (its first answer applied); then the player runs on at 7 yd/s (0.7 yd per 100 ms
    /// update) for <paramref name="updates"/> updates while the pool answers each search one or two updates late (the gate opens after
    /// every second update). Counts the updates the wolf stood still while out of place (farther than <paramref name="inPlace"/> from
    /// the point it wants, <paramref name="wanted"/>), and the answers applied while it was on a spline.
    /// </summary>
    private static RunReport RunBehindARunningTarget(Scene s, int updates, Func<Vector3> wanted, float inPlace)
    {
        for (int i = 0; i < 50 && !s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(100);
            Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        }

        Assert.True(s.Wolf.IsMoving, "the wolf never set off");
        long submitted0 = s.System.PathJobsSubmitted;
        long applied0 = s.System.PathJobsApplied;
        long stale0 = s.System.PathJobsStale;
        s.Paths.Gate.Reset();
        int standing = 0;
        long appliedWhileMoving = 0;
        int mostInFlight = 0;
        float y = s.Player.Y;
        for (int i = 0; i < updates; i++)
        {
            y += 0.7f;
            s.Player.Relocate(s.Player.X, y, s.Player.Z, MathF.PI / 2f, 0);
            bool wasMoving = s.Wolf.IsMoving;
            long applied = s.System.PathJobsApplied;
            s.World.RunTick(100);
            mostInFlight = Math.Max(mostInFlight, s.System.PathJobsPending);
            if (wasMoving && s.System.PathJobsApplied > applied)
            {
                appliedWhileMoving++;
            }

            Vector3 point = wanted();
            if (!s.Wolf.IsMoving && Distance2D(s.Wolf, point.X, point.Y) > inPlace)
            {
                standing++;
            }

            if (i % 2 == 1)
            {
                // The answers land between this update and the next one: one or two updates after they were asked for.
                s.Paths.Gate.Set();
                Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
                s.Paths.Gate.Reset();
            }
        }

        s.Paths.Gate.Set();
        return new RunReport(standing, appliedWhileMoving, s.System.PathJobsSubmitted - submitted0, s.System.PathJobsApplied - applied0, s.System.PathJobsStale - stale0, mostInFlight);
    }

    [Fact]
    public void WithPathJobsFollow_AFollowerAlreadyRunning_TakesPoolAnswersMidSpline_AndNeverStopsBehindItsRunningOwner()
    {
        // The follower is on a spline when each answer lands, 0.7-1.4 yd past the job's start (it runs ~7 yd/s). Before the re-anchor
        // every such answer was dropped as stale (the start no longer fit) and asked again on every update, so the follower ran out its
        // spline, stood, and only then took an answer: stop-and-go behind a running owner, one abandoned job per update.
        using Scene s = Start(movementType: 0);
        s.System.Options.Movement.PathJobsFollow = true;
        s.Player.Relocate(5, 14, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Motion.MoveFollow(s.Player, 2, MathF.PI);
        Vector3 FollowPoint()
        {
            float reach = s.Player.BoundingRadius + s.Wolf.BoundingRadius + 2f;
            return new Vector3(s.Player.X + (reach * MathF.Cos(s.Player.Orientation + MathF.PI)), s.Player.Y + (reach * MathF.Sin(s.Player.Orientation + MathF.PI)), s.Player.Z);
        }

        RunReport run = RunBehindARunningTarget(s, updates: 60, FollowPoint, inPlace: FollowMovementGenerator.Leeway + 0.5f);
        output.WriteLine($"follow: {run}");

        Assert.True(run.AppliedWhileMoving >= 10, $"pool answers were not applied mid-spline: {run}");
        Assert.True(run.StandingOutOfPlace == 0, $"the follower stopped behind its running owner: {run}");
        Assert.True(run.Stale <= 2, $"jobs were abandoned and resubmitted while the follower ran: {run}");
        Assert.True(run.MostInFlight <= 1, $"more than one job in flight for one follower: {run}");
    }

    [Fact]
    public void AChaserAlreadyRunning_TakesPoolAnswersMidSpline_AndNeverStopsBehindItsRunningTarget()
    {
        using Scene s = Start(movementType: 0);
        s.System.Options.Movement.PathJobsFollow = true; // a chase takes answers mid-spline only with PathJobsFollow
        s.Player.Relocate(5, 14, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Motion.MoveChase(s.Player);
        Vector3 Victim() => new(s.Player.X, s.Player.Y, s.Player.Z);

        // In melee reach the chaser stands by design; anything farther must be closing.
        RunReport run = RunBehindARunningTarget(s, updates: 60, Victim, inPlace: s.Player.BoundingRadius + s.Wolf.BoundingRadius + 5f);
        output.WriteLine($"chase: {run}");

        Assert.True(run.AppliedWhileMoving >= 10, $"pool answers were not applied mid-spline: {run}");
        Assert.True(run.StandingOutOfPlace == 0, $"the chaser stopped behind its running target: {run}");
        Assert.True(run.Stale <= 2, $"jobs were abandoned and resubmitted while the chaser ran: {run}");
        Assert.True(s.Wolf.Motion.IsReachable);
    }

    [Fact]
    public void WithoutPathJobsFollow_ARunningChaser_KeepsDroppingAnswersItRanPast()
    {
        // PathJobs alone keeps a chase as it was: the mid-spline carry is part of PathJobsFollow.
        using Scene s = Start(movementType: 0);
        s.Player.Relocate(5, 14, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Motion.MoveChase(s.Player);
        Vector3 Victim() => new(s.Player.X, s.Player.Y, s.Player.Z);

        RunReport run = RunBehindARunningTarget(s, updates: 20, Victim, inPlace: s.Player.BoundingRadius + s.Wolf.BoundingRadius + 5f);
        output.WriteLine($"chase without PathJobsFollow: {run}");

        Assert.True(run.Stale >= 10, $"a chase carried answers mid-spline without PathJobsFollow: {run}");
    }

    /// <summary>
    /// The wolf follows (or chases) the player on a spline (answers land at once until it is moving); then the player steps 0.6 yd on,
    /// the moving wolf asks (one job), runs one more update while the job waits (its start is now ~0.7 yd behind it, on its spline),
    /// <paramref name="beforeAnswer"/> runs, the job finishes, and the update that claims it runs. Returns how the applied and stale
    /// counters moved on that claiming update.
    /// </summary>
    private static (long Applied, long Stale) ClaimACarriedAnswer(Scene s, Action? beforeAnswer = null, bool chase = false)
    {
        s.System.Options.Movement.PathJobsFollow = true;
        s.Player.Relocate(5, 30, s.Player.Z, MathF.PI / 2f, 0);
        if (chase)
        {
            s.Wolf.Motion.MoveChase(s.Player);
        }
        else
        {
            s.Wolf.Motion.MoveFollow(s.Player, 2, MathF.PI);
        }

        for (int i = 0; i < 50 && !s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(100);
            Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        }

        Assert.True(s.Wolf.IsMoving, "the wolf never set off");
        s.Paths.Gate.Reset();
        s.Player.Relocate(5, 30.6f, s.Player.Z, MathF.PI / 2f, 0);
        long submitted = s.System.PathJobsSubmitted;
        s.World.RunTick(100); // asks: one job, from where it runs now
        Assert.Equal(submitted + 1, s.System.PathJobsSubmitted);
        s.World.RunTick(100); // runs on; the job is waited for, carried
        Assert.Equal(submitted + 1, s.System.PathJobsSubmitted);
        Assert.True(s.Wolf.IsMoving);
        beforeAnswer?.Invoke();
        long applied = s.System.PathJobsApplied;
        long stale = s.System.PathJobsStale;
        s.Paths.Gate.Set();
        Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        s.Paths.Gate.Reset();
        s.World.RunTick(100);
        (long, long) moved = (s.System.PathJobsApplied - applied, s.System.PathJobsStale - stale);
        s.Paths.Gate.Set();
        return moved;
    }

    [Fact]
    public void WithAMesh_ACarriedAnswer_IsWalkedFromHere_WhenItsFirstLegWalks()
    {
        var mesh = new MeshRecordingPathfinder();
        using Scene s = Start(movementType: 0, pathfinder: mesh);
        int walks = 0;
        (long applied, long stale) = ClaimACarriedAnswer(s, () => walks = mesh.Walks);

        Assert.Equal((1L, 0L), (applied, stale));
        Assert.True(mesh.Walks > walks, "the re-anchored first leg was not walked on the mesh");
    }

    [Fact]
    public void WithAMesh_ACarriedAnswerWhoseFirstLegIsBlocked_IsDroppedAndAskedAgain()
    {
        var mesh = new MeshRecordingPathfinder();
        using Scene s = Start(movementType: 0, pathfinder: mesh);
        (long applied, long stale) = ClaimACarriedAnswer(s, () => mesh.Walk = (start, end) => new MeshRaycastResult(true, true, 0.1f, Vector3.Lerp(start, end, 0.1f)));

        Assert.Equal((0L, 1L), (applied, stale));
        Assert.Equal(1, s.System.PathJobsInFlight); // asked again from where it stands
    }

    [Fact]
    public void WithAMesh_ACarriedAnswer_IsRefused_WhenTheCreatureCannotBePlacedOnTheMesh()
    {
        // A map with navigation whose mesh has no polygon under the creature (off the mesh, tile not loaded): the leg cannot be checked,
        // so it is not taken unchecked.
        var mesh = new MeshRecordingPathfinder();
        using Scene s = Start(movementType: 0, pathfinder: mesh);
        (long applied, long stale) = ClaimACarriedAnswer(s, () => mesh.Walk = (_, end) => MeshRaycastResult.Unavailable(end));

        Assert.Equal((0L, 1L), (applied, stale));
    }

    [Fact]
    public void WithAMesh_ACarriedAnswerWhoseFirstLegEndsOnAnotherLayer_IsDroppedAndAskedAgain()
    {
        // The walk reaches the corner's column, but on the floor 5 yd under it (the corner is on a bridge): not the same surface.
        var mesh = new MeshRecordingPathfinder();
        using Scene s = Start(movementType: 0, pathfinder: mesh);
        (long applied, long stale) = ClaimACarriedAnswer(s, () => mesh.Walk = (_, end) => new MeshRaycastResult(true, false, 1f, end with { Z = end.Z - 5f }));

        Assert.Equal((0L, 1L), (applied, stale));
    }

    [Fact]
    public void ACarriedNoPathAnswer_IsNotApplied_AndIsAskedAgain()
    {
        // No path from a point the chaser has already run past says nothing about where it stands: not applied (it would mark the target
        // unreachable and feed the evade), asked again from here.
        using Scene s = Start(movementType: 0);
        (long applied, long stale) = ClaimACarriedAnswer(s, () => s.Paths.SharedAnswer = (_, _) => new PathResult(PathType.NoPath, []), chase: true);

        Assert.Equal((0L, 1L), (applied, stale));
        Assert.True(s.Wolf.Motion.IsReachable, "a carried no-path answer marked the chase target unreachable");
        Assert.Equal(1, s.System.PathJobsInFlight);
    }

    [Fact]
    public void ACarriedAnswer_DropsACornerTheCreatureHasAlreadyPassed()
    {
        // The answer's first corner lies 0.4 yd past the job's start, on the stretch the follower has since run: walking it would turn the
        // follower back. The re-anchored path heads for the next corner instead.
        using Scene s = Start(movementType: 0);
        Vector3 passed = default;
        (long applied, _) = ClaimACarriedAnswer(s, () => s.Paths.SharedAnswer = (start, end) =>
        {
            passed = start + (Vector3.Normalize(end - start) * 0.4f);
            return new PathResult(PathType.Normal, [start, passed, end]);
        });

        Assert.Equal(1, applied);
        CreatureSpline spline = Assert.IsType<CreatureSpline>(s.Wolf.Spline);
        foreach (Vector3 point in spline.Points)
        {
            Assert.True(Vector3.Distance(point, passed) > 0.2f, $"the new spline still turns back to the passed corner {passed}");
        }

        Assert.True(Vector3.Distance(new Vector3(spline.StartX, spline.StartY, spline.StartZ), passed) > 0.5f);
    }

    [Fact]
    public void AnAnswerForAFollowerThatRanMoreThanTheCarryDistance_IsDroppedAndAskedAgain()
    {
        // The pool answers slower than the follower runs PathJobCarryYards along its spline: the job is dropped and asked again from where
        // the follower then is (still one job in flight).
        using Scene s = Start(movementType: 0);
        s.System.Options.Movement.PathJobsFollow = true;
        s.Player.Relocate(5, 40, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Motion.MoveFollow(s.Player, 2, MathF.PI);
        for (int i = 0; i < 50 && !s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(100);
            Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        }

        s.Paths.Gate.Reset();
        s.Player.Relocate(5, 40.6f, s.Player.Z, MathF.PI / 2f, 0);
        s.World.RunTick(100);
        Assert.Equal(1, s.System.PathJobsInFlight);
        long stale = s.System.PathJobsStale;
        var asked = new Vector3(s.Wolf.X, s.Wolf.Y, s.Wolf.Z);
        float ran = 0f;
        for (int i = 0; i < 30 && ran <= CreatureMapSystem.PathJobCarryYards; i++)
        {
            Assert.True(s.Wolf.IsMoving, "the follower stopped before it ran the carry distance");
            Assert.Equal(stale, s.System.PathJobsStale); // within the carry distance the job is waited for
            s.World.RunTick(100);
            ran = Vector3.Distance(asked, new Vector3(s.Wolf.X, s.Wolf.Y, s.Wolf.Z));
        }

        Assert.True(ran > CreatureMapSystem.PathJobCarryYards, $"the follower ran only {ran:F1} yd");
        Assert.Equal(stale + 1, s.System.PathJobsStale);
        Assert.Equal(1, s.System.PathJobsInFlight);
        s.Paths.Gate.Set();
    }

    [Fact]
    public void AnAnswerForAFollowerThatRanItsSplineOut_IsTakenWhereItStands()
    {
        // The follower's spline ends while its job waits: it stands at the spline's end, still on the path it ran, so the answer is carried.
        using Scene s = Start(movementType: 0);
        s.System.Options.Movement.PathJobsFollow = true;
        s.Player.Relocate(5, 6, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Motion.MoveFollow(s.Player, 2, MathF.PI);
        for (int i = 0; i < 50 && !s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(100);
            Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        }

        Assert.True(s.Wolf.IsMoving);
        CreatureSpline running = s.Wolf.Spline!;
        s.Paths.Gate.Reset();
        s.Player.Relocate(5, 8, s.Player.Z, MathF.PI / 2f, 0); // 2 yd on: the follow point leaves the spline's end behind
        long submitted = s.System.PathJobsSubmitted;
        s.World.RunTick(100);
        Assert.Equal(submitted + 1, s.System.PathJobsSubmitted);
        Assert.Same(running, s.Wolf.Spline);
        var asked = new Vector3(s.Wolf.X, s.Wolf.Y, s.Wolf.Z);
        long stale = s.System.PathJobsStale;
        for (int i = 0; i < 30 && s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(100);
        }

        Assert.False(s.Wolf.IsMoving, "the follower's spline did not run out");
        var end = new Vector3(running.EndX, running.EndY, running.EndZ);
        Assert.True(Vector3.Distance(asked, end) > CreatureMapSystem.PathJobStartTolerance, "the job's start was not left behind");
        Assert.True(Vector3.Distance(asked, end) <= CreatureMapSystem.PathJobCarryYards);
        s.World.RunTick(100); // standing at the spline's end it asks again: the job is still waited for
        Assert.Equal(stale, s.System.PathJobsStale);
        Assert.Equal(submitted + 1, s.System.PathJobsSubmitted);

        long applied = s.System.PathJobsApplied;
        s.Paths.Gate.Set();
        Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        s.World.RunTick(100);

        Assert.Equal(applied + 1, s.System.PathJobsApplied);
        Assert.Equal(stale, s.System.PathJobsStale);
        Assert.True(s.Wolf.IsMoving, "the follower did not set off on the carried answer");
    }

    [Fact]
    public void AJobStuckInAWorker_IsDroppedAfterTheTimeout_AndAskedAgain()
    {
        // A standing creature waits for its queued job; one still not answered PathJobPendingTimeoutMs after it was submitted is dropped
        // and asked again, so a search stuck in a worker cannot hold the creature forever.
        using Scene s = Start(movementType: 0);
        s.Paths.Gate.Reset();
        s.System.MoveTo(s.Wolf, 15, 0, 83.5f, run: false, finalOrientation: null);
        Assert.Equal(1, s.System.PathJobsSubmitted);
        for (int i = 0; i < 45; i++)
        {
            s.World.RunTick(100);
        }

        Assert.Equal(0, s.System.PathJobsStale);
        for (int i = 0; i < 10; i++)
        {
            s.World.RunTick(100);
        }

        Assert.Equal(1, s.System.PathJobsStale);
        Assert.Equal(2, s.System.PathJobsSubmitted);
        s.Paths.Gate.Set();
    }

    [Fact]
    public void WithPathJobsFollow_AnAnswerForAFollowerThatLeftItsSpline_IsDroppedAndAskedAgain()
    {
        // Only travel along the creature's own spline carries a job's start: a creature stopped and put elsewhere (a knockback, a
        // teleport) is no longer on the path the job was asked from, so the answer is dropped as before.
        using Scene s = Start(movementType: 0);
        s.System.Options.Movement.PathJobsFollow = true;
        s.Player.Relocate(5, 30, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Motion.MoveFollow(s.Player, 2, MathF.PI);
        for (int i = 0; i < 50 && !s.Wolf.IsMoving; i++)
        {
            s.World.RunTick(100);
            Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        }

        Assert.True(s.Wolf.IsMoving);
        s.Paths.Gate.Reset();
        s.Player.Relocate(5, 31, s.Player.Z, MathF.PI / 2f, 0);
        s.World.RunTick(100); // the moving follower asks again: one job in flight, asked from a point on its spline
        Assert.Equal(1, s.System.PathJobsInFlight);
        long stale = s.System.PathJobsStale;
        long applied = s.System.PathJobsApplied;

        s.System.StopMoving(s.Wolf);
        s.Wolf.Relocate(s.Wolf.X + 3f, s.Wolf.Y, s.Wolf.Z, s.Wolf.Orientation, 0);
        s.Paths.Gate.Set();
        Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        s.Paths.Gate.Reset();
        s.World.RunTick(100);

        Assert.Equal(applied, s.System.PathJobsApplied);
        Assert.Equal(stale + 1, s.System.PathJobsStale);
        Assert.Equal(1, s.System.PathJobsInFlight); // asked again from where it now stands
        s.Paths.Gate.Set();
    }

    /// <summary>
    /// Lets the creature's one queued job finish while the target moves <paramref name="drift"/> yd on, then runs one update: the job's
    /// answer is now a lagging one (its point is more than PathJobFollowTolerance from the re-asked one).
    /// </summary>
    private static void AnswerAfterTheTargetMoved(Scene s, float drift)
    {
        Assert.Equal(1, s.System.PathJobsSubmitted);
        s.Player.Relocate(s.Player.X, s.Player.Y + drift, s.Player.Z, s.Player.Orientation, 0);
        s.Paths.Gate.Set();
        Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");
        s.Paths.Gate.Reset(); // the re-asked job waits
        s.World.RunTick(100);
    }

    [Fact]
    public void WithPathJobsFollow_ALaggingPartialAnswerThatEndsFartherFromTheOwner_IsDroppedAndAskedAgain()
    {
        // What counts is where the found path ends, not the point it was asked for: a partial path that stops farther from the owner's
        // new follow point than the follower already stands would walk it away from its owner.
        using Scene s = Start(movementType: 0);
        s.System.Options.Movement.PathJobsFollow = true;
        s.Player.Relocate(0, -10, s.Player.Z, MathF.PI / 2f, 0);
        s.Paths.SharedAnswer = (start, end) => new PathResult(PathType.Incomplete, [start, start + (Vector3.Normalize(start - end) * 5f)]);
        s.Paths.Gate.Reset();
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        float startX = s.Wolf.X;
        float startY = s.Wolf.Y;

        AnswerAfterTheTargetMoved(s, drift: 2f);

        Assert.Equal(0, s.System.PathJobsApplied);
        Assert.Equal(1, s.System.PathJobsStale);
        Assert.Equal(2, s.System.PathJobsSubmitted);
        Assert.False(s.Wolf.IsMoving, "the follower set off on a partial path that leads away from its owner");
        Assert.True(Distance2D(s.Wolf, startX, startY) < 0.01f);
        s.Paths.Gate.Set();
    }

    [Fact]
    public void ALaggingNoPathAnswer_DoesNotMarkTheChaseTargetUnreachable_AndIsAskedAgain()
    {
        // A no-path answer for a point the target has already left says nothing about the target: applying it would set IsReachable false
        // and feed the unreachable-target evade (CreatureMapSystem.Combat) for a stale point.
        using Scene s = Start(movementType: 0);
        s.Player.Relocate(25, 0, s.Player.Z, 0, 0);
        s.Paths.SharedAnswer = (_, _) => new PathResult(PathType.NoPath, []);
        s.Paths.Gate.Reset();
        s.Wolf.Motion.MoveChase(s.Player);

        AnswerAfterTheTargetMoved(s, drift: 2f);

        Assert.True(s.Wolf.Motion.IsReachable, "a lagging no-path answer marked the chase target unreachable");
        Assert.Equal(0, s.System.PathJobsApplied);
        Assert.Equal(1, s.System.PathJobsStale);
        Assert.Equal(2, s.System.PathJobsSubmitted);
        s.Paths.Gate.Set();
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
    [Trait("Category", "Timing")] // asserts a Stopwatch budget while a background thread holds the gate
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
