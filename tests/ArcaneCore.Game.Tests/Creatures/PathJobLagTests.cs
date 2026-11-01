using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Bounds on a lagging follow answer from the path-job pool (<c>Creatures:Movement:PathJobsFollow</c>): an answer that would carry the
/// follower one leg past an owner who reversed is discarded, a lagging job that has not finished is waited for only so far and so
/// long, and a job asked for another target is dropped when the target switches.
/// </summary>
public sealed class PathJobLagTests
{
    /// <summary>A thread-safe pathfinder whose worker searches wait on <see cref="Gate"/> and answer the straight three-point path.</summary>
    private sealed class GatedPathfinder : IPathfinder, ISharedPathSearch
    {
        public ManualResetEventSlim Gate { get; } = new(true);

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => Answer(start, end);

        public bool CanSearchShared(uint mapId) => true;

        /// <summary>How many worker searches have entered <see cref="FindPathShared"/> (they may still be waiting on <see cref="Gate"/>).</summary>
        public int Entered => Volatile.Read(ref _entered);

        private int _entered;

        public PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options)
        {
            Interlocked.Increment(ref _entered);
            Gate.Wait(TimeSpan.FromSeconds(10));
            return Answer(start, end);
        }

        private static PathResult Answer(Vector3 start, Vector3 end) => new(PathType.Normal, [start, (start + end) * 0.5f, end]);
    }

    private sealed record Scene(WorldRuntime World, CreatureMapSystem System, Player Player, Creature Wolf, GatedPathfinder Paths, NavJobSystem Jobs)
        : IDisposable
    {
        public void Dispose()
        {
            Jobs.Dispose();
            World.Dispose();
        }
    }

    private static Scene Start()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0, movementType: 0, wander: 5)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        var paths = new GatedPathfinder();
        WorldCollision.Of(world).Install(pathfinder: paths);
        var pool = new NavJobSystem(threads: 2);
        system.PathJobs = pool;
        system.Options.Movement.PathJobsFollow = true;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        return new Scene(world, system, player, Assert.Single(system.Creatures), paths, pool);
    }

    private static void WaitUntilPublished(Scene s)
        => Assert.True(SpinWait.SpinUntil(() => s.System.PathJobsFinished == s.System.PathJobsInFlight, TimeSpan.FromSeconds(10)), "path job did not publish its answer");

    private static void WaitUntilWorkerHasIt(Scene s, int entered)
        => Assert.True(SpinWait.SpinUntil(() => s.Paths.Entered >= entered, TimeSpan.FromSeconds(10)), "no worker took the path job");

    [Fact]
    public void ALaggingAnswer_ThatWouldRunPastAReversedOwner_IsDiscarded()
    {
        // The owner stood 10 yd north of the wolf's line, the wolf 30 yd south of it; the owner then walked 6 yd back toward the wolf. The
        // finished path still ends at the owner's old point: closer than where the wolf stands, but 6 yd past the owner's new one.
        using Scene s = Start();
        s.Player.Relocate(0, -10, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Relocate(0, -30, s.Wolf.Z, s.Wolf.Orientation, 0);
        s.Paths.Gate.Reset();
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        s.World.RunTick(100);
        Assert.Equal(1, s.System.PathJobsSubmitted);

        s.Player.Relocate(0, -16, s.Player.Z, MathF.PI / 2f, 0);
        s.Paths.Gate.Set();
        WaitUntilPublished(s);
        s.Paths.Gate.Reset(); // the re-asked job waits
        s.World.RunTick(100);

        Assert.Equal(0, s.System.PathJobsApplied);
        Assert.Equal(1, s.System.PathJobsStale);
        Assert.Equal(2, s.System.PathJobsSubmitted);
        Assert.False(s.Wolf.IsMoving, "the follower set off on a leg that runs past its reversed owner");
        s.Paths.Gate.Set();
    }

    [Fact]
    public void ALaggingAnswer_ThatStillLeadsTowardAnOwnerWhoMovedOn_IsStillWalked()
    {
        // The owner walked on 3 yd in the direction the wolf travels: the lagging answer ends short of the new point and is taken.
        using Scene s = Start();
        s.Player.Relocate(0, -10, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Relocate(0, -30, s.Wolf.Z, s.Wolf.Orientation, 0);
        s.Paths.Gate.Reset();
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        s.World.RunTick(100);
        Assert.Equal(1, s.System.PathJobsSubmitted);

        s.Player.Relocate(0, -7, s.Player.Z, MathF.PI / 2f, 0);
        s.Paths.Gate.Set();
        WaitUntilPublished(s);
        s.World.RunTick(100);

        Assert.Equal(1, s.System.PathJobsApplied);
        Assert.True(s.Wolf.IsMoving, "the follower did not take the lagging answer");
    }

    [Fact]
    public void ALaggingJobThatHasNotFinished_IsDroppedOnceTheOwnerIsTooFarAhead()
    {
        using Scene s = Start();
        s.Player.Relocate(0, -10, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Relocate(0, -30, s.Wolf.Z, s.Wolf.Orientation, 0);
        s.Paths.Gate.Reset();
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        s.World.RunTick(100);
        Assert.Equal(1, s.System.PathJobsSubmitted);
        WaitUntilWorkerHasIt(s, 1);

        s.Player.Relocate(0, -10 + CreatureMapSystem.PathJobLagMaxDriftYards + 3f, s.Player.Z, MathF.PI / 2f, 0);
        s.World.RunTick(100);

        Assert.Equal(1, s.System.PathJobsStale);
        Assert.Equal(2, s.System.PathJobsSubmitted);
        s.Paths.Gate.Set();
    }

    [Fact]
    public void ALaggingJobThatIsStillQueued_IsNeverDroppedForTimeOrDistance()
    {
        // A one-thread pool whose only worker is held on another search: the follower's job stays queued (IsStarted false). Dropping and
        // resubmitting it would send it to the back of the lane, so it is waited for however long it takes and however far the owner goes.
        using Scene s = Start();
        using var pool = new NavJobSystem(threads: 1);
        s.System.PathJobs = pool;
        s.Paths.Gate.Reset();
        var blocker = new NavJob(s.Paths, 0u,new Vector3(100, 100, 0), new Vector3(110, 100, 0), PathOptions.Default);
        Assert.True(pool.TrySubmit(blocker));
        WaitUntilWorkerHasIt(s, 1);

        s.Player.Relocate(0, -10, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Relocate(0, -30, s.Wolf.Z, s.Wolf.Orientation, 0);
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        s.World.RunTick(100);
        Assert.Equal(1, s.System.PathJobsSubmitted);

        s.Player.Relocate(0, -7, s.Player.Z, MathF.PI / 2f, 0);
        for (long waited = 0; waited <= 2 * CreatureMapSystem.PathJobLagMaxWaitMs; waited += 100)
        {
            s.World.RunTick(100);
        }

        s.Player.Relocate(0, -10 + CreatureMapSystem.PathJobLagMaxDriftYards + 3f, s.Player.Z, MathF.PI / 2f, 0);
        s.World.RunTick(100);

        Assert.Equal(0, s.System.PathJobsStale);
        Assert.Equal(1, s.System.PathJobsSubmitted);
        Assert.Equal(1, s.Paths.Entered); // only the blocker has been taken by a worker
        s.Paths.Gate.Set();
    }

    [Fact]
    public void ALaggingJobThatHasNotFinished_IsDroppedAfterTheWaitBound()
    {
        using Scene s = Start();
        s.Player.Relocate(0, -10, s.Player.Z, MathF.PI / 2f, 0);
        s.Wolf.Relocate(0, -30, s.Wolf.Z, s.Wolf.Orientation, 0);
        s.Paths.Gate.Reset();
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        s.World.RunTick(100);
        Assert.Equal(1, s.System.PathJobsSubmitted);
        WaitUntilWorkerHasIt(s, 1); // the bound counts from when the map sees a worker running the job

        s.Player.Relocate(0, -7, s.Player.Z, MathF.PI / 2f, 0);
        s.World.RunTick(100);
        Assert.Equal(0, s.System.PathJobsStale); // within the bound the lagging job is waited for

        for (long waited = 0; waited <= CreatureMapSystem.PathJobLagMaxWaitMs; waited += 100)
        {
            s.World.RunTick(100);
        }

        Assert.True(s.System.PathJobsStale >= 1, "a lagging job that never answered was waited for without a time bound");
        Assert.True(s.System.PathJobsSubmitted >= 2);
        s.Paths.Gate.Set();
    }

    [Fact]
    public void ATargetSwitch_DropsThePendingJob()
    {
        using Scene s = Start();
        (Player other, _) = AddPlayer(s.World, 2, 0, 0);
        s.Player.Relocate(0, -10, s.Player.Z, MathF.PI / 2f, 0);
        other.Relocate(3, -10, other.Z, MathF.PI / 2f, 0);
        s.Wolf.Relocate(0, -30, s.Wolf.Z, s.Wolf.Orientation, 0);
        s.Paths.Gate.Reset();
        s.Wolf.Motion.MoveFollow(s.Player, 2, 0);
        s.World.RunTick(100);
        Assert.Equal(1, s.System.PathJobsSubmitted);

        s.Wolf.Motion.MoveFollow(other, 2, 0);
        s.World.RunTick(100);

        Assert.Equal(1, s.System.PathJobsStale);
        Assert.Equal(2, s.System.PathJobsSubmitted);
        Assert.Equal(1, s.System.PathJobsInFlight);
        s.Paths.Gate.Set();
    }
}
