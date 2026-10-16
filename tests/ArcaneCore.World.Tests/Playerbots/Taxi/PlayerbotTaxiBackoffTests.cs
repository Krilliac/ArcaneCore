using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

/// <summary>
/// The taxi's transit-defect fixes: the backoff is per departure node on the travel schedule, a bot that passes an unknown flight master
/// detours to learn its node, a walk the navigation keeps refusing fails after three refusals, and boarding is bounded when the bot cannot
/// be stopped.
/// </summary>
public sealed partial class PlayerbotTaxiTests
{
    private static int TaxiReports(AnomalyCapture cap) => AllTaxiCodes.Sum(code => cap.Count(TaxiKey(code)));

    [Fact]
    public async Task AFailedDeparture_BacksOffThatNodeOnly_AndAnotherKnownNodeStillFlies()
    {
        await using Rig rig = await Rig.StartAsync(alternateDeparture: true);
        await rig.Host.WaitForWorldAsync(() => rig.Session.Player!.VisibleObjects.Contains(TaxiFixture.AlternateMasterGuid), "the second master is visible");
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2, 4);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.Equal([1u, 2u], PlayerbotTaxi.TripOf(player)!.Nodes);

            // The server refuses the flight from node 1 (the bot is in combat when the request reaches it).
            player.UnitFlags |= UnitFlags.InCombat;
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Failed, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            player.UnitFlags &= ~UnitFlags.InCombat;
            uint now = rig.Session.World.NowMs;
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, now));
            Assert.False(PlayerbotTaxi.IsBackedOff(player, 4, now));
            Assert.False(PlayerbotTaxi.IsBackedOff(player, 2, now));
            Assert.Equal(1, cap.Count(TaxiKey("activate-refused")));

            // Node 4, beside node 1 with a master of its own, still flies the same trip.
            player.Relocate(TaxiFixture.StartX + 40f, player.Y, player.Z, 0, now);
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.Equal([4u, 2u], PlayerbotTaxi.TripOf(player)!.Nodes);
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.True(PlayerbotTaxi.IsFlying(player));
            rig.FlyUntilLanded(player);
            Assert.Equal(TaxiStep.Arrived, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.Equal(920u, player.Money);

            // Nothing else was reported, and node 1 still waits.
            Assert.Equal(1, TaxiReports(cap));
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, rig.Session.World.NowMs));
            return 0;
        });
    }

    [Fact]
    public async Task TheBackoffFollowsTheTravelSchedule_DoublesPerFailureOfTheNode_CapsAtTheMaximum_AndALandingClearsIt()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            PlayerbotOptions options = Options(t => t.BackoffMinutes = 1440);
            uint t0 = 5_000;
            uint[] expected = [60_000, 120_000, 240_000, 300_000, 300_000];
            foreach (uint wait in expected)
            {
                Assert.Equal(wait, PlayerbotTransitBackoff.Fail(player, TransitKind.Taxi, 1, t0, options));
                Assert.True(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 1, t0 + wait - 1));
                Assert.False(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 1, t0 + wait));
                Assert.False(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 2, t0)); // another node never waits
                t0 += wait;
            }

            Assert.Equal(5, PlayerbotTransitBackoff.Failures(player, TransitKind.Taxi, 1));
            PlayerbotTransitBackoff.Clear(player, TransitKind.Taxi, 1);
            Assert.Equal(0, PlayerbotTransitBackoff.Failures(player, TransitKind.Taxi, 1));
            Assert.Equal(60_000u, PlayerbotTransitBackoff.Fail(player, TransitKind.Taxi, 1, t0, options));
            return 0;
        });
    }

    [Fact]
    public async Task TheTaxiBackoffMinutes_CapTheScheduleOfANode()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options(t => t.BackoffMinutes = 2);
            options.Travel.TransitBackoffSeconds = 300;
            options.Travel.TransitBackoffMaxSeconds = 300;
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            player.UnitFlags |= UnitFlags.InCombat;
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Failed, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            uint now = rig.Session.World.NowMs;
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, now + 110_000u));
            Assert.False(PlayerbotTaxi.IsBackedOff(player, 1, now + 130_000u)); // 2 minutes, not the schedule's 5
            return 0;
        });
    }

    [Fact]
    public async Task ThreeRefusedPlansInARow_FailTheWalkToTheMaster_BackTheNodeOff_AndReportOnce()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new NoPathfinder());
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            player.Relocate(player.X - 120f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            return 0;
        });
        await rig.WaitForMasterOutOfSightAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            TaxiTrip trip = PlayerbotTaxi.TripOf(player)!;
            string? failure = null;
            int drives = 0;
            TaxiStep step = TaxiStep.Traveling;
            while (step == TaxiStep.Traveling && drives < 10)
            {
                trip.NextPlanMs = unchecked(rig.Session.World.NowMs - 1); // the retry wait is not what is tested
                step = PlayerbotTaxi.Drive(rig.Session, player, options, 500, out failure);
                drives++;
            }

            Assert.Equal(TaxiStep.Failed, step);
            Assert.Equal(PlayerbotTaxi.MaxRefusedPlans, drives);
            Assert.Contains("refused 3 times", failure);
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, rig.Session.World.NowMs));
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.Equal(1, cap.Count(TaxiKey("master-unreachable")));
            Assert.Equal(1, TaxiReports(cap));
            return 0;
        });
    }

    [Fact]
    public async Task ABotThatCannotBeStopped_AtTheMaster_FailsTheBoardingAfterTheStopBound()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));

            // The bot is moving and every managed action is refused (a quest reward settling): the stop never goes through.
            Assert.True(player.BeginQuestSettlement(Guid.NewGuid()));
            MovementInfo moving = player.Movement;
            moving.Flags |= MovementFlags.Forward;
            player.ApplyMovement(moving, rig.Session.World.NowMs);

            TaxiStep step = TaxiStep.Traveling;
            string? failure = null;
            int drives = 0;
            while (step == TaxiStep.Traveling && drives < PlayerbotTaxi.MaxStopAttempts + 5)
            {
                step = PlayerbotTaxi.Drive(rig.Session, player, options, 500, out failure);
                drives++;
            }

            Assert.Equal(TaxiStep.Failed, step);
            Assert.Equal(PlayerbotTaxi.MaxStopAttempts + 1, drives);
            Assert.Contains("to a stop", failure);
            Assert.Equal(1, cap.Count(TaxiKey("stop-refused")));
            Assert.Equal(1, TaxiReports(cap));
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, rig.Session.World.NowMs));
            return 0;
        });
    }

    [Fact]
    public async Task AFlightMasterPassedWithinTheDetourRadius_WhoseNodeIsUnknown_IsWalkedTo_AndTeachesItsNode_WithoutAFlight()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            player.Money = 1000;
            player.Relocate(player.X - 60f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            return 0;
        });
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            PlayerbotOptions options = Options();
            rig.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.False(Rig.Knows(state, 1));
            Assert.False(PlayerbotTaxi.Discover(rig.Session, player, options)); // out of reach to ask: no request yet, but a detour
            TaxiTrip trip = Assert.IsType<TaxiTrip>(PlayerbotTaxi.TripOf(player));
            Assert.True(trip.LearnOnly);
            Assert.Equal([1u], trip.Nodes);
            Assert.Equal(TaxiStage.ToMaster, trip.Stage);
            Assert.True(PlayerbotTaxi.IsActive(player));
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options)); // never a way to anywhere

            float before = player.X;
            for (int think = 0; think < 3; think++)
            {
                PlayerbotMotion.ElapseForTests(player, 500);
                rig.Session.ManagedBudget = new ManagedActionBudget(4);
                Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            }

            Assert.True(player.X > before, $"no step towards the master from x {before:F1}");
            Assert.False(Rig.Knows(state, 1));

            // At the master: it is asked through the real handler, and nothing flies.
            PlayerbotMovementControl.Stop(rig.Session, player);
            player.Relocate(TaxiFixture.StartX, player.Y, player.Z, 0, rig.Session.World.NowMs);
            rig.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.Equal(TaxiStep.Arrived, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.True(Rig.Knows(state, 1));
            Assert.False(PlayerbotTaxi.IsFlying(player));
            Assert.Equal(1000u, player.Money);
            Assert.Equal(TaxiStage.Done, trip.Stage);
            Assert.False(PlayerbotTaxi.IsActive(player));
            Assert.Equal(0, TaxiReports(cap));

            // Learned: no second detour.
            PlayerbotTaxi.ForceNextScan(player);
            Assert.False(PlayerbotTaxi.Discover(rig.Session, player, options));
            Assert.Equal(TaxiStage.Done, PlayerbotTaxi.TripOf(player)!.Stage);
            return 0;
        });
    }

    [Fact]
    public async Task NoDetour_ToAMasterBeyondTheRadius_OrOfABackedOffNode_OrWithTaxiOff()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            player.Relocate(player.X - 120f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            return 0;
        });
        await rig.WaitForMasterOutOfSightAsync();
        await rig.OnPlayer((player, state) =>
        {
            PlayerbotOptions options = Options();
            PlayerbotTaxi.ForceNextScan(player);
            Assert.False(PlayerbotTaxi.Discover(rig.Session, player, options));
            Assert.Null(PlayerbotTaxi.TripOf(player)); // 122 yards from the master: beyond the radius

            player.Relocate(TaxiFixture.StartX - 60f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            PlayerbotTransitBackoff.Fail(player, TransitKind.Taxi, 1, rig.Session.World.NowMs, options);
            PlayerbotTaxi.Cancel(player);
            PlayerbotTaxi.ForceNextScan(player);
            Assert.False(PlayerbotTaxi.Discover(rig.Session, player, options));
            Assert.Null(PlayerbotTaxi.TripOf(player)); // within the radius, but its node waits out a failure

            PlayerbotTransitBackoff.Clear(player, TransitKind.Taxi, 1);
            PlayerbotOptions off = Options(t => t.Enabled = false);
            PlayerbotTaxi.ForceNextScan(player);
            Assert.False(PlayerbotTaxi.Discover(rig.Session, player, off));
            Assert.Null(PlayerbotTaxi.TripOf(player));
            return 0;
        });
    }
}
