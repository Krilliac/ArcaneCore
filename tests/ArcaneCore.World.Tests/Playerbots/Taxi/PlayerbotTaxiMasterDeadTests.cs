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
    public async Task ACorpseThatIsGoneWhileTheRespawnTimerRuns_IsNotAnAnomaly()
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
            master.RespawnAtMs = system.ClockMs + 1_000; // soon: the hold is the shortest one
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

    [Fact]
    public async Task ARespawnThatIsDueButNotHappening_IsStillMasterMissing()
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
            master.RespawnAtMs = system.ClockMs; // overdue: a link or pool holds the respawn, or the timer is stuck
            Assert.Null(PlayerbotTaxi.MasterRespawnWaitMs(player.Map!, master.Spawn!.Guid));
            DriveOnce(rig, player, options, TaxiStep.Failed);
            Assert.True(cap.Any(TaxiKey("master-missing")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task ASpawnWithNoCreatureAndNoPendingRespawn_IsMasterMissing()
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
            uint spawn = master.Spawn!.Guid;
            system.Despawn(master);
            Assert.Null(system.FindBySpawnGuid(spawn));
            Assert.Null(system.PendingRespawnAt(spawn));
            Assert.Null(PlayerbotTaxi.MasterRespawnWaitMs(player.Map!, spawn));
            DriveOnce(rig, player, options, TaxiStep.Failed);
            Assert.True(cap.Any(TaxiKey("master-missing")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task ASpawnWhoseCreatureLeftTheMapWithARespawnPending_IsMasterDead()
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
            uint spawn = master.Spawn!.Guid;
            system.Despawn(master);

            // The dormant respawn time an unloaded grid keeps (PendingRespawnAt).
            system.SetDormantRespawn(spawn, system.ClockMs + 30_000);
            Assert.Null(system.FindBySpawnGuid(spawn));
            Assert.InRange(PlayerbotTaxi.MasterRespawnWaitMs(player.Map!, spawn) ?? -1, 1, 30_000);
            AssertMasterDeadOutcome(rig, player, options, cap);
            return 0;
        });
    }

    [Fact]
    public async Task TheHoldOnAKilledMastersNode_NeverExceedsBackoffMinutes()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options(t => t.BackoffMinutes = 1);
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            (CreatureMapSystem system, Creature master) = MasterOf(player);
            system.KillCreature(master);
            master.RespawnAtMs = system.ClockMs + 3_600_000; // an hour away
            uint before = rig.Session.World.NowMs;
            DriveOnce(rig, player, options, TaxiStep.MasterDead);
            uint after = rig.Session.World.NowMs;
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, after));
            Assert.False(PlayerbotTaxi.IsBackedOff(player, 1, unchecked(after + 60_001u)));
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, unchecked(before + 59_000u)));
            return 0;
        });
    }

    [Theory]
    [InlineData(0x8000_0005u)]
    [InlineData(0xFFFF_FF00u)]
    [InlineData(0xFFFF_FFFFu)]
    [InlineData(1_000u)]
    public async Task ADeferredHold_IsWritten_AndExpires_AtAnyClock_IncludingAcrossTheWrap(uint now)
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, _) =>
        {
            const uint Wait = 5_000;
            PlayerbotTransitBackoff.Defer(player, TransitKind.Taxi, 9, now, Wait);
            Assert.True(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 9, now));
            Assert.True(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 9, unchecked(now + Wait - 1)));
            Assert.False(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 9, unchecked(now + Wait)));
            Assert.Equal(0, PlayerbotTransitBackoff.Failures(player, TransitKind.Taxi, 9));
            Assert.False(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 10, now)); // another node never waits

            // A later, longer hold extends it; an expired one is replaced by a new one.
            PlayerbotTransitBackoff.Defer(player, TransitKind.Taxi, 9, unchecked(now + Wait + 10), Wait);
            Assert.True(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 9, unchecked(now + Wait + 10)));
            return 0;
        });
    }

    [Fact]
    public async Task ADeferredHold_NeverShortensALongerFailureBackoff_NorCountsAsAFailure()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, _) =>
        {
            PlayerbotOptions options = Options(t => t.BackoffMinutes = 1440);
            uint t0 = 7_000;
            uint wait = PlayerbotTransitBackoff.Fail(player, TransitKind.Taxi, 1, t0, options);
            PlayerbotTransitBackoff.Defer(player, TransitKind.Taxi, 1, t0 + 100, 5_000);
            Assert.True(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 1, t0 + wait - 1));
            Assert.False(PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, 1, t0 + wait));
            Assert.Equal(1, PlayerbotTransitBackoff.Failures(player, TransitKind.Taxi, 1));
            return 0;
        });
    }
}
