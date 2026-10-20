using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

/// <summary>
/// A flight master that was killed is normal play, not a bot-QA anomaly: a bot that reaches its spawn finds nobody, ends the trip as
/// <see cref="TaxiStep.MasterDead"/> (nothing reported, no failure counted) and holds the node off until about the respawn; a master that is
/// truly absent still reports "master-missing".
/// </summary>
public sealed partial class PlayerbotTaxiTests
{
    private static (CreatureMapSystem System, Creature Master) MasterOf(Player player)
    {
        CreatureMapSystem system = player.Map!.FindUpdater<CreatureMapSystem>()!;
        return (system, system.FindCreature(TaxiFixture.MasterGuid)!);
    }

    private static void AssertMasterDeadOutcome(Rig rig, Player player, PlayerbotOptions options, AnomalyCapture cap)
    {
        DriveOnce(rig, player, options, TaxiStep.MasterDead);
        Assert.Empty(cap.OfSubsystem(PlayerbotTaxi.Area));
        AssertNoTaxiAnomaly(cap);
        Assert.Equal(TaxiStage.Failed, PlayerbotTaxi.TripOf(player)!.Stage);
        Assert.Contains("killed", PlayerbotTaxi.TripOf(player)!.Failure, StringComparison.Ordinal);
        Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, rig.Session.World.NowMs));
        Assert.Equal(0, PlayerbotTransitBackoff.Failures(player, TransitKind.Taxi, 1));
    }

    [Fact]
    public async Task AMasterThatWasKilled_IsNotAnAnomaly_TheTripEndsAsMasterDead_AndTheGoalWalks()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            (CreatureMapSystem system, Creature master) = MasterOf(player);
            system.KillCreature(master);
            Assert.NotEqual(CreatureDeathState.Alive, master.DeathState);

            AssertMasterDeadOutcome(rig, player, options, cap);

            // The trip is over and the node waits: the goal walks, and the same think does not start another trip from it.
            DriveOnce(rig, player, options, TaxiStep.Idle);
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.Equal(TaxiStage.Failed, PlayerbotTaxi.TripOf(player)!.Stage);
            AssertNoTaxiAnomaly(cap);
            return 0;
        });
    }

    [Fact]
    public async Task AMasterGoneFromTheMapAndWaitingForItsRespawn_IsNotAnAnomaly_EitherEnd()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            (CreatureMapSystem system, Creature master) = MasterOf(player);

            // The corpse is gone (the creature left the map) and the respawn timer runs: vmangos DEAD.
            system.KillCreature(master);
            master.DeathState = CreatureDeathState.Dead;
            master.RespawnAtMs = system.ClockMs + 30_000;
            player.Map!.RemoveObject(master);
            Assert.InRange(PlayerbotTaxi.MasterRespawnWaitMs(player.Map, master.Spawn!.Guid) ?? -1, 1, 30_000);

            AssertMasterDeadOutcome(rig, player, options, cap);
            return 0;
        });
    }

    [Fact]
    public async Task ManyKilledMasterTrips_AreNeverAFailureInARow_AndNeverGrowTheWait()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            (CreatureMapSystem system, Creature master) = MasterOf(player);
            system.KillCreature(master);
            TaxiTrip trip = PlayerbotTaxi.TripOf(player)!;
            for (int trips = 0; trips < PlayerbotTaxi.RepeatedFailures + 2; trips++)
            {
                trip.Stage = TaxiStage.ToMaster;
                DriveOnce(rig, player, options, TaxiStep.MasterDead);
            }

            Assert.Equal(0, PlayerbotTransitBackoff.Failures(player, TransitKind.Taxi, 1));
            AssertNoTaxiAnomaly(cap);
            Assert.Empty(cap.OfSubsystem(PlayerbotTaxi.Area));
            return 0;
        });
    }

    [Fact]
    public async Task AMasterThatIsAliveButGoneFromTheMap_StillReportsMasterMissing()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            (_, Creature master) = MasterOf(player);
            Assert.Equal(CreatureDeathState.Alive, master.DeathState);
            Assert.Null(PlayerbotTaxi.MasterRespawnWaitMs(player.Map!, master.Spawn!.Guid));
            player.Map!.RemoveObject(master);
            DriveOnce(rig, player, options, TaxiStep.Failed);
            Assert.True(cap.Any(TaxiKey("master-missing")), cap.Describe());
            Assert.Equal(1, PlayerbotTransitBackoff.Failures(player, TransitKind.Taxi, 1));
            return 0;
        });
    }

    [Fact]
    public async Task AfterTheRespawn_TheBotFliesFromThatNodeAgain()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            (CreatureMapSystem system, Creature master) = MasterOf(player);
            system.KillCreature(master);
            master.RespawnAtMs = system.ClockMs; // due now: the hold is the shortest one
            AssertMasterDeadOutcome(rig, player, options, cap);
            system.ForceRespawn(master);
            Assert.Equal(CreatureDeathState.Alive, master.DeathState);
            return 0;
        });

        // The hold ends by time only (MasterDeadMinHoldMs), the failure count was never touched.
        await rig.Host.WaitForWorldAsync(() => !PlayerbotTaxi.IsBackedOff(rig.Session.Player!, 1, rig.Session.World.NowMs), "the hold on the node ends");
        await rig.Host.WaitForWorldAsync(() => rig.Session.Player!.VisibleObjects.Contains(TaxiFixture.MasterGuid), "the respawned master is visible");
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            rig.Session.ManagedBudget = new ArcaneCore.World.Net.ManagedActionBudget(4);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.True(PlayerbotTaxi.IsFlying(player));
            rig.FlyUntilLanded(player);
            DriveOnce(rig, player, options, TaxiStep.Arrived);
            AssertNoTaxiAnomaly(cap);
            return 0;
        });
    }
}
