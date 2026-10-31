using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game;
using ArcaneCore.Protocol;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The cadence of a crowded server: with about a thousand bots a bot thinks every few seconds, so the motion lease must follow the
/// real think gap (<see cref="PlayerbotMotion.NoteThinkGap"/>) or every bot walks 2 s and stands until its next think. Time inside a
/// world-thread call moves through <see cref="PlayerbotMotion.ElapseForTests"/>.
/// </summary>
public sealed class PlayerbotCadenceTests
{
    private static readonly PlayerbotOptions Options = new() { MaxPathPoints = 8, MaxRouteYards = 400 };

    /// <summary>Starts a bot on a 200-yard straight route east after <paramref name="thinks"/> thinks <paramref name="gapMs"/> apart.</summary>
    private static PlayerbotRoute StartWalk(WorldTestHost host, WorldSession session, uint gapMs, int thinks)
    {
        WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
        Player player = session.Player!;
        for (int i = 0; i < thinks; i++) PlayerbotMotion.NoteThinkGap(player, gapMs);
        Assert.True(PlayerbotNavigation.TryPlan(player, new(player.X + 200, player.Y, player.Z), Options, out PlayerbotRoute? route));
        session.ManagedBudget = new ManagedActionBudget(4);
        Assert.True(PlayerbotNavigation.TryAdvance(session, route!, Options, 500, host.World.NowMs));
        Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
        return route!;
    }

    private static bool Walking(WorldSession session, uint elapseMs)
    {
        Player player = session.Player!;
        PlayerbotMotion.ElapseForTests(player, elapseMs);
        PlayerbotMotion.Pump(session, player, session.World.NowMs);
        return player.Movement.HasFlag(MovementFlags.Forward);
    }

    [Fact]
    public async Task AFightsShortCombatLaneGaps_DoNotShortenTheLeaseOfTheWalkAfterIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                for (int i = 0; i < 4; i++) PlayerbotMotion.NoteThinkGap(player, 5000);
                uint before = PlayerbotMotion.ThinkGapMs(player);
                // A fight: twenty combat-lane thinks 400 ms apart leave the main lane's gap as it was.
                for (int i = 0; i < 20; i++) PlayerbotMotion.NoteThinkGap(player, 400, combatLane: true);
                Assert.Equal(before, PlayerbotMotion.ThinkGapMs(player));
                // A starved combat lane (a longer gap) still raises it.
                PlayerbotMotion.NoteThinkGap(player, 9000, combatLane: true);
                Assert.True(PlayerbotMotion.ThinkGapMs(player) > before);
                // Main-lane thinks still lower it.
                for (int i = 0; i < 20; i++) PlayerbotMotion.NoteThinkGap(player, 400);
                Assert.InRange(PlayerbotMotion.ThinkGapMs(player), 1u, 1000u);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ABotThatThinksEveryFiveSeconds_KeepsWalkingPastTheOldTwoSecondLease()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                StartWalk(host, session, 5000, 4);
                Assert.True(PlayerbotMotion.LeaseMsOf(session.Player!) >= 10_000, $"lease {PlayerbotMotion.LeaseMsOf(session.Player!)} ms");
                Assert.True(Walking(session, 2500), "stopped at the old 2 s lease");
                Assert.True(Walking(session, 2500), "stopped before its next think");
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task TheLease_StillStopsABotWhoseRouteIsNotFollowedAgain_AndIsCapped()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            // No measured gap: the base lease (4 s).
            await host.World.InvokeAsync(() =>
            {
                StartWalk(host, session, 0, 0);
                Assert.Equal(4000u, PlayerbotMotion.LeaseMsOf(session.Player!));
                Assert.False(Walking(session, 4500));
                return true;
            });
            await host.World.InvokeAsync(() =>
            {
                // A 5 s gap: a 15 s lease. Not followed again, the bot stops once it ran out.
                StartWalk(host, session, 5000, 4);
                Assert.True(Walking(session, 14_500));
                Assert.False(Walking(session, 1000), "walked on past its lease");
                Assert.False(PlayerbotMotion.IsActive(session.Player!));
                return true;
            });
            // A 60 s gap does not ask for more than the cap.
            await host.World.InvokeAsync(() =>
            {
                StartWalk(host, session, 60_000, 8);
                Assert.Equal(PlayerbotMotion.MaxGapLeaseMs, PlayerbotMotion.LeaseMsOf(session.Player!));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ABotWithNoMeasuredGap_SeedsItsLeaseFromTheSchedulersPass_NotTheFourSecondFloor()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                // A fresh login: GapMs 0 (the first think skips NoteThinkGap) while the main lane needs 8 s for a full pass.
                PlayerbotMotion.NoteSchedulerPass(session.Player!, 8000);
                StartWalk(host, session, 0, 0);
                Assert.Equal(0u, PlayerbotMotion.ThinkGapMs(session.Player!));
                Assert.True(Walking(session, 6000), $"stopped before its next think; lease {PlayerbotMotion.LeaseMsOf(session.Player!)} ms");
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task TheLeaseCap_FollowsTheSchedulersPass_UpToAHardMaximum()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                // A measured gap of 20 s with a 20 s pass: more than the old 15 s cap, 1.5 x the pass.
                PlayerbotMotion.NoteSchedulerPass(session.Player!, 20_000);
                StartWalk(host, session, 20_000, 4);
                Assert.True(PlayerbotMotion.LeaseMsOf(session.Player!) > PlayerbotMotion.MaxGapLeaseMs, $"lease {PlayerbotMotion.LeaseMsOf(session.Player!)} ms");
                Assert.Equal(30_000u, PlayerbotMotion.LeaseMsOf(session.Player!));
                Assert.True(Walking(session, 16_000), "stopped at the old 15 s cap");
                return true;
            });
            await host.World.InvokeAsync(() =>
            {
                PlayerbotMotion.NoteSchedulerPass(session.Player!, 1_000_000);
                StartWalk(host, session, 100_000, 8);
                Assert.Equal(PlayerbotMotion.HardMaxGapLeaseMs, PlayerbotMotion.LeaseMsOf(session.Player!));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ARouteTheBrainGaveUp_StopsTheBot_UnlessAThinkKeptOrStartedOne()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                PlayerbotRoute route = StartWalk(host, session, 5000, 4);
                uint renewals = PlayerbotMotion.RenewalsOf(player);

                // The think kept nothing, started nothing, gave nothing up: the walk goes on.
                Assert.False(PlayerbotMotion.SettleAfterThink(session, player, gaveUp: false, renewals, 5000));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                // Gave the route up but another think renewed a route: kept.
                Assert.True(PlayerbotNavigation.TryAdvance(session, route, Options, 500, session.World.NowMs));
                Assert.False(PlayerbotMotion.SettleAfterThink(session, player, gaveUp: true, renewals, 5000));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                // An unrelated wait for a navigation tile in the same think keeps nothing: the route it gave up is stopped, though the
                // lease (10 s) is far from over.
                renewals = PlayerbotMotion.RenewalsOf(player);
                PathSearchCounters.RecordTilePending();
                Assert.True(PlayerbotMotion.SettleAfterThink(session, player, gaveUp: true, renewals, 5000));
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>The feature's wiring: a brain that gave a route up (<see cref="PlayerbotBrain.TakeRouteAbandoned"/>) or changed its goal stops the bot.</summary>
    [Fact]
    public async Task TheFeaturesSettle_StopsABotWhoseBrainAbandonedItsRoute_OrChangedItsGoal()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var abandon = typeof(PlayerbotBrain).GetMethod("AbandonRoute", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var routeField = typeof(PlayerbotBrain).GetField("_route", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

                // Nothing abandoned, goal as before: the walk goes on.
                PlayerbotRoute route = StartWalk(host, session, 5000, 4);
                routeField.SetValue(brain, route);
                Assert.False(brain.TakeRouteAbandoned());
                ManagedPlayerbotFeature.SettleMotion(session, brain, player, brain.Goal, PlayerbotMotion.RenewalsOf(player), 5000);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));

                // The brain abandons its route and starts none: stopped, and the flag is read once.
                uint renewals = PlayerbotMotion.RenewalsOf(player);
                abandon.Invoke(brain, []);
                ManagedPlayerbotFeature.SettleMotion(session, brain, player, brain.Goal, renewals, 5000);
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
                Assert.False(brain.TakeRouteAbandoned());

                // A goal that is not the one the think began with, no route kept: stopped.
                StartWalk(host, session, 5000, 4);
                ManagedPlayerbotFeature.SettleMotion(session, brain, player, (PlayerbotGoalKind)(((int)brain.Goal) + 1), PlayerbotMotion.RenewalsOf(player), 5000);
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Leaving a mob that is another player's drops the route to it through <c>AbandonRoute</c>: with no other target, the settle stops the bot.</summary>
    [Fact]
    public async Task LeavingSomeoneElsesMob_StopsABotWalkingToIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var leave = typeof(PlayerbotBrain).GetMethod("LeaveSomeoneElses", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var routeField = typeof(PlayerbotBrain).GetField("_route", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var mob = new ArcaneCore.Game.Creatures.Creature(993903, new ArcaneCore.Kernel.WorldData.Creatures.CreatureTemplate
                {
                    Entry = 993903, Name = "tagged mob", CreatureType = 1, MinLevel = 20, MaxLevel = 20,
                    MinLevelHealth = 2000, MaxLevelHealth = 2000,
                }, null, ArcaneCore.Kernel.WorldData.Creatures.CreatureContent.Empty, new Random(993903));

                PlayerbotRoute route = StartWalk(host, session, 5000, 4);
                routeField.SetValue(brain, route);
                uint renewals = PlayerbotMotion.RenewalsOf(player);
                leave.Invoke(brain, [player, mob, host.World.NowMs]);
                Assert.Null(routeField.GetValue(brain));
                ManagedPlayerbotFeature.SettleMotion(session, brain, player, brain.Goal, renewals, 5000);
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward), "kept walking to a mob it left");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>A stun or a fear takes the bot's control: the walk stops at the next pump, whatever the lease says.</summary>
    [Theory]
    [InlineData(UnitFlags.Stunned)]
    [InlineData(UnitFlags.Fleeing)]
    [InlineData(UnitFlags.Confused)]
    public async Task AStunOrAFear_StopsAWalkingBot(UnitFlags flag)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                StartWalk(host, session, 5000, 4);
                Assert.True(Walking(session, 100));
                player.UnitFlags |= flag;
                Assert.False(Walking(session, 100), $"{flag} did not stop the walk");
                Assert.False(PlayerbotMotion.IsActive(player));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>A chained route that cannot be taken leaves the motion on the route it has (<c>keepMotion</c>), instead of stopping the bot mid-leg.</summary>
    [Fact]
    public async Task ARouteSwitchThatIsRefused_KeepsTheWalk_WhenAskedTo()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                PlayerbotRoute current = StartWalk(host, session, 5000, 4);
                var refused = new Vector3(player.X + 300, player.Y + 40, player.Z);
                Assert.True(PlayerbotNavigation.TryPlan(player, refused, Options, out PlayerbotRoute? other));
                PlayerbotMotion.Of(player).Blacklist.Add((refused, session.World.NowMs + 60_000));

                Assert.False(PlayerbotNavigation.TryAdvance(session, other!, Options, 500, session.World.NowMs, keepMotion: true));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                Assert.True(PlayerbotMotion.IsActive(player));
                Assert.True(PlayerbotNavigation.TryAdvance(session, current, Options, 500, session.World.NowMs));

                Assert.False(PlayerbotNavigation.TryAdvance(session, other!, Options, 500, session.World.NowMs));
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward), "without keepMotion the refusal stops the bot");
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>The loop watch counts back-and-forth route ends; a chase's ends follow a moving target and are not counted.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheLoopWatch_CountsAlternatingRouteEnds_ButNotAChase(bool chase, bool loops)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                for (int i = 0; i < 12; i++)
                {
                    float y = player.Y + (i % 2 == 0 ? 20 : -20);
                    // Once the watch gave a place up it is refused (and the plan with it): that is the loop.
                    if (!PlayerbotNavigation.TryPlan(player, new(player.X + 30, y, player.Z), Options, out PlayerbotRoute? route)) break;
                    route!.Chase = chase;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotNavigation.TryAdvance(session, route, Options, 500, session.World.NowMs);
                }

                Assert.Equal(loops, PlayerbotMotion.LoopCount(player) > 0);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public void Then_JoinsTheRestOfARouteToTheNextOne_WithEachLegsProof()
    {
        var a = new PlayerbotRoute([new(0, 0, 0), new(10, 0, 0), new(20, 0, 0), new(30, 0, 0)], 30, new[] { true, false, true });
        a.NextPoint = 2; // walking from (10,0) to (20,0): the first leg is behind
        var b = new PlayerbotRoute([new(30, 0, 0), new(40, 5, 0), new(50, 5, 0)], 21, new[] { false, true });

        PlayerbotRoute joined = a.Then(new(14, 0, 0), b);

        Assert.Equal([new Vector3(14, 0, 0), new(20, 0, 0), new(30, 0, 0), new(40, 5, 0), new(50, 5, 0)], joined.Points);
        Assert.Equal(1, joined.NextPoint);
        Assert.Equal(6f + 10f + Vector3.Distance(new(30, 0, 0), new(40, 5, 0)) + 10f, joined.Distance, 3);
        // Legs ending at points 1..4: a's second leg (false), its third (true), then b's two (false, true).
        Assert.Equal([false, true, false, true], Enumerable.Range(1, 4).Select(joined.LegNavigatedAt).ToArray());
        Assert.False(joined.Navigated);
        Assert.False(joined.LegsNavigated(1, 4));
        Assert.True(joined.LegsNavigated(2, 2));
    }

    /// <summary>A chained leg is refused (the next think plans it as before) for a hazard on it, a wait for a tile, or a refused place.</summary>
    [Fact]
    public async Task TryChain_IsRefused_ForAHazardAWaitingTileOrABlacklistedPlace()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var pathfinder = new PlayerbotTileHoldTests.PendingPathfinder();
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                for (int i = 0; i < 4; i++) PlayerbotMotion.NoteThinkGap(player, 5000);
                Assert.True(PlayerbotNavigation.TryPlan(player, new(player.X + 60, player.Y, player.Z), Options, out PlayerbotRoute? route));
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route!, Options, 500, session.World.NowMs));
                var next = new Vector3(player.X + 100, player.Y, player.Z);

                // A wait for a tile: no route, none made up.
                pathfinder.Pending = true;
                long mark = PlayerbotNavigation.PendingMark;
                Assert.False(PlayerbotNavigation.TryChain(player, route!, next, Options, out PlayerbotRoute? chained));
                Assert.Null(chained);
                Assert.True(PlayerbotNavigation.PendingSince(mark));
                pathfinder.Pending = false;

                // A place the loop watch refuses.
                PlayerbotMotion.Of(player).Blacklist.Add((next, session.World.NowMs + 60_000));
                Assert.False(PlayerbotNavigation.TryChain(player, route!, next, Options, out chained));
                PlayerbotMotion.ForgetLoops(player);

                // A hazard on the leg.
                var risk = new PlayerbotRisk(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = true } }, new PlayerbotCombatSpells(session));
                risk.Hazards.Add(player.MapId, new Vector3(player.X + 85, player.Y, player.Z), 5f, session.World.NowMs, 300, "death");
                PlayerbotNavigation.Guard(player, risk);
                Assert.False(PlayerbotNavigation.TryChain(player, route!, next, Options, out chained));
                PlayerbotNavigation.Guard(player, null);

                // Nothing in the way: chained, and it is one route from here to the new end.
                Assert.True(PlayerbotNavigation.TryChain(player, route!, next, Options, out chained));
                Assert.Equal(next.X, chained!.Points[^1].X, 1);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public void TheCadenceStats_ReportPercentilesAndBudgetShares_ThenStartAgain_WithoutAllocating()
    {
        var stats = new PlayerbotCadenceStats();
        for (int i = 0; i < 95; i++) stats.RecordGap(2_000);
        for (int i = 0; i < 5; i++) stats.RecordGap(6_000);
        stats.RecordGap(9_050);
        stats.EndTick(10, timeLimited: true, actionLimited: false);
        stats.EndTick(30, timeLimited: false, actionLimited: true);
        stats.EndTick(20, timeLimited: false, actionLimited: false);
        stats.EndTick(0, timeLimited: false, actionLimited: false); // thought for nobody: no tick

        Assert.False(stats.TryReport(59_999, 700, out _));
        Assert.True(stats.TryReport(1, 700, out PlayerbotCadenceReport report));
        Assert.Equal(700, report.Bots);
        Assert.Equal(60, report.Thinks);
        Assert.Equal(3, report.Ticks);
        Assert.Equal(2_100u, report.GapP50Ms);
        Assert.Equal(6_100u, report.GapP95Ms);
        Assert.Equal(9_050u, report.GapMaxMs);
        Assert.Equal(20.0, report.ThinksPerTick, 6);
        Assert.Equal(100.0 / 3, report.TimeLimitedPct, 6);
        Assert.Equal(100.0 / 3, report.ActionLimitedPct, 6);

        // The window starts again.
        Assert.True(stats.TryReport(60_000, 5, out PlayerbotCadenceReport empty));
        Assert.Equal((0L, 0, 0u), (empty.Thinks, empty.Ticks, empty.GapMaxMs));

        stats.RecordGap(1); stats.EndTick(1, false, false); // warm
        void Window() { for (int i = 0; i < 10_000; i++) { stats.RecordGap((uint)(i % 20_000)); stats.EndTick(3, i % 2 == 0, i % 3 == 0); } }
        ArcaneCore.TestSupport.AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "10000 cadence RecordGap/EndTick pairs");
    }

    [Fact]
    public async Task TheFeature_LogsTheCadenceLineEveryMinute_WithoutNamingABot()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, logger: log, manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Cadenceone", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new Idle())).Success);

        await host.World.AdvanceClockAsync(30_000);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Playerbot cadence"));
        await host.World.AdvanceClockAsync(31_000);
        string line = Assert.Single(log.Entries, e => e.Message.Contains("Playerbot cadence over the last minute")).Message;
        Assert.Contains("1 bots", line);
        Assert.Contains("think gap p50 ", line); Assert.Contains(" ms, max ", line);
        Assert.Contains("ticks ended on the time budget", line);
        Assert.Contains("on the action budget", line);
        Assert.DoesNotContain("Cadenceone", line);
        Assert.DoesNotContain(id.ToString(), line);
        await feature.ShutdownBeforeWorldStopAsync();
    }

    private sealed class Idle : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) { }
    }

    [Fact]
    public async Task SettleAfterThink_WhenThinkKeepsActiveRouteWithoutCallingFollow_RenewsMotionLeaseSoPumpDoesNotExpireMidroute()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                StartWalk(host, session, 1000, 4);
                Assert.Equal(4000u, PlayerbotMotion.LeaseMsOf(player));

                Assert.True(Walking(session, 3000));
                uint renewals = PlayerbotMotion.RenewalsOf(player);
                PlayerbotMotion.NoteThinkGap(player, 3000);
                Assert.False(PlayerbotMotion.SettleAfterThink(session, player, gaveUp: false, renewals, 3000));

                // Another 3000 ms elapses (6000 ms since StartWalk, past the initial 4000 ms lease, but only 3000 ms since the think that kept the route).
                Assert.True(Walking(session, 3000), "motion lease expired midroute even though a think ran and kept the route 3000 ms ago");
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }
}
