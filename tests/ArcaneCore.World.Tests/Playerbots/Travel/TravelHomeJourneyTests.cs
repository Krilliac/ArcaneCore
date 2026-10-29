using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using ArcaneCore.World.Tests.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The home return by authentic travel (PlayerbotBrain.TryTravelHome, docs/areas/playbots.md, Travel): a homed bot whose home lies across the
/// sea (another map, or Teldrassil's water on map 1) begins a journey through the travel seam (<see cref="IPlayerbotTravel"/>, registered as
/// production registers it) instead of the home teleport, and its brain drives it home by ship (<see cref="ShipTestWorld"/>: the Booty Bay
/// boat from map 0 to map 1). In <see cref="PlayerbotHomeReturnMode.Walk"/> no journey begins. When none can begin the return rules decide
/// as before (the teleport), and the next journey is tried only after the transit backoff, doubling while it keeps failing.
/// </summary>
public sealed class TravelHomeJourneyTests(ITestOutputHelper output)
{
    /// <summary>A home on Teldrassil's mainland side of the water (Darkshore, map 1) and a bot standing on Teldrassil.</summary>
    private static readonly Vector3 DarkshoreHome = new(6450f, 800f, TransportWorldContent.StartZ);
    private static readonly Vector3 OnTeldrassil = new(9000f, 900f, TransportWorldContent.StartZ);

    private static PlayerbotOptions Options(PlayerbotHomeReturnMode mode)
    {
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        options.Home.ReturnMode = mode;
        return options;
    }

    /// <summary>A travel seam that records every journey asked of it and begins one or refuses, as told (world thread only).</summary>
    private sealed class ScriptedTravel(bool begins) : IPlayerbotTravel
    {
        public List<(uint Map, Vector3 To, uint AtMs)> Begun { get; } = [];

        private bool _traveling;

        public bool CanReach(WorldSession session, Player player, uint toMap, Vector3 to) => begins;

        public bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why)
        {
            Begun.Add((toMap, to, session.World.NowMs));
            why = begins ? null : "no route (scripted)";
            _traveling |= begins;
            return begins;
        }

        public bool IsTraveling(Player player) => _traveling;
    }

    /// <summary>A travel seam whose attempts succeed without beginning a journey (the bot already stands at the goal): nothing is ever travelling.</summary>
    private sealed class BeginsNothingTravel : IPlayerbotTravel
    {
        public int Attempts { get; private set; }

        public bool CanReach(WorldSession session, Player player, uint toMap, Vector3 to) => true;

        public bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why)
        {
            Attempts++;
            why = null;
            return true;
        }

        public bool IsTraveling(Player player) => false;
    }

    private static Task ThinkAsync(ShipTestWorld world, PlayerbotBrain brain) => world.OnWorldAsync(() => brain.Update(100));

    private async Task TraceAsync(ShipTestWorld world, PlayerbotBrain brain)
        => output.WriteLine(await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            TravelJourney? j = PlayerbotTravel.JourneyOf(p);
            return $"at {world.Host.World.NowMs} ms: map {p.MapId} ({p.X:F1},{p.Y:F1}); journey {j?.State} leg {j?.LegIndex}/{j?.Legs.Count} "
                + $"failure {j?.Failure}; home {brain.HomeReturn.State} teleports {brain.HomeReturn.Teleports} judged {brain.HomeReturn.LastJudgement}";
        }));

    /// <summary>
    /// Auto and Teleport: the home on map 1 is not teleported to; the bot's first think begins the journey (walk, the boat, walk), and brain
    /// updates alone take it to the dock, aboard, across and to its home's centre, inside its home with no teleport and no travel anomaly.
    /// </summary>
    [Theory]
    [InlineData(PlayerbotHomeReturnMode.Auto)]
    [InlineData(PlayerbotHomeReturnMode.Teleport)]
    public async Task AHomeOnTheOtherMap_IsReachedByShip_NotByTheHomeTeleport(PlayerbotHomeReturnMode mode)
    {
        PlayerbotOptions options = Options(mode);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(configureServices: services =>
            services.AddSingleton<IPlayerbotTravel>(new PlayerbotTravel(options)));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var home = new PlayerbotHomeArea(1, TravelJourneyShipTests.Goal, 30f, "Porthome");
        var brain = new PlayerbotBrain(world.Session, options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            await world.OnWorldAsync(() => brain.Home = home);
            await ThinkAsync(world, brain);
            await world.OnWorldAsync(() =>
            {
                TravelJourney journey = PlayerbotTravel.JourneyOf(world.Player) ?? throw new Xunit.Sdk.XunitException("no journey home began");
                Assert.Equal(JourneyState.Active, journey.State);
                Assert.Equal(new TravelPlace(1, home.Centre), journey.Goal);
                Assert.Equal([TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], journey.Planned.Legs.Select(l => l.Means));
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(0, brain.HomeReturn.Teleports);
                Assert.False(world.Teleports.IsBeingTeleported(world.Player));
                Assert.Equal(0u, world.Player.MapId);
            });

            bool aboard = false;
            JourneyState? state = await TravelJourneyShipTests.RunAsync(world, brain, 600_000, p => aboard |= p.Transport?.Entry == 20808);
            await TraceAsync(world, brain);
            Assert.Equal(JourneyState.Arrived, state);
            Assert.True(aboard, "the bot was never aboard the boat");
            await ThinkAsync(world, brain);
            await world.OnWorldAsync(() =>
            {
                Assert.Equal(1u, world.Player.MapId);
                Assert.True(home.DistanceTo(world.Player.X, world.Player.Y) <= PlayerbotTravel.ArriveYards,
                    $"the bot stands at {world.Player.X},{world.Player.Y}");
                Assert.Equal(PlayerbotHomeState.Inside, brain.HomeReturn.State);
                Assert.Equal(0, brain.HomeReturn.Teleports);
                Assert.Equal(0, brain.TransitFailureCount);
            });
            foreach (string area in new[] { PlayerbotTravel.AnomalyArea, PlayerbotShip.AnomalyArea })
                Assert.False(capture.OfSubsystem(PlayerbotAnomalies.Subsystem(area)).Any(), $"{area} anomaly: {capture.Describe()}");
        }
        finally
        {
            brain.Stop();
        }
    }

    /// <summary>Walk: a bot on another map than its home is left off-map, as before: no journey, no teleport.</summary>
    [Fact]
    public async Task InWalkMode_AHomeOnTheOtherMap_BeginsNoJourney()
    {
        PlayerbotOptions options = Options(PlayerbotHomeReturnMode.Walk);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(configureServices: services =>
            services.AddSingleton<IPlayerbotTravel>(new PlayerbotTravel(options)));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var brain = new PlayerbotBrain(world.Session, options);
        try
        {
            await world.OnWorldAsync(() => brain.Home = new PlayerbotHomeArea(1, TravelJourneyShipTests.Goal, 30f, "Porthome"));
            for (int i = 0; i < 5; i++)
            {
                await ThinkAsync(world, brain);
                await world.AdvanceAsync(100);
            }

            await world.OnWorldAsync(() =>
            {
                Assert.Null(PlayerbotTravel.JourneyOf(world.Player));
                Assert.Equal(PlayerbotHomeState.OffMap, brain.HomeReturn.State);
                Assert.Equal(0, brain.HomeReturn.Teleports);
                Assert.Equal(0u, world.Player.MapId);
            });
        }
        finally
        {
            brain.Stop();
        }
    }

    /// <summary>
    /// One map, Teldrassil's water between the bot and its home: Auto and Teleport begin a journey to the home's centre (and do not teleport);
    /// Walk asks for none and walks, as before.
    /// </summary>
    [Theory]
    [InlineData(PlayerbotHomeReturnMode.Auto, true)]
    [InlineData(PlayerbotHomeReturnMode.Teleport, true)]
    [InlineData(PlayerbotHomeReturnMode.Walk, false)]
    public async Task AHomeAcrossTeldrassilsWater_BeginsAJourney_ExceptInWalkMode(PlayerbotHomeReturnMode mode, bool journeys)
    {
        PlayerbotOptions options = Options(mode);
        var travel = new ScriptedTravel(begins: true);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(configureServices: services => services.AddSingleton<IPlayerbotTravel>(travel));
        await world.PlaceAsync(1, OnTeldrassil);
        var home = new PlayerbotHomeArea(1, DarkshoreHome, 100f, "Darkshore");
        Assert.True(ArcaneCore.World.Playerbots.Population.PopulationTownProgression.SeparatedByWater(1, OnTeldrassil.X, OnTeldrassil.Y, 1, DarkshoreHome.X, DarkshoreHome.Y));
        var brain = new PlayerbotBrain(world.Session, options);
        try
        {
            await world.OnWorldAsync(() => brain.Home = home);
            await ThinkAsync(world, brain);
            await world.OnWorldAsync(() =>
            {
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(0, brain.HomeReturn.Teleports);
                Assert.False(world.Teleports.IsBeingTeleported(world.Player));
                if (journeys)
                {
                    (uint map, Vector3 to, _) = Assert.Single(travel.Begun);
                    Assert.Equal(1u, map);
                    Assert.Equal(home.Centre, to);
                }
                else
                {
                    Assert.Empty(travel.Begun);
                    Assert.Equal(PlayerbotHomeTeleportReason.None, brain.HomeReturn.LastJudgement);
                }
            });

            // The journey under way holds the return: the next thinks ask for no other journey and still do not teleport.
            for (int i = 0; i < 3; i++)
            {
                await world.AdvanceAsync(100);
                await ThinkAsync(world, brain);
            }

            await world.OnWorldAsync(() =>
            {
                Assert.Equal(journeys ? 1 : 0, travel.Begun.Count);
                Assert.Equal(0, brain.HomeReturn.Teleports);
            });
        }
        finally
        {
            brain.Stop();
        }
    }

    /// <summary>
    /// An attempt that succeeds without a journey under way (the bot already stands at the goal) waits the transit backoff like a refused
    /// one: each attempt builds the travel facts, which read navmesh tiles on the world thread, so five thinks in a row must not make five.
    /// </summary>
    [Fact]
    public async Task AnAttemptThatBeginsNoJourney_IsNotRepeatedEveryThink()
    {
        PlayerbotOptions options = Options(PlayerbotHomeReturnMode.Auto);
        options.Travel.TransitBackoffSeconds = 60;
        var travel = new BeginsNothingTravel();
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(configureServices: services => services.AddSingleton<IPlayerbotTravel>(travel));
        await world.PlaceAsync(1, OnTeldrassil);
        var brain = new PlayerbotBrain(world.Session, options);
        try
        {
            await world.OnWorldAsync(() => brain.Home = new PlayerbotHomeArea(1, DarkshoreHome, 100f, "Darkshore"));
            for (int i = 0; i < 5; i++)
            {
                await ThinkAsync(world, brain);
                await world.AdvanceAsync(100);
            }

            Assert.Equal(1, travel.Attempts);
        }
        finally
        {
            brain.Stop();
        }
    }

    /// <summary>
    /// No journey can begin (the seam refuses): the return rules answer as before, the home teleport (another map) takes the bot home. The
    /// bot put back on map 0 is not asked to journey again until the transit backoff (10 s) has passed, and the next wait doubles (20 s).
    /// </summary>
    [Fact]
    public async Task WhenNoJourneyCanBegin_TheTeleportTakesTheBotHome_AndTheJourneyIsRetriedOnlyAfterTheBackoff()
    {
        PlayerbotOptions options = Options(PlayerbotHomeReturnMode.Auto);
        options.Travel.TransitBackoffSeconds = 10;
        options.Travel.TransitBackoffMaxSeconds = 300;
        Assert.Equal(10_000u, options.Travel.BackoffMs(1));
        Assert.Equal(20_000u, options.Travel.BackoffMs(2));
        var travel = new ScriptedTravel(begins: false);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(configureServices: services => services.AddSingleton<IPlayerbotTravel>(travel));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var home = new PlayerbotHomeArea(1, TravelJourneyShipTests.Goal, 30f, "Porthome");
        var brain = new PlayerbotBrain(world.Session, options);
        try
        {
            await world.OnWorldAsync(() => brain.Home = home);
            await ThinkAsync(world, brain);
            uint firstAt = await world.OnWorldAsync(() =>
            {
                (uint map, Vector3 to, uint at) = Assert.Single(travel.Begun);
                Assert.Equal(1u, map);
                Assert.Equal(home.Centre, to);
                Assert.Equal(1, brain.HomeReturn.Teleports);
                Assert.Equal(PlayerbotHomeTeleportReason.OtherMap, brain.HomeReturn.LastTeleport);
                return at;
            });

            // The far teleport is acknowledged as a client's; the bot stands at its home on map 1.
            bool home1 = false;
            for (int i = 0; i < 200 && !home1; i++)
            {
                home1 = await world.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(world.Session, world.Player);
                    return world.Player.IsInWorld && world.Player.MapId == 1 && world.Teleports.StageOf(world.Player) is null;
                });
                if (!home1) await world.AdvanceAsync(50);
            }

            Assert.True(home1, "the home teleport did not take the bot to map 1");
            await world.OnWorldAsync(() => Assert.True(home.DistanceTo(world.Player.X, world.Player.Y) < 1f));

            // Back on map 0 inside the backoff: no journey asked (and the home teleport is in its cooldown).
            await world.PlaceAsync(0, ShipTestWorld.Start);
            uint now = await world.OnWorldAsync(() => world.Host.World.NowMs);
            Assert.True(now - firstAt < 10_000u, $"the test spent {now - firstAt} ms before the second think");
            await ThinkAsync(world, brain);
            await world.OnWorldAsync(() =>
            {
                Assert.Single(travel.Begun);
                Assert.Equal(1, brain.HomeReturn.Teleports);
                Assert.Equal(0u, world.Player.MapId);
            });

            // The backoff passed: asked again; it fails again, and the next wait is twice as long.
            await world.AdvanceAsync(firstAt + 10_000u - await world.OnWorldAsync(() => world.Host.World.NowMs));
            await ThinkAsync(world, brain);
            uint secondAt = await world.OnWorldAsync(() =>
            {
                Assert.Equal(2, travel.Begun.Count);
                return travel.Begun[1].AtMs;
            });
            await world.AdvanceAsync(10_000);
            await ThinkAsync(world, brain);
            await world.OnWorldAsync(() => Assert.Equal(2, travel.Begun.Count));
            await world.AdvanceAsync(secondAt + 20_000u - await world.OnWorldAsync(() => world.Host.World.NowMs));
            await ThinkAsync(world, brain);
            await world.OnWorldAsync(() => Assert.Equal(3, travel.Begun.Count));
            await TraceAsync(world, brain);
        }
        finally
        {
            brain.Stop();
        }
    }

    /// <summary>The production travel seam, recording the journeys the brain begins through it (world thread only).</summary>
    private sealed class RecordingTravel(PlayerbotTravel inner) : IPlayerbotTravel
    {
        public PlayerbotTravel Inner => inner;

        public List<(TravelJourney? Journey, uint AtMs)> Begun { get; } = [];

        public bool CanReach(WorldSession session, Player player, uint toMap, Vector3 to) => inner.CanReach(session, player, toMap, to);

        public bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why)
        {
            bool begun = inner.TryBegin(session, player, toMap, to, out why);
            Begun.Add((begun ? PlayerbotTravel.JourneyOf(player) : null, session.World.NowMs));
            return begun;
        }

        public bool IsTraveling(Player player) => inner.IsTraveling(player);
    }

    /// <summary>
    /// Journeys home that BEGIN and later fail count towards the doubling backoff (10 s after the first, 20 s after the second: a begun
    /// journey does not clear the count), journeys another caller began (as <c>.playerbot travel</c> does, through the same service) neither
    /// count nor clear it, whether begun in the wait or over a journey home under way, and only a journey home that arrived starts the count
    /// again (the next failure waits 10 s). The production seam (<see cref="PlayerbotTravel"/>) plans the real walk, boat and walk to map 1;
    /// each journey is failed by its own bound (<see cref="TravelJourney.DeadlineMs"/>, <c>overdue</c>) and the arrival is set on the journey.
    /// </summary>
    [Fact]
    public async Task JourneysHomeThatBeganAndFailed_DoubleTheBackoff_OnlyAnArrivalClearsIt_AndOthersJourneysDoNotCount()
    {
        PlayerbotOptions options = Options(PlayerbotHomeReturnMode.Auto);
        options.Travel.TransitBackoffSeconds = 10;
        options.Travel.TransitBackoffMaxSeconds = 300;
        Assert.Equal(10_000u, options.Travel.BackoffMs(1));
        Assert.Equal(20_000u, options.Travel.BackoffMs(2));
        var travel = new RecordingTravel(new PlayerbotTravel(options));
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(configureServices: services => services.AddSingleton<IPlayerbotTravel>(travel));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var home = new PlayerbotHomeArea(1, TravelJourneyShipTests.Goal, 30f, "Porthome");
        Vector3 elsewhere = ShipTestWorld.Start + new Vector3(-40f, 0f, 0f);
        var brain = new PlayerbotBrain(world.Session, options);

        Task<uint> NowAsync() => world.OnWorldAsync(() => world.Host.World.NowMs);

        async Task AdvanceToAsync(uint at)
        {
            uint now = await NowAsync();
            if (unchecked((int)(at - now)) > 0) await world.AdvanceAsync(at - now);
        }

        Task<int> BegunAsync() => world.OnWorldAsync(() => travel.Begun.Count);

        // The journey under way runs past its bound: the next think fails it (overdue), the one after reads it in TryTravelHome. The
        // world time of that think (the backoff counts from it).
        async Task<uint> FailJourneyAsync(TravelJourney expected)
        {
            await world.OnWorldAsync(() =>
            {
                TravelJourney journey = PlayerbotTravel.JourneyOf(world.Player)!;
                Assert.Same(expected, journey);
                Assert.Equal(JourneyState.Active, journey.State);
                journey.DeadlineMs = unchecked(world.Host.World.NowMs - 1);
            });
            await ThinkAsync(world, brain);
            await world.OnWorldAsync(() => Assert.Equal(JourneyState.Failed, expected.State));
            uint at = await NowAsync();
            await ThinkAsync(world, brain);
            return at;
        }

        async Task<TravelJourney> LastBegunAsync(int count)
            => await world.OnWorldAsync(() =>
            {
                Assert.Equal(count, travel.Begun.Count);
                TravelJourney journey = travel.Begun[^1].Journey ?? throw new Xunit.Sdk.XunitException("the journey home did not begin");
                Assert.Equal(new TravelPlace(1, home.Centre), journey.Goal);
                Assert.Equal(JourneyState.Active, journey.State);
                return journey;
            });

        try
        {
            await world.OnWorldAsync(() => brain.Home = home);
            await ThinkAsync(world, brain);
            TravelJourney first = await LastBegunAsync(1);

            // The first journey home fails: the wait is BackoffMs(1). The return rules teleport the bot home meanwhile (the once a cooldown
            // allows); it is put back on map 0.
            uint failedAt = await FailJourneyAsync(first);
            await world.OnWorldAsync(() => Assert.Equal(1, brain.HomeReturn.Teleports));
            bool onMap1 = false;
            for (int i = 0; i < 200 && !onMap1; i++)
            {
                onMap1 = await world.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(world.Session, world.Player);
                    return world.Player.IsInWorld && world.Player.MapId == 1 && world.Teleports.StageOf(world.Player) is null;
                });
                if (!onMap1) await world.AdvanceAsync(50);
            }

            Assert.True(onMap1, "the home teleport did not take the bot to map 1");
            await world.PlaceAsync(0, ShipTestWorld.Start);
            await AdvanceToAsync(failedAt + 9_000);
            await ThinkAsync(world, brain);
            Assert.Equal(1, await BegunAsync());
            await AdvanceToAsync(failedAt + 10_000);
            await ThinkAsync(world, brain);
            TravelJourney second = await LastBegunAsync(2);

            // The second journey home BEGAN and failed: the count was not cleared by its beginning, the wait doubles to BackoffMs(2).
            failedAt = await FailJourneyAsync(second);
            await AdvanceToAsync(failedAt + 15_000);
            await ThinkAsync(world, brain);
            await world.OnWorldAsync(() =>
            {
                Assert.Equal(2, travel.Begun.Count); // 15 s after the second failure: still waiting
                Assert.Equal(1, brain.HomeReturn.Teleports);
                Assert.Equal(0u, world.Player.MapId);
            });

            // In the wait, another caller's journey begins and fails: it does not count (the wait still ends 20 s after the second failure).
            TravelJourney other = await world.OnWorldAsync(() =>
            {
                Assert.True(travel.Inner.TryBegin(world.Session, world.Player, 0, elsewhere, out string? why), why);
                return PlayerbotTravel.JourneyOf(world.Player)!;
            });
            await ThinkAsync(world, brain);
            await FailJourneyAsync(other);
            Assert.Equal(2, await BegunAsync());
            Assert.True(await NowAsync() - failedAt < 20_000u);
            await AdvanceToAsync(failedAt + 20_000);
            await ThinkAsync(world, brain);
            TravelJourney third = await LastBegunAsync(3);

            // Another caller's journey replaces the journey home under way and fails: neither counts, and with the wait over the journey
            // home begins again at once.
            other = await world.OnWorldAsync(() =>
            {
                Assert.True(travel.Inner.TryBegin(world.Session, world.Player, 0, elsewhere, out string? why), why);
                TravelJourney replacing = PlayerbotTravel.JourneyOf(world.Player)!;
                Assert.NotSame(third, replacing);
                return replacing;
            });
            await FailJourneyAsync(other);
            TravelJourney fourth = await LastBegunAsync(4);

            // The journey home arrives: the count starts again, so the next journey begins at once, and its failure waits BackoffMs(1).
            await world.OnWorldAsync(() => fourth.State = JourneyState.Arrived);
            await ThinkAsync(world, brain);
            TravelJourney fifth = await LastBegunAsync(5);
            failedAt = await FailJourneyAsync(fifth);
            await AdvanceToAsync(failedAt + 9_000);
            await ThinkAsync(world, brain);
            Assert.Equal(5, await BegunAsync());
            await AdvanceToAsync(failedAt + 10_000);
            await ThinkAsync(world, brain);
            await LastBegunAsync(6);
            await world.OnWorldAsync(() =>
            {
                Assert.Equal(1, brain.HomeReturn.Teleports);
                Assert.All(travel.Begun, b => Assert.NotNull(b.Journey));
            });
            await TraceAsync(world, brain);
        }
        finally
        {
            brain.Stop();
        }
    }
}
