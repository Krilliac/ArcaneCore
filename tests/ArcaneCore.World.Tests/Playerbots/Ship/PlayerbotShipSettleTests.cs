using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Travel;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Ship;

/// <summary>
/// No bot is left aboard a ship without a driver (<see cref="PlayerbotShip"/>, <see cref="ShipTrip.Settling"/>): a leg cancelled or failed
/// while the server still carries the bot stays under way until the bot stepped off at the next port the ship waits at; a fight before the
/// bot is aboard ends the leg and the journey begins it again after the fight; and the journey's bound holds with a leg under way, except
/// aboard (<see cref="PlayerbotTravel"/>). On the synthetic crossing of <see cref="ShipTestWorld"/> (the Booty Bay boat, entry 20808).
/// </summary>
public sealed class PlayerbotShipSettleTests(ITestOutputHelper output)
{
    private const uint Boat = 20808;

    private static string ShipKey(string code) => PlayerbotAnomalies.Key(PlayerbotShip.AnomalyArea, code);

    /// <summary>The boat leg (from port A on map 0) begun, and driven as the brain would until the bot is aboard and the leg rides.</summary>
    private static async Task<(ShipRoute Route, ShipTrip Trip)> BoardAsync(ShipTestWorld world, PlayerbotOptions options)
    {
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        Assert.True(await world.OnWorldAsync(() => PlayerbotShip.TryBegin(world.Session, world.Player, boat, fromA: true, options, out _)));
        for (uint elapsed = 0; elapsed < 300_000; elapsed += 50)
        {
            if (elapsed % 100 == 0)
            {
                bool riding = await world.OnWorldAsync(() =>
                {
                    PlayerbotShip.Drive(world.Session, world.Player, options, 100, out _);
                    return PlayerbotShip.TripOf(world.Player) is { Stage: ShipStage.Ride } && world.Player.Transport?.Entry == Boat;
                });
                if (riding) return (boat, (await world.OnWorldAsync(() => PlayerbotShip.TripOf(world.Player)))!);
            }

            await world.AdvanceAsync(50);
            await world.OnWorldAsync(() => PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs));
        }

        throw new TimeoutException("the bot never boarded the boat");
    }

    /// <summary>Whether the bot is aboard the boat with no ship leg under way (the state none of these paths may leave it in).</summary>
    private static bool Stranded(Player player) => player.Transport?.Entry == Boat && !PlayerbotShip.IsActive(player);

    private async Task TraceAsync(ShipTestWorld world, string what)
        => output.WriteLine(what + ": " + await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            ShipTrip? trip = PlayerbotShip.TripOf(p);
            TravelJourney? journey = PlayerbotTravel.JourneyOf(p);
            return $"at {world.Host.World.NowMs} ms: map {p.MapId} ({p.X:F1},{p.Y:F1},{p.Z:F1}) transport {p.Transport?.Entry}; trip {trip?.Stage} "
                + $"settling {trip?.Settling} cancelled {trip?.Cancelled} ({trip?.FailureCode}: {trip?.Failure}) left at {trip?.LeftAt}; "
                + $"journey {journey?.State} ({journey?.Failure})";
        }));

    [Fact]
    public async Task ALegCancelledAboard_RidesToTheNextPort_AndStepsOffThere_NeverStrandedAboard()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        var options = new PlayerbotOptions();
        (_, ShipTrip trip) = await BoardAsync(world, options);
        await world.OnWorldAsync(() =>
        {
            PlayerbotShip.Cancel(world.Player);
            Assert.True(trip.Cancelled);
            Assert.True(PlayerbotShip.IsActive(world.Player), "a leg cancelled aboard ended at sea");
        });

        bool stranded = false;
        (TravelLegStep step, string? failure) = await world.DriveAsync(options, 600_000, observe: p => stranded |= Stranded(p));
        stranded |= await world.OnWorldAsync(() => Stranded(world.Player));
        await TraceAsync(world, $"{step} ({failure})");
        Assert.False(stranded, "the bot was aboard with no leg under way");
        Assert.True(step is TravelLegStep.Arrived or TravelLegStep.Failed, $"{step}: {failure}");
        await world.OnWorldAsync(() =>
        {
            Assert.Null(world.Player.Transport);
            Assert.True(trip.SteppedOff);
            Assert.NotNull(trip.LeftAt);
            Assert.True(PlayerbotShip.AtPort(world.Player, trip.LeftAt!.Value), "the bot did not step off at a port");
        });
    }

    [Fact]
    public async Task ARideOverdueAboard_IsReportedOnce_AndHoldAboardDrivesItToTheNextPort_WhereTheBotStepsOff()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        var options = new PlayerbotOptions();
        (ShipRoute route, ShipTrip trip) = await BoardAsync(world, options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        // Aboard two round trips and more already: the ride is overdue past its last bound at the next think.
        await world.OnWorldAsync(() => trip.BoardedMs = unchecked(world.Host.World.NowMs - ((2 * PlayerbotShip.PeriodMs(route)) + PlayerbotShip.RideGraceMs + 1_000)));

        // Driven only as ManagedPlayerbotFeature drives a bot another driver took over while aboard (its HoldAboard).
        bool stranded = false, held = true;
        for (uint elapsed = 0; elapsed < 600_000 && held; elapsed += 50)
        {
            if (elapsed % 100 == 0)
                held = await world.OnWorldAsync(() =>
                {
                    bool holds = ManagedPlayerbotFeature.HoldAboard(world.Session, world.Player, options, 100);
                    stranded |= Stranded(world.Player);
                    return holds;
                });
            if (!held) break;
            await world.AdvanceAsync(50);
            await world.OnWorldAsync(() =>
            {
                PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs);
                stranded |= Stranded(world.Player);
            });
        }

        await TraceAsync(world, "after HoldAboard");
        Assert.False(held, "HoldAboard still held the bot after ten minutes");
        Assert.False(stranded, "HoldAboard let go of the bot while it was still aboard");
        await world.OnWorldAsync(() =>
        {
            Assert.Null(world.Player.Transport);
            Assert.True(trip.Settling);
            Assert.True(trip.SteppedOff);
            Assert.True(trip.Stage is ShipStage.Done or ShipStage.Failed, $"{trip.Stage}");
            Assert.True(PlayerbotShip.AtPort(world.Player, trip.LeftAt!.Value), "the bot did not step off at a port");
        });
        Assert.Equal(1, capture.Count(ShipKey("ride-overdue")));
    }

    [Fact]
    public async Task ADisembarkTheServerRefuses_IsReportedOnce_AndTheBotRidesOnAndStepsOffAtTheNextPort_ThenTheLegFails()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        var options = new PlayerbotOptions();
        (_, ShipTrip trip) = await BoardAsync(world, options);
        Player bot = world.Player;
        using AnomalyCapture capture = BotAnomalyCapture.For(bot);
        // The server takes the bot back aboard after its first three steps off (at the arrival port), then lets it go.
        int refused = 0;
        await world.OnWorldAsync(() => world.System.PassengerLeft += (ship, unit) =>
        {
            if (ReferenceEquals(unit, bot) && refused < PlayerbotShip.MaxAttempts && PlayerbotShip.TripOf(bot)?.Stage == ShipStage.Disembark)
            {
                refused++;
                ship.AddPassenger(unit);
            }
        });

        bool stranded = false;
        (TravelLegStep step, string? failure) = await world.DriveAsync(options, 900_000, observe: p => stranded |= Stranded(p));
        stranded |= await world.OnWorldAsync(() => Stranded(bot));
        await TraceAsync(world, $"{step} ({failure})");
        Assert.Equal(PlayerbotShip.MaxAttempts, refused);
        Assert.False(stranded, "the bot was aboard with no leg under way");
        Assert.Equal(TravelLegStep.Failed, step);
        Assert.Contains("did not take the bot off", failure);
        Assert.Contains("stepped off at the departure port", failure);
        await world.OnWorldAsync(() =>
        {
            Assert.Null(bot.Transport);
            Assert.Equal(ShipStage.Failed, trip.Stage);
            Assert.Equal("disembark-refused", trip.FailureCode);
            Assert.Equal(trip.From, trip.LeftAt);
            Assert.True(PlayerbotShip.AtPort(bot, trip.From), "the bot did not step off at the departure port");
        });
        Assert.Equal(1, capture.Count(ShipKey("disembark-refused")));
    }

    /// <summary>Brain thinks every 50 ms tick, the motion pumped after each, until <paramref name="until"/> holds (world thread) or the time is up.</summary>
    private static async Task<bool> ThinkUntilAsync(ShipTestWorld world, PlayerbotBrain brain, uint maxMs, Func<Player, bool> until)
    {
        for (uint elapsed = 0; elapsed < maxMs; elapsed += 50)
        {
            if (await world.OnWorldAsync(() =>
                {
                    brain.Update(50);
                    return until(world.Player);
                }))
                return true;
            await world.AdvanceAsync(50);
            await world.OnWorldAsync(() => PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs));
        }

        return false;
    }

    [Fact]
    public async Task ABotAttackedWhileWaitingForTheShip_DropsTheLeg_TheBrainFights_AndTheJourneyBeginsItAgainAfter()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            Assert.True(await world.OnWorldAsync(() => travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out _)));
            Assert.True(await ThinkUntilAsync(world, brain, 300_000, p => PlayerbotShip.TripOf(p) is { Stage: ShipStage.WaitShip }),
                "the bot never waited at the dock");

            // Attacked at the dock: the leg is dropped at the next think (no report), and the brain, not the leg, owns the bot.
            ShipTrip waiting = (await world.OnWorldAsync(() => PlayerbotShip.TripOf(world.Player)))!;
            (bool active, PlayerbotGoalKind goal, JourneyState? state) = await world.OnWorldAsync(() =>
            {
                world.Player.Map!.Combat.SetInCombatState(world.Player, 60_000);
                brain.Update(100); // a whole think interval: the brain thinks now
                return (PlayerbotShip.IsActive(world.Player), brain.Goal, PlayerbotTravel.JourneyOf(world.Player)?.State);
            });
            await TraceAsync(world, "attacked at the dock");
            Assert.False(active, "the ship leg still held a bot in a fight at the dock");
            Assert.Equal(PlayerbotGoalKind.Combat, goal);
            Assert.Equal(JourneyState.Active, state);
            for (int think = 0; think < 20; think++)
            {
                await world.OnWorldAsync(() => brain.Update(50));
                await world.AdvanceAsync(50);
            }

            Assert.False(await world.OnWorldAsync(() => PlayerbotShip.IsActive(world.Player)), "a ship leg began in the fight");

            // The fight over: the journey begins the leg again and arrives.
            await world.OnWorldAsync(() => MapCombat.ClearInCombat(world.Player));
            JourneyState? end = await TravelJourneyShipTests.RunAsync(world, brain, 600_000);
            await TraceAsync(world, "after the fight");
            Assert.Equal(JourneyState.Arrived, end);
            await world.OnWorldAsync(() =>
            {
                Assert.NotSame(waiting, PlayerbotShip.TripOf(world.Player));
                Assert.Equal(ShipStage.Done, PlayerbotShip.TripOf(world.Player)!.Stage);
                Assert.Equal(0, brain.TransitFailureCount);
            });
            Assert.False(capture.OfSubsystem(PlayerbotAnomalies.Subsystem(PlayerbotShip.AnomalyArea)).Any(), capture.Describe());
            Assert.False(capture.OfSubsystem(PlayerbotAnomalies.Subsystem(PlayerbotTravel.AnomalyArea)).Any(), capture.Describe());
        }
        finally
        {
            brain.Stop();
        }
    }

    [Fact]
    public async Task AJourneyPastItsBound_EndsWithTheShipLegNotYetAboard_ButAboardOnlyOnceTheBotSteppedOff()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            // Waiting at the dock (a leg under way, not aboard) when the bound passes: the journey ends at once and the leg with it.
            Assert.True(await world.OnWorldAsync(() => travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out _)));
            Assert.True(await ThinkUntilAsync(world, brain, 300_000, p => PlayerbotShip.TripOf(p) is { Stage: ShipStage.WaitShip }),
                "the bot never waited at the dock");
            (JourneyState? state, bool legActive) = await world.OnWorldAsync(() =>
            {
                PlayerbotTravel.JourneyOf(world.Player)!.DeadlineMs = world.Host.World.NowMs - 1;
                brain.Update(100); // a whole think interval: the brain thinks now
                return (PlayerbotTravel.JourneyOf(world.Player)?.State, PlayerbotShip.IsActive(world.Player));
            });
            await TraceAsync(world, "past the bound at the dock");
            Assert.Equal(JourneyState.Failed, state);
            Assert.False(legActive, "the ship leg outlived its overdue journey");
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "overdue")));

            // Aboard when the bound passes: the journey waits for the next port, and ends once the bot stepped off.
            Assert.True(await world.OnWorldAsync(() => travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out string? why)));
            Assert.True(await ThinkUntilAsync(world, brain, 600_000, p => p.Transport?.Entry == Boat), "the bot never boarded");
            await world.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)!.DeadlineMs = world.Host.World.NowMs - 1);
            bool endedAboard = false;
            Assert.True(await ThinkUntilAsync(world, brain, 600_000, p =>
            {
                JourneyState? s = PlayerbotTravel.JourneyOf(p)?.State;
                endedAboard |= s != JourneyState.Active && p.Transport is not null;
                return s != JourneyState.Active;
            }), "the journey never ended");
            await TraceAsync(world, "past the bound aboard");
            Assert.False(endedAboard, "the journey ended with the bot still aboard");
            await world.OnWorldAsync(() =>
            {
                Assert.Equal(JourneyState.Failed, PlayerbotTravel.JourneyOf(world.Player)!.State);
                Assert.Null(world.Player.Transport);
                Assert.Equal(1u, world.Player.MapId); // stepped off at the arrival port, not put off at sea
            });
            Assert.Equal(2, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "overdue")));
        }
        finally
        {
            brain.Stop();
        }
    }
}
