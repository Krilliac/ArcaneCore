using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Tests.Docs;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Fakes;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A bot think never runs a far navigation-mesh search on the world thread: the query becomes a path job (<see cref="PlayerbotPathJobs"/>),
/// the bot answers "pending" like it does for a tile still being read, and a later think applies the finished answer. A fake pathfinder
/// counts the owner searches (the world thread) and a manual runner holds the jobs until a test lets a worker take them, so
/// no real thread decides anything here.
/// </summary>
public sealed class PlayerbotPathJobTests
{
    private static PlayerbotOptions Options(bool jobs = true, int syncMs = 0) => new()
    {
        Enabled = true, MaxPathPoints = 128, MaxRouteYards = 2000, PathJobs = jobs, SyncPathQueryMaxMs = syncMs,
    };

    private static bool IsPending(PathResult path) => (path.Type & PathType.TilesPending) != 0;

    /// <summary>The service, its manual runner and clocks, and the fake pathfinder, bound to the world bot.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly RiskTestWorld _world;

        public Harness(RiskTestWorld world, PlayerbotOptions options, ManualSharedPathSearch? nav = null)
        {
            _world = world;
            Options = options;
            Nav = nav ?? new ManualSharedPathSearch();
            WorldCollision.Of(world.Host.World).Install(pathfinder: Nav);
            Runner = new ManualPathJobRunner();
            Jobs = new PlayerbotPathJobs(options, Runner, () => Clock) { WallMs = () => Wall };
            Origin = new Vector3(Player.X, Player.Y, Player.Z);
        }

        public PlayerbotOptions Options { get; }
        public ManualSharedPathSearch Nav { get; }
        public ManualPathJobRunner Runner { get; }
        public PlayerbotPathJobs Jobs { get; }
        public PlayerbotThinkProbe Probe { get; } = new();
        public Guid Bot { get; } = Guid.NewGuid();
        public uint Clock { get; set; } = 100_000;
        public long Wall { get; set; } = 1_000_000;
        public Vector3 Origin { get; set; }
        public Player Player => _world.Player;
        public MapCollision Collision => Player.Map!.Collision;

        public Vector3 Far(float yards = 250f, float dy = 0f) => Origin + new Vector3(yards, dy, 0);

        /// <summary>One think: a probe begun, the bot bound, the body run, the probe ended.</summary>
        public T Think<T>(Func<T> body, Guid? bot = null)
        {
            Probe.SyncQueryMaxMs = Options.SyncPathQueryMaxMs;
            Probe.Begin();
            using PlayerbotPathJobs.BindScope bound = Jobs.Bind(bot ?? Bot, Player);
            try { return body(); }
            finally { Probe.End(); }
        }

        public static PathOptions Query(int nodes) => new()
        {
            MaxPoints = 128, Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, MaxSearchNodes = nodes, LoadTiles = true, NonBlockingTiles = true,
        };

        /// <summary>A far query inside the deferrable scope of a planning wrapper.</summary>
        public PathResult Ask(Vector3 to, int nodes = 12_000, Guid? bot = null, Vector3? from = null)
            => Think(() =>
            {
                using PlayerbotPathJobs.DeferrableScope scope = PlayerbotPathJobs.Deferrable();
                return PlayerbotThinkProbe.Find(Collision, from ?? Origin, to, Query(nodes));
            }, bot);

        public PlanOutcome Plan(Vector3 to, ref TileWait wait, uint now, out PlayerbotRoute? route, bool chase = false, bool trial = false)
        {
            PlayerbotRoute? planned = null;
            TileWait local = wait;
            PlanOutcome outcome = Think(() => PlayerbotNavigation.PlanExact(Player, to, Options, ref local, now, out planned, chase: chase, trial: trial));
            wait = local;
            route = planned;
            return outcome;
        }

        public void Dispose() => Runner.Dispose();
    }

    // ---- 1. a far query is deferred, completes, and is applied ---------------------------------------------------------------------

    [Fact]
    public async Task AFarPlan_IsPendingAtFirst_NeverSearchesOnTheWorldThread_AndRoutesOnceItsJobIsDone_LikeTheSynchronousOracle()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var jobsOn = new Harness(world, Options());
            var wait = new TileWait();
            Vector3 far = jobsOn.Far();

            Assert.Equal(PlanOutcome.Pending, jobsOn.Plan(far, ref wait, 1_000, out PlayerbotRoute? first));
            Assert.Null(first);
            Assert.Equal(0, jobsOn.Nav.OwnerCalls);
            Assert.Single(jobsOn.Runner.Submitted);

            jobsOn.Runner.CompleteAll();
            Assert.Equal(PlanOutcome.Route, jobsOn.Plan(far, ref wait, 1_500, out PlayerbotRoute? routed));
            Assert.NotNull(routed);
            Assert.True(routed.Points.Count >= 2);
            Assert.Equal(0, jobsOn.Nav.OwnerCalls);
            Assert.True(jobsOn.Runner.Submitted.Count >= 1);
            Assert.Equal(1, jobsOn.Jobs.Totals.Applied);

            // The oracle: the same scenario with jobs off searches on the world thread and plans the same route.
            using var jobsOff = new Harness(world, Options(jobs: false));
            var oracleWait = new TileWait();
            Assert.Equal(PlanOutcome.Route, jobsOff.Plan(far, ref oracleWait, 1_000, out PlayerbotRoute? oracle));
            Assert.Equal(1, jobsOff.Nav.OwnerCalls);
            Assert.Empty(jobsOff.Runner.Submitted);
            Assert.Equal(oracle!.Points, routed.Points);
            return true;
        });
    }

    // ---- 2. stale answers are dropped ------------------------------------------------------------------------------------------------

    /// <summary>Regression guard (green before the fix too): a bot that left its route is never given the answer asked where it stood.</summary>
    [Fact]
    public async Task ABotThatWalkedOffItsRoute_NeverGetsTheOldAnswer()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Assert.True(IsPending(h.Ask(h.Far())));
            h.Runner.CompleteAll();

            Vector3 moved = h.Origin + new Vector3(0, 12, 0); // 12 yards off the route (the route runs along x)
            h.Player.Relocate(moved.X, moved.Y, moved.Z);
            Assert.True(IsPending(h.Ask(h.Far(), from: moved)));
            PlayerbotPathJobTotals totals = h.Jobs.Totals;
            Assert.Equal(0, totals.Applied);
            Assert.Equal(1, totals.Stale);
            Assert.Equal(2, h.Runner.Submitted.Count);
            return true;
        });
    }

    [Fact]
    public async Task AWalkingBot_CollectsItsRunningJob_SplicedFromWhereItStands()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Vector3 d = h.Far();
            Assert.True(IsPending(h.Ask(d)));

            Vector3 here = h.Origin + new Vector3(6, 0, 0);
            h.Player.Relocate(here.X, here.Y, here.Z);
            Assert.True(IsPending(h.Ask(d, from: here)));
            Assert.Single(h.Runner.Submitted); // the job asked from the old spot is kept, not abandoned and asked again
            h.Runner.CompleteAll();

            Vector3 later = h.Origin + new Vector3(9, 0, 0);
            h.Player.Relocate(later.X, later.Y, later.Z);
            PathResult answer = h.Ask(d, from: later);
            Assert.False(IsPending(answer));
            Assert.Equal(later, answer.Points[0]);
            Assert.Equal(d, answer.Points[^1]);
            Assert.Equal(0, h.Nav.OwnerCalls);
            Assert.Equal(1, h.Jobs.Totals.Applied);
            Assert.Equal(0, h.Jobs.Totals.Stale);
            return true;
        });
    }

    /// <summary>A bot walking a route: its stored position lags its extrapolated one by up to a heartbeat, and the query's from is the extrapolated one.</summary>
    [Fact]
    public async Task AWalkingBotWithMotionState_FromAheadOfItsStoredPosition_KeepsAndCollectsItsJob()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Vector3 d = h.Far();
            PlayerbotMotion.MotionState motion = PlayerbotMotion.Of(h.Player);
            motion.World = world.Host.World;
            motion.Map = h.Player.Map;
            motion.Route = new PlayerbotRoute([h.Origin, h.Origin + new Vector3(300f, 0, 0)], 300f);
            motion.Anchor = h.Origin;
            motion.Speed = 0.007f; // a slow walker anchored long ago: 3.5 yards ahead, and the real clock moves it only a hair between asks
            motion.AnchorMs = world.Host.World.NowMs - 500_000;

            Vector3 ahead = PlayerbotMotion.CurrentPosition(h.Player);
            Assert.True(Vector3.Distance(ahead, h.Origin) > 2f, "the motion position is ahead of the stored one");
            Assert.True(IsPending(h.Ask(d, from: ahead)));
            Assert.True(IsPending(h.Ask(d, from: PlayerbotMotion.CurrentPosition(h.Player))));
            Assert.Single(h.Runner.Submitted); // kept across thinks, not abandoned and asked again
            h.Runner.CompleteAll();

            Vector3 later = PlayerbotMotion.CurrentPosition(h.Player);
            PathResult answer = h.Ask(d, from: later);
            Assert.False(IsPending(answer));
            Assert.True(Vector3.Distance(later, answer.Points[0]) < 1f);
            Assert.Equal(d, answer.Points[^1]);
            Assert.Equal(0, h.Nav.OwnerCalls);
            Assert.Equal(1, h.Jobs.Totals.Applied);
            Assert.Equal(0, h.Jobs.Totals.Stale);
            return true;
        });
    }

    [Fact]
    public async Task ANearAnswerUnderPlanExact_IsARoute_WithoutAPathEndsShortAnomaly()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            using AnomalyCapture cap = BotAnomalyCapture.For(h.Player);
            var wait = new TileWait();
            Vector3 far = h.Far(250);
            Assert.Equal(PlanOutcome.Pending, h.Plan(far, ref wait, 1_000, out _));
            h.Runner.CompleteAll();

            Assert.Equal(PlanOutcome.Route, h.Plan(far + new Vector3(0, 20, 0), ref wait, 1_500, out PlayerbotRoute? route));
            Assert.NotNull(route);
            Assert.Equal(0, cap.Count(PlayerbotAnomalies.Key("nav", "path-ends-short")));
            Assert.Equal(0, h.Nav.OwnerCalls);
            return true;
        });
    }

    /// <summary>Covers a precedence that already holds (green before the fix): the navigation executor's plan answers before any path job.</summary>
    [Fact]
    public async Task InsideAnAsyncNavigationPlan_AFarQuerySubmitsNoPathJob()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            var plan = new PlayerbotAsyncNavigationPlan(h.Player.Map!.MapId, h.Origin);
            using (plan.Activate())
            {
                h.Ask(h.Far());
            }

            Assert.Empty(h.Runner.Submitted);
            Assert.Equal(0, h.Jobs.Totals.Submitted);
            Assert.Equal(0, h.Nav.OwnerCalls);
            return true;
        });
    }

    [Fact]
    public async Task AJobHold_PausesTheTileWait_InsteadOfResettingIt()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            var wait = new TileWait();
            foreach (uint at in new uint[] { 0, 4_000, 8_000 })
            {
                PlayerbotPathJobs.NoteTilePending();
                Assert.True(wait.Hold(at));
            }

            // One think waits for a path job (a far plan whose job is queued): one second of the wait is not tile time.
            Assert.Equal(PlanOutcome.Pending, h.Plan(h.Far(), ref wait, 9_000, out _));

            PlayerbotPathJobs.NoteTilePending();
            Assert.True(wait.Hold(13_000));
            PlayerbotPathJobs.NoteTilePending();
            Assert.False(wait.Hold(17_000), "16 s of tile wait, 1 s of job wait excluded: past the 15 s bound");
            return true;
        });
    }

    [Fact]
    public async Task AnAnswerSearchedOnAnotherPathfinder_IsNeverApplied()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Assert.True(IsPending(h.Ask(h.Far())));
            h.Runner.CompleteAll();

            var reloaded = new ManualSharedPathSearch(); // the dataset was reloaded: another pathfinder object
            WorldCollision.Of(world.Host.World).Install(pathfinder: reloaded);
            Assert.True(IsPending(h.Ask(h.Far())));
            Assert.Equal(0, h.Jobs.Totals.Applied);
            Assert.Equal(1, h.Jobs.Totals.Stale);
            Assert.Equal(2, h.Runner.Submitted.Count);
            Assert.Equal(0, h.Nav.OwnerCalls);
            return true;
        });
    }

    [Fact]
    public async Task AGoalNobodyAsksForAgain_IsSweptAfterTenSeconds_AndItsAnswerIsNeverApplied()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Vector3 goal = h.Far();
            Assert.True(IsPending(h.Ask(goal)));
            h.Runner.CompleteAll();

            h.Clock += PlayerbotPathJobs.UnclaimedMs + 1;
            h.Jobs.Sweep();
            Assert.True(IsPending(h.Ask(goal)), "the swept answer must not come back");
            Assert.Equal(0, h.Jobs.Totals.Applied);
            Assert.Equal(2, h.Runner.Submitted.Count);
            return true;
        });
    }

    // ---- 3. logout ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABotThatLeaves_AbandonsItsQueuedJob_NothingIsSearchedOrApplied_AndTheSlotIsFree()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Assert.True(IsPending(h.Ask(h.Far())));
            Assert.Equal(1, h.Jobs.InFlight);

            h.Jobs.Forget(h.Bot);
            h.Runner.CompleteAll();
            Assert.Equal(0, h.Nav.SharedCalls);
            Assert.Equal(NavJobStatus.Canceled, h.Runner.Submitted[0].Status);
            Assert.Equal(0, h.Jobs.InFlight);

            Assert.True(IsPending(h.Ask(h.Far())), "a bot that comes back starts over");
            Assert.Equal(0, h.Jobs.Totals.Applied);
            Assert.Equal(2, h.Runner.Submitted.Count);
            return true;
        });
    }

    // ---- 4. limits -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondFarQueryOfTheSameBot_WaitsForTheFirst_WithoutASecondJob()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Assert.True(IsPending(h.Ask(h.Far(250))));
            Assert.True(IsPending(h.Ask(h.Far(300, 40))));
            Assert.Single(h.Runner.Submitted);
            Assert.Equal(0, h.Nav.OwnerCalls);
            return true;
        });
    }

    [Fact]
    public async Task ThousandBots_HoldAtMostTheGlobalLimits_AllWait_NoneSearchesOnTheWorldThread_AndTheOldestWaiterIsServedFirst()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Guid[] bots = [.. Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid())];
            foreach (Guid bot in bots)
                Assert.True(IsPending(h.Ask(h.Far(), nodes: 8192, bot: bot)), "every bot waits, none is refused");

            PlayerbotPathJobTotals totals = h.Jobs.Totals;
            Assert.InRange(totals.Submitted, 1, 64);
            Assert.InRange(h.Jobs.InFlightNodes, 1, h.Options.PathJobsMaxInFlightNodes);
            Assert.Equal(32, totals.Submitted); // 262144 nodes / 8192
            Assert.Equal(0, h.Nav.OwnerCalls);
            Assert.Equal(1000 - 32, h.Jobs.Waiters);
            Assert.Equal(bots[32], h.Jobs.LineForTests()[0]);

            h.Runner.CompleteAll();
            Assert.True(IsPending(h.Ask(h.Far(), nodes: 8192, bot: bots[999])));
            Assert.Equal(32, h.Runner.Submitted.Count); // the newest waiter does not overtake the line
            Assert.True(IsPending(h.Ask(h.Far(), nodes: 8192, bot: bots[32])));
            Assert.Equal(33, h.Runner.Submitted.Count);
            Assert.DoesNotContain(bots[32], h.Jobs.LineForTests());
            return true;
        });
    }

    // ---- 6/7. a wait always ends, and never as "unreachable" ----------------------------------------------------------------------

    [Fact]
    public async Task AJobWait_IsNeverSpentAsTheTileBound_ReportsOnceWhenLong_AndEscalatesToOneBoundedSynchronousSearch()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            PlayerbotOptions options = Options(syncMs: 100);
            options.PathJobWaitMaxMs = 120_000;
            using var h = new Harness(world, options);
            using AnomalyCapture cap = BotAnomalyCapture.For(h.Player);
            var wait = new TileWait();
            Vector3 far = h.Far();
            uint now = 1_000;
            for (int step = 0; step < 14; step++)
            {
                Assert.Equal(PlanOutcome.Pending, h.Plan(far, ref wait, now, out _));
                now += 5_000;
                h.Wall += 5_000;
            }

            Assert.True(h.Wall - 1_000_000 >= 60_000);
            Assert.Equal(1, cap.Count(PlayerbotAnomalies.Key("nav", "job-wait-long")));
            Assert.True(wait.Active, "a job wait does not spend the tile wait");
            Assert.Equal(0, h.Nav.OwnerCalls);

            h.Wall += 130_000;
            Assert.Equal(PlanOutcome.Route, h.Plan(far, ref wait, now, out _));
            Assert.Equal(1, h.Nav.OwnerCalls);
            Assert.Equal(100, h.Nav.OwnerOptions[0].MaxSearchMs);
            Assert.Equal(1, h.Jobs.Totals.Escalations);
            Assert.Equal(1, cap.Count(PlayerbotAnomalies.Key("nav", "job-escalated")));
            return true;
        });
    }

    [Fact]
    public async Task ThreeCanceledJobs_EscalateTheFourthAskToOneSynchronousSearch()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options(syncMs: 100));
            Assert.True(IsPending(h.Ask(h.Far())));
            for (int i = 0; i < 3; i++)
            {
                Assert.True(h.Runner.CancelNext());
                if (i < 2)
                {
                    Assert.True(IsPending(h.Ask(h.Far())));
                    Assert.Equal(0, h.Nav.OwnerCalls);
                }
            }

            PathResult sync = h.Ask(h.Far());
            Assert.False(IsPending(sync));
            Assert.Equal(1, h.Nav.OwnerCalls);
            Assert.Equal(100, h.Nav.OwnerOptions[0].MaxSearchMs);
            Assert.Equal(3, h.Jobs.Totals.Canceled);
            Assert.Equal(1, h.Jobs.Totals.Escalations);
            return true;
        });
    }

    [Fact]
    public async Task AFaultedJob_EscalatesAtOnce()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Assert.True(IsPending(h.Ask(h.Far())));
            Assert.True(h.Runner.FaultNext());
            Assert.False(IsPending(h.Ask(h.Far())));
            Assert.Equal(1, h.Nav.OwnerCalls);
            Assert.Equal(1, h.Jobs.Totals.Faulted);
            return true;
        });
    }

    // ---- 8. tiles the detached search missed ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PathType.Incomplete, SharedMissVerdict.Pending, false, 1)]
    [InlineData(PathType.Normal, SharedMissVerdict.Pending, true, 0)]
    [InlineData(PathType.Incomplete, SharedMissVerdict.None, true, 1)]
    [InlineData(PathType.Incomplete, SharedMissVerdict.Changed, false, 1)]
    public async Task MissedTiles_DecideWhetherAPartialAnswerStands(PathType type, SharedMissVerdict verdict, bool applied, int resolveCalls)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            var nav = new ManualSharedPathSearch { Type = type, Missed = [(3, 4)], Verdict = verdict };
            using var h = new Harness(world, Options(), nav);
            Assert.True(IsPending(h.Ask(h.Far())));
            h.Runner.CompleteAll();

            PathResult answer = h.Ask(h.Far());
            Assert.Equal(applied, !IsPending(answer));
            Assert.Equal(applied ? 1 : 0, h.Jobs.Totals.Applied);
            Assert.Equal(resolveCalls, nav.ResolveCalls);
            // A tile installed since the submit: the search runs again.
            Assert.Equal(verdict == SharedMissVerdict.Changed ? 2 : 1, h.Runner.Submitted.Count);
            Assert.Equal(0, nav.OwnerCalls);
            return true;
        });
    }

    [Fact]
    public async Task AnEndpointTileStillBeingRead_IsAnOrdinaryPendingAnswer_WithoutAJob()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            var nav = new ManualSharedPathSearch { PrepareResult = false };
            using var h = new Harness(world, Options(), nav);
            Assert.True(IsPending(h.Ask(h.Far())));
            Assert.Empty(h.Runner.Submitted);
            Assert.False(PlayerbotPathJobs.LastPendingWasJob);
            return true;
        });
    }

    // ---- 9. callers that cannot wait stay synchronous ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallersThatCannotWait_SearchOnceOnTheWorldThread_WithOrWithoutJobs(bool jobs)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options(jobs));
            Vector3 far = h.Far();
            var wait = new TileWait();

            // The bool planners: no wrapper, no deferrable scope.
            Assert.True(h.Think(() => PlayerbotNavigation.TryPlan(h.Player, far, h.Options, out _)));
            Assert.Equal(1, h.Nav.OwnerCalls);
            Assert.True(h.Think(() => PlayerbotNavigation.TryPlanUnguarded(h.Player, far, h.Options, out _)));
            Assert.Equal(2, h.Nav.OwnerCalls);
            Assert.True(h.Think(() => PlayerbotNavigation.TryPlanFar(h.Player, far, h.Options, 12_000, out _)));
            Assert.Equal(3, h.Nav.OwnerCalls);

            // A chase and a trial answer this think, even through the waiting wrapper.
            Assert.Equal(PlanOutcome.Route, h.Plan(far, ref wait, 1_000, out _, chase: true));
            Assert.Equal(4, h.Nav.OwnerCalls);
            Assert.Equal(PlanOutcome.Route, h.Plan(far, ref wait, 1_000, out _, trial: true));
            Assert.Equal(5, h.Nav.OwnerCalls);

            // A query within the vmangos search budget (a goal under 100 yards), and one that cannot take a pending answer.
            Assert.Equal(PlanOutcome.Route, h.Plan(h.Far(50), ref wait, 1_000, out _));
            Assert.Equal(6, h.Nav.OwnerCalls);
            PathOptions blocking = Harness.Query(12_000) with { NonBlockingTiles = false };
            h.Think(() =>
            {
                using PlayerbotPathJobs.DeferrableScope scope = PlayerbotPathJobs.Deferrable();
                return PlayerbotThinkProbe.Find(h.Collision, h.Origin, far, blocking);
            });
            Assert.Equal(7, h.Nav.OwnerCalls);
            Assert.Empty(h.Runner.Submitted);
            return true;
        });
    }

    // ---- 10. telemetry ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheProbe_CountsJobsApartFromWorldThreadQueries_AndDescribesThem()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Assert.True(IsPending(h.Ask(h.Far())));
            Assert.Equal(1, h.Probe.JobsSubmitted);
            Assert.Equal(0, h.Probe.QueryTicks);
            Assert.Equal(1, h.Probe.Queries);
            Assert.Contains("off-thread: 1 jobs submitted", h.Probe.Describe(), StringComparison.Ordinal);

            h.Runner.CompleteAll();
            Assert.False(IsPending(h.Ask(h.Far())));
            Assert.Equal(1, h.Probe.JobsApplied);
            Assert.Equal(0, h.Probe.QueryTicks);
            Assert.Equal(0, h.Probe.Largest.Nodes);
            Assert.True(h.Probe.LargestJob.Nodes > 0);
            Assert.Equal(1, h.Probe.Queries); // an applied answer counts against the optional-plan budget
            Assert.Contains("off-thread: 0 jobs submitted, 0 waiting, 1 applied", h.Probe.Describe(), StringComparison.Ordinal);
            return true;
        });
    }

    // ---- 12. the world-thread deadline ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASearchCutAtTheDeadline_IsACappedAnswerWithAnAnomaly_NeverAPendingOne_AndAFarPlanStillRoutes()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            var nav = new ManualSharedPathSearch { OwnerHitsDeadline = true };
            using var h = new Harness(world, Options(jobs: false, syncMs: 100), nav);
            using AnomalyCapture cap = BotAnomalyCapture.For(h.Player);
            var wait = new TileWait();

            // Jobs are off, so asking again would repeat the same search: the capped corridor is the answer, in a waiting plan or not.
            Assert.Equal(PlanOutcome.Route, h.Plan(h.Far(250), ref wait, 1_000, out PlayerbotRoute? route));
            Assert.NotNull(route);
            Assert.Equal(100, nav.OwnerOptions[0].MaxSearchMs);
            Assert.Equal(1, cap.Count(PlayerbotAnomalies.Key("nav", "sync-deadline")));
            Assert.Equal(1, nav.OwnerCalls);
            Assert.False(wait.Active);

            Assert.True(h.Think(() => PlayerbotNavigation.TryPlan(h.Player, h.Far(60), h.Options, out _)));
            return true;
        });
    }

    // ---- 13. fallback queries and moving destinations never wait forever ------------------------------------------------------------

    [Fact]
    public async Task AFallbackQueryFromAnotherStart_LeavesTheMainAnswerAlone_AndBothAreAppliedOnceDone()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            PlayerbotOptions options = Options();
            options.PathJobsMaxPerBot = 2;
            using var h = new Harness(world, options);
            Vector3 far = h.Far();
            Vector3 beside = h.Origin + new Vector3(0, 3, 0);

            Assert.True(IsPending(h.Ask(far)));
            h.Runner.CompleteAll();
            Assert.False(IsPending(h.Ask(far))); // the main answer is applied (and unusable to the caller)
            Assert.True(IsPending(h.Ask(far, from: beside))); // the fallback from beside the bot: a second job
            Assert.Equal(2, h.Runner.Submitted.Count);

            Assert.False(IsPending(h.Ask(far))); // the main entry survived the fallback's query
            h.Runner.CompleteAll();
            Assert.False(IsPending(h.Ask(far, from: beside)));
            Assert.Equal(2, h.Runner.Submitted.Count);
            Assert.Equal(0, h.Jobs.Totals.Stale);
            Assert.Equal(0, h.Jobs.Totals.Escalations);
            return true;
        });
    }

    [Fact]
    public async Task ADestinationThatMovesEveryThink_GetsARouteWithinAFewThinks_NotAfterThePathJobWait()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            int thinks = 0;
            bool answered = false;
            for (int step = 0; step < 40 && !answered; step++)
            {
                thinks++;
                answered = !IsPending(h.Ask(h.Far(250 + (2 * step))));
                h.Runner.CompleteAll();
                h.Wall += 2_000;
                h.Clock += 500;
            }

            Assert.True(answered, "the wait must end");
            Assert.True(thinks <= 3, $"a moving destination needed {thinks} thinks");
            Assert.Equal(0, h.Jobs.Totals.Escalations);
            Assert.Equal(0, h.Nav.OwnerCalls);
            Assert.InRange(h.Runner.Submitted.Count, 1, 2); // the second is the refresh for the destination asked now
            Assert.Equal(1, h.Jobs.Totals.Applied);
            return true;
        });
    }

    [Fact]
    public async Task ADestinationThatMovesEveryThink_WithJobLatency_GetsARouteWithinAFewThinks()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            PathResult? answer = null;
            int thinks = 0;
            for (int step = 0; step < 12 && answer is null; step++)
            {
                thinks++;
                PathResult r = h.Ask(h.Far(120 + (3.5f * step))); // a leader running away at 7 yards per second, 500 ms thinks
                if (!IsPending(r)) answer = r;
                if (step % 3 == 2) h.Runner.CompleteAll(); // the job takes three thinks
                h.Wall += 500;
                h.Clock += 500;
            }

            Assert.NotNull(answer);
            Assert.True(thinks <= 6, $"a moving destination needed {thinks} thinks");
            Assert.Equal(0, h.Jobs.Totals.Escalations);
            Assert.Equal(0, h.Nav.OwnerCalls);
            Assert.True(h.Jobs.Totals.Applied >= 1);
            Assert.True(h.Runner.Submitted.Count >= 2, "a refresh job for the destination asked now");
            Assert.Equal(PathType.Short, answer.Type & PathType.Short);
            return true;
        });
    }

    [Fact]
    public async Task AFinishedAnswerForADestinationFarFromTheAskedOne_IsNeverApplied()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options());
            Assert.True(IsPending(h.Ask(h.Far(250))));
            h.Runner.CompleteAll();
            Assert.True(IsPending(h.Ask(h.Far(300)))); // 50 yards away: beyond the tolerance, a new job
            Assert.Equal(0, h.Jobs.Totals.Applied);
            Assert.Equal(2, h.Runner.Submitted.Count);
            return true;
        });
    }

    [Fact]
    public async Task ADestinationThatMovesEveryThink_ByJumpsBeyondTheTolerance_StillEndsItsWait_WithOneBoundedSynchronousSearch()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var h = new Harness(world, Options(syncMs: 100));
            bool answered = false;
            for (int step = 0; step < 40 && !answered; step++)
            {
                answered = !IsPending(h.Ask(h.Far(250 + (60 * step)))); // each ask is far from the last: always a new job, never a match
                h.Runner.CompleteAll();
                h.Wall += 2_000;
                h.Clock += 500;
            }

            Assert.True(answered, "the wait must end");
            Assert.Equal(1, h.Jobs.Totals.Escalations);
            Assert.Equal(1, h.Nav.OwnerCalls);
            Assert.True(h.Wall - 1_000_000 <= (h.Options.PathJobWaitMaxMs + 4_000));
            return true;
        });
    }

    [Fact]
    public async Task ADefaultConfigFarPlan_IsNotCutByTheDeadline_SoItsOutcomeDoesNotDependOnHostLoad()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            var defaults = new PlayerbotOptions { Enabled = true, MaxPathPoints = 128, MaxRouteYards = 2000 };
            Assert.Equal(0, defaults.SyncPathQueryMaxMs);
            Assert.False(defaults.PathJobs);

            // What a server started from the shipped configuration file gets (the C# default alone proves nothing about it).
            IConfigurationRoot shipped = new ConfigurationBuilder()
                .Add(new ConfConfigurationSource { Path = Path.Combine(RepoRoot.Find(), "src", "ArcaneCore.World", "world.conf.dist") }).Build();
            Assert.Equal(0, PlayerbotOptions.Bind(shipped).SyncPathQueryMaxMs);

            var nav = new ManualSharedPathSearch();
            using var h = new Harness(world, defaults, nav);
            var wait = new TileWait();
            Assert.Equal(PlanOutcome.Route, h.Plan(h.Far(250), ref wait, 1_000, out PlayerbotRoute? route));
            Assert.NotNull(route);
            Assert.Equal(0, nav.OwnerOptions[0].MaxSearchMs);
            Assert.Equal(0, h.Probe.Search.DeadlineHits);
            return true;
        });
    }

    // ---- 14. any order of completion, cancellation, faults and logouts ----------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnyOrderOfCompletionCancellationFaultAndLogout_ThrowsNothing_AndNeverAppliesAfterForget(int seed)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            PlayerbotOptions options = Options(syncMs: 100);
            options.PathJobsMaxInFlight = 6;
            using var h = new Harness(world, options);
            var random = new Random(seed);
            Guid[] bots = [.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid())];
            Vector3[] goals = [h.Far(250), h.Far(300, 30), h.Far(400, -20)];
            for (int step = 0; step < 600; step++)
            {
                switch (random.Next(7))
                {
                    case 0 or 1 or 2:
                        h.Ask(goals[random.Next(goals.Length)], 8192 + (random.Next(3) * 4096), bots[random.Next(bots.Length)]);
                        break;
                    case 3:
                        h.Runner.CompleteNext();
                        break;
                    case 4:
                        h.Runner.CancelNext();
                        break;
                    case 5:
                        if (random.Next(4) == 0) h.Runner.FaultNext();
                        else h.Runner.CompleteAll();
                        break;
                    default:
                        Guid gone = bots[random.Next(bots.Length)];
                        h.Jobs.Forget(gone);
                        long applied = h.Jobs.Totals.Applied;
                        h.Ask(goals[0], 12_000, gone);
                        Assert.Equal(applied, h.Jobs.Totals.Applied);
                        h.Clock += (uint)random.Next(4_000);
                        h.Jobs.Sweep();
                        break;
                }

                Assert.InRange(h.Jobs.InFlight, 0, options.PathJobsMaxInFlight);
            }

            return true;
        });
    }
}
