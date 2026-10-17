using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// Fresh-launch start homes (2026-10-12 live: no fresh bot had a home, so level 1 bots rode the Deeprun Tram to Stormwind quests, died to
/// the next zone's mobs, and about 258 orcs and trolls shared the Valley of Trials): every bot the fresh launch creates is anchored to its
/// race's start area, each start area holds at most <c>FreshStartZoneCap</c> bots, the home follows the bot's level, and a rush that finds
/// every start area full waits instead of failing.
/// </summary>
public sealed class FreshStartHomeTests
{
    private static readonly IReadOnlyDictionary<byte, string> ExpectedPlace = new Dictionary<byte, string>
    {
        [1] = "start:northshire", [2] = "start:valley-of-trials", [3] = "start:coldridge", [4] = "start:shadowglen",
        [5] = "start:deathknell", [6] = "start:camp-narache", [7] = "start:coldridge", [8] = "start:valley-of-trials",
    };

    private static Task<PopulationTestHost> StartAsync(MemoryHomeStore homes, Action<PlayerbotOptions>? configure = null)
        => PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 20;
            o.Population.FreshLaunch = true;
            o.Population.TargetOnline = 8;
            o.Population.Factions = PopulationFactions.Both;
            o.Population.MaxStartsPerInterval = 20;
            o.Population.MaxCreatePerMinute = 60;
            o.Population.TownProgression = false; // these tests are about the start homes; the town homes have their own tests
            configure?.Invoke(o);
        }, configureServices: services => services.AddSingleton<IManagedPlayerbotHomeStore>(homes), dataStore: () => new FreshLaunchTests.RaceStartDataStore());

    /// <summary>The terrain of the test world has no area flags: every point is in zone 12, so the zone clip has a zone to clip to.</summary>
    private static PopulationTestHost WithZone(PopulationTestHost world)
    {
        world.Bots.ZoneLookups = _ => new PlayerbotZoneLookup((_, _, _) => PopulationTestHost.Elwynn);
        return world;
    }

    private static async Task<byte> RaceOfAsync(PopulationTestHost world, Guid botId)
    {
        int account = (await world.Owners.LoadAllAsync()).Single(b => b.BotId == botId).AccountId;
        return (await world.Host.Characters.GetByAccountAsync(account)).Single().Race;
    }

    private static Task SetLevelAsync(PopulationTestHost world, Guid botId, byte level)
        => world.Host.World.InvokeAsync(() =>
        {
            world.Bots.FindSession(botId)!.Player!.Level = level;
            return true;
        });

    /// <summary>One more pass, a full interval later (the home sweep runs at most once per interval).</summary>
    private static async Task<PopulationStatus> NextPassAsync(PopulationTestHost world)
    {
        world.Feature.ClockMs += (world.Population.IntervalSeconds * 1000L) + 1000;
        world.Ticks.Set(200, 10);
        return await world.PassAsync();
    }

    private static int HeldBy(PopulationTestHost world, string place)
        => world.Bots.Snapshot().Count(s => world.Bots.HomeOf(s.BotId)?.Place == place);

    [Fact]
    public async Task AFreshBot_GetsItsRacesStartHome_ClippedToTheStartZone()
    {
        var homes = new MemoryHomeStore();
        await using PopulationTestHost world = WithZone(await StartAsync(homes));

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("created 8", status.Reason);
        await world.WaitForSettledAsync(8);
        foreach (PlayerbotStatus bot in world.Bots.Snapshot())
        {
            byte race = await RaceOfAsync(world, bot.BotId);
            PlayerbotHome home = Assert.IsType<PlayerbotHome>(world.Bots.HomeOf(bot.BotId));
            Assert.Equal(ExpectedPlace[race], home.Place);
            Assert.Equal(1100f, home.Radius);
            Assert.True(home.ZoneClipped);
            Assert.Equal(FreshLaunchTests.RaceStartDataStore.StartOf(race).X, home.X);
            Assert.Equal(FreshLaunchTests.RaceStartDataStore.StartOf(race).Y, home.Y);
            Assert.Equal(0u, home.MapId);
            Assert.NotNull(homes.Get(bot.BotId)); // stored, not only in memory
        }
    }

    [Fact]
    public async Task StartHomeOff_GivesNoHome_AndNoCap()
    {
        await using PopulationTestHost world = WithZone(await StartAsync(new MemoryHomeStore(), o =>
        {
            o.Population.FreshStartHome = false;
            o.Population.FreshStartZoneCap = 1;
        }));

        world.Ticks.Set(200, 10);
        Assert.Contains("created 8", (await world.PassAsync()).Reason);
        await world.WaitForSettledAsync(8);
        Assert.All(world.Bots.Snapshot(), bot => Assert.Null(world.Bots.HomeOf(bot.BotId)));
    }

    [Fact]
    public async Task TheStartZoneCap_IsHonoured_TheExtraBotsAreDeferredNotFailed_AndAGraduateOpensASlot()
    {
        await using PopulationTestHost world = WithZone(await StartAsync(new MemoryHomeStore(), o =>
        {
            o.Population.Factions = PopulationFactions.Alliance; // three start areas: Northshire, Coldridge, Shadowglen
            o.Population.FreshStartZoneCap = 2;
            o.Population.TargetOnline = 8;
        }));

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        // Three areas of two: six bots of the eight asked for; the rest wait, and the status says why.
        Assert.Contains("created 6", status.Reason);
        Assert.Contains("held back: start zones full", status.Reason);
        Assert.Null(status.LastError);
        await world.WaitForSettledAsync(6);
        foreach (string place in new[] { "start:northshire", "start:coldridge", "start:shadowglen" })
            Assert.Equal(2, HeldBy(world, place));

        // Nothing is created while the areas stay full.
        status = await NextPassAsync(world);
        Assert.Contains("created 0", status.Reason);
        Assert.Equal(6, world.RunningCount);

        // A Coldridge bot reaches the graduate level: its place is free again, and only that area has room.
        Guid graduate = world.Bots.Snapshot().First(s => world.Bots.HomeOf(s.BotId)?.Place == "start:coldridge").BotId;
        await SetLevelAsync(world, graduate, 6);
        status = await NextPassAsync(world);
        Assert.Contains("created 1", status.Reason);
        Assert.Equal("zone:coldridge", world.Bots.HomeOf(graduate)!.Place);
        Assert.Equal(2, HeldBy(world, "start:coldridge"));
        Assert.Equal(2, HeldBy(world, "start:northshire"));
        Assert.Equal(2, HeldBy(world, "start:shadowglen"));
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task ARush_ThatFindsEveryStartAreaFull_WaitsAndSaysSo_ThenGoesOnAsBotsGraduate()
    {
        await using PopulationTestHost world = WithZone(await StartAsync(new MemoryHomeStore(), o =>
        {
            o.Population.Enabled = false; // the rush needs no running population
            o.Population.TargetOnline = 0;
            o.Population.Factions = PopulationFactions.Alliance;
            o.Population.FreshStartZoneCap = 2;
            o.Population.IntervalSeconds = 2;
            o.Population.RushBrakeWindowSeconds = 1;
        }));

        Assert.Null(world.Feature.StartRush(20, 50));
        PopulationRushStatus status = await WaitRushAsync(world, r => r.Created >= 6 && r.Reason.StartsWith("deferred: start zones full", StringComparison.Ordinal),
            "the rush to find every start area full");
        Assert.Equal("running", status.State);
        Assert.Equal(6, status.Created);
        Assert.Equal(0, status.Failed);
        await world.WaitForSettledAsync(6);

        // A graduate frees one place; the interval pass that runs beside the rush sweeps it, and the rush goes on.
        Guid graduate = world.Bots.Snapshot().First(s => world.Bots.HomeOf(s.BotId)?.Place == "start:coldridge").BotId;
        await SetLevelAsync(world, graduate, 6);
        DateTime until = DateTime.UtcNow.AddSeconds(15);
        while (!world.Bots.HomeOf(graduate)!.Place!.StartsWith("zone:", StringComparison.Ordinal) && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.StartsWith("zone:", world.Bots.HomeOf(graduate)!.Place);
        status = await WaitRushAsync(world, r => r.Created >= 7, "the rush to go on after a bot graduated", seconds: 30);
        Assert.Equal("running", status.State);
        Assert.Equal(0, status.Failed);
        Assert.True(world.Feature.StopRush());
    }

    [Fact]
    public async Task TheHome_FollowsTheLevel_WidensAtTheGraduateLevel_AndIsReleasedAtTheReleaseLevel()
    {
        await using PopulationTestHost world = WithZone(await StartAsync(new MemoryHomeStore(), o =>
        {
            o.Population.Factions = PopulationFactions.Alliance;
            o.Population.TargetOnline = 2;
            o.Population.FreshStartHome = false; // the bots first exist homeless, as the live fresh launch made them
        }));
        world.Ticks.Set(200, 10);
        Assert.Contains("created 2", (await world.PassAsync()).Reason);
        await world.WaitForSettledAsync(2);
        Guid[] bots = [.. world.Bots.Snapshot().Select(s => s.BotId)];
        Assert.All(bots, id => Assert.Null(world.Bots.HomeOf(id)));

        // Adoption: switched on, the sweep gives each running bot of the population the start home of its race.
        world.Population.FreshStartHome = true;
        Assert.Contains("start homes 2", (await NextPassAsync(world)).Reason);
        foreach (Guid id in bots)
        {
            PlayerbotHome home = world.Bots.HomeOf(id)!;
            Assert.Equal(ExpectedPlace[await RaceOfAsync(world, id)], home.Place);
            Assert.Equal(1100f, home.Radius);
            Assert.False(world.Bots.FindBrain(id)!.ActiveFilterHome!.AllowsLeaveQuests);
        }

        // One level under the graduate level: still the valley.
        await SetLevelAsync(world, bots[0], 5);
        await NextPassAsync(world);
        Assert.StartsWith("start:", world.Bots.HomeOf(bots[0])!.Place);
        Assert.Equal(1100f, world.Bots.HomeOf(bots[0])!.Radius);

        // At the graduate level the home widens to the start zone and leave-home quests open up.
        await SetLevelAsync(world, bots[0], 6);
        await NextPassAsync(world);
        PlayerbotHome widened = world.Bots.HomeOf(bots[0])!;
        Assert.StartsWith("zone:", widened.Place);
        Assert.Equal(4500f, widened.Radius);
        Assert.True(widened.ZoneClipped);
        Assert.True(world.Bots.FindBrain(bots[0])!.ActiveFilterHome!.AllowsLeaveQuests);
        Assert.StartsWith("start:", world.Bots.HomeOf(bots[1])!.Place); // the other bot is still level 1

        // One level under the release level: still widened. At it: no home of the population's.
        await SetLevelAsync(world, bots[0], 9);
        await NextPassAsync(world);
        Assert.StartsWith("zone:", world.Bots.HomeOf(bots[0])!.Place);
        await SetLevelAsync(world, bots[0], 10);
        await NextPassAsync(world);
        Assert.Null(world.Bots.HomeOf(bots[0]));
        Assert.NotNull(world.Bots.HomeOf(bots[1]));
    }

    /// <summary>
    /// A pass that captured the world before the shutdown began (held reading the membership while the managed feature stops every bot)
    /// must not run its start-home sweep: the graduation it would make is the shutdown's to skip (SweepStartHomesAsync's stopping guard).
    /// Without a shutdown the same held pass widens the home, so the sweep is due and the guard is what keeps it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGraduationSweepInFlightWhenTheShutdownBegins_ChangesNoHome(bool shutdown)
    {
        var homes = new MemoryHomeStore();
        await using PopulationTestHost world = WithZone(await StartAsync(homes, o =>
        {
            o.Population.Factions = PopulationFactions.Alliance;
            o.Population.TargetOnline = 1;
        }));
        world.Ticks.Set(200, 10);
        Assert.Contains("created 1", (await world.PassAsync()).Reason);
        await world.WaitForSettledAsync(1);
        Guid bot = Assert.Single(world.Bots.Snapshot()).BotId;
        Assert.StartsWith("start:", world.Bots.HomeOf(bot)!.Place);

        // At the graduate level the next sweep widens the home to the start zone.
        await SetLevelAsync(world, bot, 6);
        world.Feature.ClockMs += (world.Population.IntervalSeconds * 1000L) + 1000;
        TaskCompletionSource held = world.Members.HoldNextLoad();
        world.Ticks.Set(200, 10);
        Task pass = world.Feature.RunPassNowAsync();
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the pass has captured the bot at level 6 and is blocked before its sweep

        if (shutdown) await world.Bots.ShutdownBeforeWorldStopAsync();
        world.Members.ReleaseHeldLoad();
        await pass.WaitAsync(TimeSpan.FromSeconds(30));

        PlayerbotHome home = world.Bots.HomeOf(bot)!;
        Assert.StartsWith(shutdown ? "start:" : "zone:", home.Place);
        Assert.Equal(shutdown ? 1100f : 4500f, home.Radius);
        Assert.Equal(home.Place, homes.Get(bot)!.Place); // the stored row too
        if (shutdown) Assert.Null(world.Feature.Status.LastError);
    }

    [Fact]
    public async Task AnOperatorsHome_IsNeverTouchedByTheGraduationSweep()
    {
        var homes = new MemoryHomeStore();
        await using PopulationTestHost world = WithZone(await StartAsync(homes, o =>
        {
            o.Population.Factions = PopulationFactions.Alliance;
            o.Population.TargetOnline = 1;
            o.Population.FreshStartHome = false;
        }));
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(1);
        Guid bot = world.Bots.Snapshot().Single().BotId;
        var centre = new System.Numerics.Vector3(-8900f, -100f, 80f);
        await world.Bots.SetHomesAsync([new ManagedPlayerbotFeature.HomeTarget(bot, 0, centre)], 600f, "Sentinel Hill");

        world.Population.FreshStartHome = true;
        await SetLevelAsync(world, bot, 10);
        await NextPassAsync(world);
        await SetLevelAsync(world, bot, 1);
        await NextPassAsync(world);

        Assert.Equal("Sentinel Hill", world.Bots.HomeOf(bot)!.Place);
        Assert.Equal(600f, world.Bots.HomeOf(bot)!.Radius);
    }

    private static async Task<PopulationRushStatus> WaitRushAsync(PopulationTestHost world, Func<PopulationRushStatus, bool> done, string what, int seconds = 20)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done(world.Feature.RushStatus))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for: {what}; rush is {world.Feature.RushStatus}");
            await Task.Delay(20);
        }

        return world.Feature.RushStatus;
    }

    // ---- the deploy-safety bound of the sweep ----

    /// <summary>A race-start data store that counts the start position queries (character creation asks too).</summary>
    private sealed class CountingStore : IWorldDataStore
    {
        private readonly FreshLaunchTests.RaceStartDataStore _inner = new();
        private int _starts;

        public int StartQueries => Volatile.Read(ref _starts);

        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            return _inner.GetStartPositionAsync(race, cls, cancellationToken);
        }

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default) => _inner.GetRaceInfoAsync(race, gender, cancellationToken);

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => _inner.GetClassInfoAsync(cls, cancellationToken);

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default) => _inner.IsValidRaceClassAsync(race, cls, cancellationToken);
    }

    private static Task<PopulationTestHost> StartManyAsync(MemoryHomeStore homes, CountingStore data, int bots, Action<PlayerbotOptions>? configure = null)
        => PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = bots + 10;
            o.MaxRegisteredBots = bots + 10;
            o.Population.FreshLaunch = true;
            o.Population.FreshStartHome = false; // the bots first exist homeless, as on the live fresh launch
            o.Population.Factions = PopulationFactions.Alliance;
            o.Population.TargetOnline = bots;
            o.Population.MaxStartsPerInterval = 100;
            o.Population.MaxCreatePerMinute = 60;
            o.Population.TownProgression = false;
            configure?.Invoke(o);
        }, configureServices: services => services.AddSingleton<IManagedPlayerbotHomeStore>(homes), dataStore: () => data);

    private static int Homed(PopulationTestHost world) => world.Bots.Snapshot().Count(s => world.Bots.HomeOf(s.BotId) is not null);

    [Fact]
    public async Task TheSweep_MakesAtMostTheBoundedAttemptsPerInterval_OnceAnInterval_AndInTimeHomesEveryBot()
    {
        var homes = new MemoryHomeStore();
        await using PopulationTestHost world = WithZone(await StartManyAsync(homes, new CountingStore(), 60));
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(60);
        Assert.Equal(0, Homed(world));

        world.Population.FreshStartHome = true;
        int[] seen = new int[12];
        await NextPassAsync(world);
        seen[0] = Homed(world);
        Assert.InRange(seen[0], 1, PlayerbotPopulationFeature.MaxHomeChangesPerSweep);

        // Within the same interval nothing more happens: one sweep per interval.
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        Assert.Equal(seen[0], Homed(world));

        // Every interval at most the bound more, and in the end every bot has its home.
        int previous = seen[0], sweeps = 1;
        while (Homed(world) < 60 && sweeps < 12)
        {
            await NextPassAsync(world);
            sweeps++;
            Assert.InRange(Homed(world) - previous, 0, PlayerbotPopulationFeature.MaxHomeChangesPerSweep);
            previous = Homed(world);
        }

        Assert.Equal(60, Homed(world));
        Assert.InRange(sweeps, 3, 6);
    }

    [Fact]
    public async Task AFailingHomeStore_CostsAtMostTheBoundedAttemptsPerSweep_AndRotatesThroughTheBots()
    {
        var homes = new MemoryHomeStore { FailSet = true };
        await using PopulationTestHost world = WithZone(await StartManyAsync(homes, new CountingStore(), 60));
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(60);

        world.Population.FreshStartHome = true;
        await NextPassAsync(world);
        Assert.InRange(homes.SetCalls, 1, PlayerbotPopulationFeature.MaxHomeChangesPerSweep);
        int afterFirst = homes.SetCalls;
        Assert.Equal(0, Homed(world));
        Assert.Contains("start homes were not set", world.Feature.Status.LastError);

        // The bots that failed wait; the next sweep tries other bots, again within the bound.
        await NextPassAsync(world);
        Assert.InRange(homes.SetCalls - afterFirst, 1, PlayerbotPopulationFeature.MaxHomeChangesPerSweep);
        Assert.True(homes.DistinctBots > afterFirst, $"the second sweep retried the same bots ({homes.DistinctBots} distinct of {homes.SetCalls} attempts)");
    }

    [Fact]
    public async Task ABotOnAMapTheServerDoesNotAllow_CostsNoQueryAtAll()
    {
        var homes = new MemoryHomeStore();
        var data = new CountingStore();
        await using PopulationTestHost world = WithZone(await StartManyAsync(homes, data, 20, o =>
        {
            o.Population.Factions = PopulationFactions.Horde; // orc, troll, tauren start on Kalimdor (map 1)
            o.AllowedMaps = [0];
        }));
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(20);
        int queries = data.StartQueries;

        world.Population.FreshStartHome = true;
        for (int i = 0; i < 3; i++) await NextPassAsync(world);

        // Undead start on map 0, which is allowed: their one (cached) start query is the only one. Orc, troll and tauren (map 1) cost none.
        Assert.InRange(data.StartQueries - queries, 0, 1);
        Assert.InRange(homes.SetCalls, 0, Homed(world));
        Assert.True(Homed(world) < 20, "the bots of the map the server does not allow stay homeless");
        Assert.Contains("not an allowed playerbot map", world.Feature.Status.LastError ?? string.Empty);
    }

    // ---- adoption does not pull back bots that moved on; an operator's clear sticks ----

    private static Task MoveAsync(PopulationTestHost world, Guid botId, byte level, uint zone, float x, float y)
        => world.Host.World.InvokeAsync(() =>
        {
            Player player = world.Bots.FindSession(botId)!.Player!;
            player.Level = level;
            player.ZoneId = zone;
            player.SetPosition(x, y, player.Z, player.Orientation);
            return true;
        });

    [Fact]
    public async Task Adoption_HomesALowLevelBotAnywhere_AndAGraduatedBotOnlyInItsStartCountry()
    {
        await using PopulationTestHost world = WithZone(await StartManyAsync(new MemoryHomeStore(), new CountingStore(), 4));
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(4);
        Guid[] bots = [.. world.Bots.Snapshot().Select(s => s.BotId)];
        FarFromHome(world, out float farX, out float farY);

        await MoveAsync(world, bots[0], 3, 40, farX, farY);                                   // stuck, level 3, far away: homed (start)
        await MoveAsync(world, bots[1], 8, 40, farX, farY);                                   // level 8 in another zone: already moved on
        await MoveAsync(world, bots[2], 8, PopulationTestHost.Elwynn, farX, farY);            // level 8, still in the start zone: homed (zone)
        await MoveAsync(world, bots[3], 14, 40, farX, farY);                                  // past the release level: nothing
        world.Population.FreshStartHome = true;
        await NextPassAsync(world);

        Assert.StartsWith("start:", world.Bots.HomeOf(bots[0])!.Place);
        Assert.Null(world.Bots.HomeOf(bots[1]));
        Assert.StartsWith("zone:", world.Bots.HomeOf(bots[2])!.Place);
        Assert.Null(world.Bots.HomeOf(bots[3]));

        // And it stays so: the bot that moved on is not pulled back at a later sweep.
        await NextPassAsync(world);
        Assert.Null(world.Bots.HomeOf(bots[1]));
    }

    private static void FarFromHome(PopulationTestHost world, out float x, out float y)
    {
        StartPosition start = FreshLaunchTests.RaceStartDataStore.StartOf(1);
        (x, y) = (start.X + 6000f, start.Y);
    }

    [Fact]
    public async Task AnOperatorsClear_OfAStartHome_Sticks()
    {
        var homes = new MemoryHomeStore();
        await using PopulationTestHost world = WithZone(await StartManyAsync(homes, new CountingStore(), 2));
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(2);
        world.Population.FreshStartHome = true;
        await NextPassAsync(world);
        Guid[] bots = [.. world.Bots.Snapshot().Select(s => s.BotId)];
        Assert.All(bots, id => Assert.NotNull(world.Bots.HomeOf(id)));

        Assert.Equal(1, await world.Bots.ClearHomesAsync([bots[0]])); // .playerbot home clear
        await NextPassAsync(world);
        await NextPassAsync(world);

        Assert.Null(world.Bots.HomeOf(bots[0]));
        Assert.NotNull(world.Bots.HomeOf(bots[1]));
        Assert.Equal(PlayerbotPopulationFeature.HomeOptOutBand, world.Members.Snapshot.Single(m => m.BotId == bots[0]).Band); // and it is remembered
    }

    // ---- the planner, without a world ----

    private static PopulationRaceClassCache AllRaces() => new(PopulationRaceClassCache.Races.Select(r => (r, (byte)1)));

    private static Dictionary<string, int> Counts(params (string Area, int Count)[] counts) => counts.ToDictionary(c => c.Area, c => c.Count);

    [Fact]
    public void PlanFresh_WithTheCap_PicksTheLeastCrowdedArea_OfTheFactionsRaces()
    {
        var options = new PlayerbotPopulationOptions { Factions = PopulationFactions.Alliance, Classes = [1] };
        // Coldridge (dwarf, gnome) is empty, Northshire and Shadowglen are not: every bot goes to Coldridge until it catches up.
        for (int i = 0; i < 50; i++)
        {
            PopulationFreshPlan plan = Assert.IsType<PopulationFreshPlan>(PopulationBandPlanner.PlanFresh(options, 0, 0, AllRaces(), new Random(i),
                Counts(("northshire", 5), ("coldridge", 0), ("shadowglen", 5)), 10, out bool full));
            Assert.False(full);
            Assert.Contains(plan.Race, new byte[] { 3, 7 });
        }
    }

    [Fact]
    public void PlanFresh_WithTheCap_SkipsAFullArea()
    {
        var options = new PlayerbotPopulationOptions { Factions = PopulationFactions.Alliance, Classes = [1] };
        HashSet<byte> races = [];
        for (int i = 0; i < 60; i++)
        {
            PopulationFreshPlan plan = Assert.IsType<PopulationFreshPlan>(PopulationBandPlanner.PlanFresh(options, 0, 0, AllRaces(), new Random(i),
                Counts(("northshire", 4), ("coldridge", 4), ("shadowglen", 3)), 4, out _));
            races.Add(plan.Race);
        }

        Assert.Equal(new byte[] { 4 }, races); // only the night elves' Shadowglen has room
    }

    [Fact]
    public void PlanFresh_WithEveryAreaFull_ReportsFull_ForOneFaction_ButTriesTheOtherFactionWhenBothAreConfigured()
    {
        var one = new PlayerbotPopulationOptions { Factions = PopulationFactions.Alliance, Classes = [1] };
        var full = Counts(("northshire", 3), ("coldridge", 3), ("shadowglen", 3));
        Assert.Null(PopulationBandPlanner.PlanFresh(one, 0, 0, AllRaces(), new Random(1), full, 3, out bool isFull));
        Assert.True(isFull);

        var both = new PlayerbotPopulationOptions { Factions = PopulationFactions.Both, Classes = [1] };
        PopulationFreshPlan plan = Assert.IsType<PopulationFreshPlan>(PopulationBandPlanner.PlanFresh(both, 0, 0, AllRaces(), new Random(1), full, 3, out isFull));
        Assert.Equal(Team.Horde, plan.Team); // the Alliance share is ahead of nothing, but its areas are full
        Assert.False(isFull);

        // No valid race and class at all is a different answer: not "full".
        Assert.Null(PopulationBandPlanner.PlanFresh(one, 0, 0, new PopulationRaceClassCache([]), new Random(1), full, 3, out isFull));
        Assert.False(isFull);
    }

    [Fact]
    public void PlanFresh_WithoutACap_IsTheOrdinaryPlan()
    {
        var options = new PlayerbotPopulationOptions { Factions = PopulationFactions.Both, Classes = [1] };
        PopulationFreshPlan? plan = PopulationBandPlanner.PlanFresh(options, 0, 0, AllRaces(), new Random(3), Counts(("coldridge", 999)), 0, out bool full);
        Assert.NotNull(plan);
        Assert.False(full);
    }

    [Fact]
    public void TheStartHomeKeys_HaveTheirDefaults_AndAreValidated()
    {
        var options = new PlayerbotPopulationOptions();
        Assert.True(options.FreshStartHome);
        Assert.Equal((1100, 6, 4500, 10, 40), (options.FreshStartHomeRadius, options.FreshGraduateLevel, options.FreshZoneHomeRadius, options.FreshReleaseLevel, options.FreshStartZoneCap));
        Assert.Equal((0, 10), (options.LeaveHomeLevelMargin, options.TramMinLevel));
        options.Validate();

        foreach (Action<PlayerbotPopulationOptions> bad in new Action<PlayerbotPopulationOptions>[]
        {
            o => o.FreshStartHomeRadius = 49, o => o.FreshStartHomeRadius = 5001, o => o.FreshZoneHomeRadius = 10, o => o.FreshGraduateLevel = 1,
            o => o.FreshReleaseLevel = 61, o => o.FreshReleaseLevel = 5, o => o.FreshStartZoneCap = -1, o => o.FreshStartZoneCap = 1001,
            o => o.LeaveHomeLevelMargin = 11, o => o.TramMinLevel = 61, o => o.TramMinLevel = -1,
        })
        {
            var broken = new PlayerbotPopulationOptions();
            bad(broken);
            Assert.Throws<InvalidOperationException>(broken.Validate);
        }
    }

    [Fact]
    public void ThePlaceText_RoundTrips_AndIgnoresAnyOtherHome()
    {
        foreach (string area in PopulationStartAreas.Areas)
            foreach (StartHomeStage stage in new[] { StartHomeStage.Start, StartHomeStage.Zone })
            {
                Assert.True(PopulationStartAreas.TryParse(PopulationStartAreas.PlaceOf(area, stage), out StartHomeStage parsed, out string parsedArea));
                Assert.Equal((stage, area), (parsed, parsedArea));
            }

        Assert.False(PopulationStartAreas.TryParse("Sentinel Hill", out _, out _));
        Assert.False(PopulationStartAreas.TryParse("start:atlantis", out _, out _));
        Assert.False(PopulationStartAreas.TryParse(null, out _, out _));
        Assert.Equal(6, PopulationStartAreas.Areas.Count);
        Assert.Equal(6, PopulationRaceClassCache.Races.Select(PopulationStartAreas.AreaOf).Distinct().Count());
    }

    private sealed class MemoryHomeStore : IManagedPlayerbotHomeStore
    {
        private readonly ConcurrentDictionary<Guid, PlayerbotHome> _rows = new();

        private readonly ConcurrentDictionary<Guid, int> _setBots = new();
        private int _setCalls;

        public bool FailSet { get; init; }

        public int SetCalls => Volatile.Read(ref _setCalls);

        public int DistinctBots => _setBots.Count;

        public PlayerbotHome? Get(Guid botId) => _rows.GetValueOrDefault(botId);

        public Task<IReadOnlyList<PlayerbotHome>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PlayerbotHome>>([.. _rows.Values.OrderBy(h => h.BotId)]);

        public Task SetAsync(PlayerbotHome home, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _setCalls);
            _setBots[home.BotId] = 1;
            if (FailSet) throw new InvalidOperationException("the characters database is unavailable");
            _rows[home.BotId] = home;
            return Task.CompletedTask;
        }

        public Task<bool> ClearAsync(Guid botId, CancellationToken cancellationToken = default) => Task.FromResult(_rows.TryRemove(botId, out _));
    }
}
