using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

/// <summary>
/// H1 outcome checks of the bot taxi (<c>bot-taxi/*</c> in the anomaly ledger): each check is proven to fire on the failing scenario and to
/// stay silent on the matching correct one (a flight that boards, flies and lands where its route ends).
/// </summary>
public sealed partial class PlayerbotTaxiTests
{
    private static string TaxiKey(string code) => PlayerbotAnomalies.Key(PlayerbotTaxi.Area, code);

    private static readonly string[] AllTaxiCodes =
    [
        "master-not-found", "master-missing", "master-unreachable", "activate-refused", "flight-not-started", "session-refused", "budget-starved",
        "node-forgotten", "cannot-pay", "map-changed", "services-unavailable", "repeated-failure", "flight-nonfinite-position", "flight-stalled",
        "flag-without-flight", "flight-overdue", "flight-state-residue", "landed-nonfinite", "landed-wrong-map", "landed-under-map", "landed-off-node",
        "stop-refused", "node-not-learned",
    ];

    private static void AssertNoTaxiAnomaly(AnomalyCapture cap)
    {
        foreach (string code in AllTaxiCodes) Assert.False(cap.Any(TaxiKey(code)), $"{code} reported: {cap.Describe()}");
    }

    /// <summary>Plan the 1 to 2 hop and board it; the bot is in the air afterwards.</summary>
    private static TaxiTrip BoardFlight(Rig rig, Player player, PlayerbotOptions options)
    {
        Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
        rig.Session.ManagedBudget = new ManagedActionBudget(4);
        Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
        Assert.Null(failure);
        Assert.True(PlayerbotTaxi.IsFlying(player));
        return PlayerbotTaxi.TripOf(player)!;
    }

    private static void DriveOnce(Rig rig, Player player, PlayerbotOptions options, TaxiStep expected)
    {
        rig.Session.ManagedBudget = new ManagedActionBudget(4);
        Assert.Equal(expected, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
    }

    [Fact]
    public async Task ACompleteFlight_ReportsNothing()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            TaxiTrip trip = BoardFlight(rig, player, options);
            var flights = rig.Host.WorldServices.GetRequiredService<NpcServicesFeature>().Flights!;

            // Two looks in the air with the bot moving on both: not stalled, not overdue, flag backed by a flight.
            for (int look = 0; look < 2; look++)
            {
                flights.Update(player.Map!, 2000);
                trip.NextWatchMs = unchecked(rig.Session.World.NowMs - 1);
                trip.SampleMs = unchecked(rig.Session.World.NowMs - PlayerbotTaxi.WatchIntervalMs);
                DriveOnce(rig, player, options, TaxiStep.Traveling);
            }

            rig.FlyUntilLanded(player);
            DriveOnce(rig, player, options, TaxiStep.Arrived);
            AssertNoTaxiAnomaly(cap);
            return 0;
        });
    }

    [Fact]
    public async Task AKnownRouteWhoseMasterCannotBeApproached_IsReportedOncePerNode()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();

            // The bot's faction is unknown to the catalog: no flight master spawn is one it may approach.
            player.FactionTemplate = 99_999;
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.True(cap.Any(TaxiKey("master-not-found")), cap.Describe());
            Assert.Equal(1, cap.Count(TaxiKey("master-not-found")));
            return 0;
        });
    }

    [Fact]
    public async Task ARouteWithAMasterToApproach_IsNotReportedMasterNotFound()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), Options()));
            Assert.False(cap.Any(TaxiKey("master-not-found")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task AMasterGoneFromItsSpawn_IsReported()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            player.Map!.RemoveObject(player.Map.FindObject(TaxiFixture.MasterGuid)!);
            DriveOnce(rig, player, options, TaxiStep.Failed);
            Assert.True(cap.Any(TaxiKey("master-missing")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task AWalkThatFindsNoWay_IsReported()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            ArcaneCore.Game.Maps.Collision.WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new NoPathfinder());
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
            DriveOnce(rig, player, options, TaxiStep.Traveling);
            Assert.False(cap.Any(TaxiKey("master-unreachable")), cap.Describe());
            PlayerbotTaxi.TripOf(player)!.BestMs = unchecked(rig.Session.World.NowMs - PlayerbotTaxi.NoProgressMs - 1_000);
            DriveOnce(rig, player, options, TaxiStep.Failed);
            Assert.True(cap.Any(TaxiKey("master-unreachable")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task AServerRefusal_IsReported_AsAnInfoWhenTheBotWasBusy()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            player.UnitFlags |= UnitFlags.InCombat;
            DriveOnce(rig, player, options, TaxiStep.Failed);
            Assert.True(cap.Any(TaxiKey("activate-refused")), cap.Describe());
            Assert.False(cap.Any(TaxiKey("flight-not-started")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task AFareTheBotCanNoLongerPay_IsReportedAsInfo_NotAsAServerRefusal()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            player.Money = 10;
            DriveOnce(rig, player, options, TaxiStep.Failed);
            Assert.True(cap.Any(TaxiKey("cannot-pay")), cap.Describe());
            Assert.False(cap.Any(TaxiKey("activate-refused")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task AnActionBudgetThatNeverAllowsTheRequest_IsReported_OnlyAfterTheAttemptsRunOut()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            for (int attempt = 0; attempt < PlayerbotTaxi.MaxActivateAttempts; attempt++)
            {
                rig.Session.ManagedBudget = new ManagedActionBudget(0);
                Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            }

            Assert.False(cap.Any(TaxiKey("budget-starved")), cap.Describe());
            rig.Session.ManagedBudget = new ManagedActionBudget(0);
            Assert.Equal(TaxiStep.Failed, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.True(cap.Any(TaxiKey("budget-starved")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task ThreeFailedTripsInARow_AreARepeatedFailure_TwoAreNot()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            player.Map!.RemoveObject(player.Map.FindObject(TaxiFixture.MasterGuid)!);
            TaxiTrip trip = PlayerbotTaxi.TripOf(player)!;
            for (int failure = 1; failure <= PlayerbotTaxi.RepeatedFailures; failure++)
            {
                trip.Stage = TaxiStage.ToMaster;
                DriveOnce(rig, player, options, TaxiStep.Failed);
                Assert.True((failure == PlayerbotTaxi.RepeatedFailures) == cap.Any(TaxiKey("repeated-failure")), cap.Describe());
            }

            return 0;
        });
    }

    [Fact]
    public async Task ALandingBetweenFailures_StartsTheCountAgain()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            TaxiTrip trip = PlayerbotTaxi.TripOf(player)!;
            var master = player.Map!.FindObject(TaxiFixture.MasterGuid)!;
            player.Map.RemoveObject(master);
            for (int failure = 0; failure < 2; failure++)
            {
                trip.Stage = TaxiStage.ToMaster;
                DriveOnce(rig, player, options, TaxiStep.Failed);
            }

            // A trip that flew and landed (the stage is forced to flying with no flight: the bot is already down).
            trip.Stage = TaxiStage.Flying;
            DriveOnce(rig, player, options, TaxiStep.Arrived);
            trip.Stage = TaxiStage.ToMaster;
            DriveOnce(rig, player, options, TaxiStep.Failed);
            Assert.False(cap.Any(TaxiKey("repeated-failure")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task AFlightThatHasNotMoved_IsStalled_AFlightThatMoved_IsNot()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            TaxiTrip trip = BoardFlight(rig, player, options);
            var flights = rig.Host.WorldServices.GetRequiredService<NpcServicesFeature>().Flights!;
            uint now = rig.Session.World.NowMs;

            // The bot flew since the last look.
            flights.Update(player.Map!, 2000);
            trip.NextWatchMs = unchecked(now - 1);
            trip.SampleMs = unchecked(now - PlayerbotTaxi.WatchIntervalMs);
            DriveOnce(rig, player, options, TaxiStep.Traveling);
            Assert.False(cap.Any(TaxiKey("flight-stalled")), cap.Describe());

            // The server did not advance the flight at all between two looks.
            trip.NextWatchMs = unchecked(now - 1);
            trip.SampleMs = unchecked(now - PlayerbotTaxi.WatchIntervalMs);
            DriveOnce(rig, player, options, TaxiStep.Traveling);
            Assert.True(cap.Any(TaxiKey("flight-stalled")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task AFlightPastItsBudget_IsOverdue_OnceInside_ItIsNot()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            TaxiTrip trip = BoardFlight(rig, player, options);
            var flights = rig.Host.WorldServices.GetRequiredService<NpcServicesFeature>().Flights!;
            uint now = rig.Session.World.NowMs;
            Assert.True(trip.FlightBudgetMs >= PlayerbotTaxi.FlightGraceMs);

            flights.Update(player.Map!, 1000);
            trip.NextWatchMs = unchecked(now - 1);
            DriveOnce(rig, player, options, TaxiStep.Traveling);
            Assert.False(cap.Any(TaxiKey("flight-overdue")), cap.Describe());

            flights.Update(player.Map!, 1000);
            trip.FlightStartMs = unchecked(now - trip.FlightBudgetMs - 1);
            trip.NextWatchMs = unchecked(now - 1);
            DriveOnce(rig, player, options, TaxiStep.Traveling);
            Assert.True(cap.Any(TaxiKey("flight-overdue")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task ATaxiFlagTheFlightSystemDoesNotHold_IsReported()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            TaxiTrip trip = PlayerbotTaxi.TripOf(player)!;

            // A flag with no flight behind it: the bot would hang in the air for ever.
            trip.Stage = TaxiStage.Flying;
            trip.FlightBudgetMs = uint.MaxValue / 2;
            trip.FlightStartMs = rig.Session.World.NowMs;
            trip.NextWatchMs = unchecked(rig.Session.World.NowMs - 1);
            player.UnitFlags |= UnitFlags.TaxiFlight;
            DriveOnce(rig, player, options, TaxiStep.Traveling);
            Assert.True(cap.Any(TaxiKey("flag-without-flight")), cap.Describe());
            Assert.Equal(1, cap.Count(TaxiKey("flag-without-flight")));
            trip.NextWatchMs = unchecked(rig.Session.World.NowMs - 1);
            DriveOnce(rig, player, options, TaxiStep.Traveling);
            Assert.Equal(1, cap.Count(TaxiKey("flag-without-flight")));
            player.UnitFlags &= ~UnitFlags.TaxiFlight;
            return 0;
        });
    }

    [Fact]
    public async Task ALandingFarFromTheLastNode_IsReported()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            BoardFlight(rig, player, options);
            rig.FlyUntilLanded(player);
            player.Relocate(player.X - 200f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            DriveOnce(rig, player, options, TaxiStep.Arrived);
            Assert.True(cap.Any(TaxiKey("landed-off-node")), cap.Describe());
            Assert.False(cap.Any(TaxiKey("landed-under-map")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task ALandingUnderTheMap_IsReportedAsAnError()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            BoardFlight(rig, player, options);
            rig.FlyUntilLanded(player);
            player.Relocate(player.X, player.Y, player.Z - 400f, 0, rig.Session.World.NowMs);
            DriveOnce(rig, player, options, TaxiStep.Arrived);
            Assert.True(cap.Any(TaxiKey("landed-under-map")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task ALandingAfterADeathInFlight_IsNotJudgedByItsPlace()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            TaxiTrip trip = BoardFlight(rig, player, options);
            rig.FlyUntilLanded(player);
            trip.Interrupted = true;
            player.Relocate(player.X - 200f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            DriveOnce(rig, player, options, TaxiStep.Arrived);
            AssertNoTaxiAnomaly(cap);
            return 0;
        });
    }

    [Fact]
    public async Task ALandingWithTheClientControlStillRemoved_IsReportedAsResidue()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            BoardFlight(rig, player, options);
            rig.FlyUntilLanded(player);
            player.UnitFlags |= UnitFlags.RemoveClientControl;
            DriveOnce(rig, player, options, TaxiStep.Arrived);
            Assert.True(cap.Any(TaxiKey("flight-state-residue")), cap.Describe());
            player.UnitFlags &= ~UnitFlags.RemoveClientControl;
            return 0;
        });
    }

    [Fact]
    public async Task ALandingOnAnotherMap_IsReported()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            TaxiTrip trip = BoardFlight(rig, player, options);
            rig.FlyUntilLanded(player);

            // The trip began on another map than the one the bot lands on.
            trip.MapId = 1u;
            DriveOnce(rig, player, options, TaxiStep.Arrived);
            Assert.True(cap.Any(TaxiKey("landed-wrong-map")), cap.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task DepartureMaster_UnreachableOnFoot_IsSkippedWithoutMasterUnreachableOrMasterNotFound()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();

            var disconnectedMesh = new DisconnectedFromMasterMesh();
            ArcaneCore.Game.Maps.Collision.WorldCollision.Of(rig.World).Install(
                lineOfSight: new FlatFloor(),
                pathfinder: disconnectedMesh);

            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            AssertNoTaxiAnomaly(cap);
            return 0;
        });
    }

    private sealed class DisconnectedFromMasterMesh : ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public bool Enabled => true;

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
            => ArcaneCore.Game.Maps.Collision.PathResult.None(start);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
            => ArcaneCore.Game.Maps.Collision.MeshRaycastResult.Unavailable(end);
    }
}
