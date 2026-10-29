using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The zone content value steering relocation of level-capped bots, through the real population feature in a real world: the low-value
/// move, the minimum stay, no ping-pong inside the cap reach, the "no safe zone" line, the ValueRouting switch and the rebuild key.
/// The zone value is injected with <see cref="PlayerbotPopulationFeature.ZoneValueBuilder"/>; the zones are an area table of their own,
/// swapped in after the first bot exists (the host creates it in Elwynn). Everything is deterministic: where a draw exists, only one
/// zone can be drawn.
/// </summary>
public sealed class PopulationZoneValueRelocationTests
{
    private const int Capped = PlayerbotPopulationOptions.MaxLevel;
    private const uint Poor = 200;     // levels 55-60, one quest
    private const uint Rich = 201;     // levels 55-60, six quests, a rep faction, four gear types
    private const uint Reach56 = 202;  // levels 50-56: within ValueCapReach (6) of 60, below 60 - OutlevelMargin (2) - 56 = 4 short
    private const uint Low = 203;      // levels 30-40: out-levelled by more than the cap reach

    private sealed class CapturedLog : ILogger<PlayerbotPopulationFeature>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Enqueue(formatter(state, exception));
    }

    private static ZoneContentFacts Facts(uint zone, int quests, bool rich)
    {
        ZoneQuestFact[] q = [.. Enumerable.Range(0, quests).Select(i => new ZoneQuestFact((uint)(zone * 100 + i), 50, 58 + i % 3, 0, 0, rich && i == 0 ? [529u] : []))];
        ZoneGearFact[] gear = rich
            ? [.. new uint[] { 5, 1, 7, 8 }.Select(t => new ZoneGearFact(zone * 10 + t, t, 62, 55, uint.MaxValue, 0, ReputationRank.Neutral, 0, false))]
            : [];
        return new ZoneContentFacts(zone) { Quests = q, Gear = gear };
    }

    private static PopulationZoneValue Value(params ZoneContentFacts[] facts) => new(facts.ToDictionary(f => f.ZoneId));

    /// <summary>Replace the host's area table and graveyards with these zones (zone, lowest sub-area level, highest), open to both teams.</summary>
    private static Task InstallZonesAsync(PopulationTestHost world, params (uint Zone, int Low, int High)[] zones) => world.Host.World.InvokeAsync(() =>
    {
        WorldMaps maps = WorldMaps.Of(world.Host.World);
        var areas = new List<AreaTemplate>();
        var locations = new List<WorldSafeLoc>();
        var links = new List<GraveyardLink>();
        foreach ((uint zone, int low, int high) in zones)
        {
            areas.Add(new AreaTemplate(zone, 0, 0, zone, 0, 0, "Zone" + zone, AreaTeams.None, 0));
            areas.Add(new AreaTemplate(zone + 1000, 0, zone, zone + 1000, 0, low, "Low" + zone, 0, 0));
            areas.Add(new AreaTemplate(zone + 2000, 0, zone, zone + 2000, 0, high, "High" + zone, 0, 0));
            locations.Add(new WorldSafeLoc(zone, 0, -9_000f - (zone - 200) * 400f, -133f + (zone - 200) * 300f, 81f, 0f, "GY" + zone));
            links.Add(new GraveyardLink(zone, zone, 0));
        }

        maps.Terrain.Areas = new AreaTable(areas, maps.Registry);
        WorldGraveyards.Of(world.Host.World).Load(new GraveyardContent(locations, links), null);
        return true;
    });

    private static Task PlaceBotAsync(PopulationTestHost world, Guid bot, int level, uint zone) => world.Host.World.InvokeAsync(() =>
    {
        Player player = world.Bots.FindSession(bot)!.Player!;
        player.Level = (byte)level;
        player.ZoneId = zone;
        return true;
    });

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Start a host with one bot, install the zones, and (when value routing is on) run a pass with the bot still in an unknown zone so
    /// the injected value is built and in force before the scenario pass. Returns the bot, still at its Elwynn place; the caller places it.
    /// </summary>
    private static async Task<(PopulationTestHost World, Guid Bot, CapturedLog Log)> StartAsync(PopulationZoneValue? value, (uint, int, int)[] zones,
        Action<PlayerbotOptions>? configure = null)
    {
        var log = new CapturedLog();
        PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 1;
            configure?.Invoke(o);
        }, withRelocation: true, configureServices: s => s.AddSingleton<ILogger<PlayerbotPopulationFeature>>(log));
        try
        {
            world.Ticks.Set(200, 10);
            Assert.Contains("created 1", (await world.PassAsync()).Reason);
            await world.WaitForSettledAsync(1);
            Guid bot = Assert.Single(world.Members.Snapshot).BotId;
            world.Population.ExcludedZones = [];
            if (value is not null) world.Feature.ZoneValueBuilder = _ => value;
            await InstallZonesAsync(world, zones);
            // The bot is level 1-10 in Elwynn, which the new atlas does not know: no candidate. This pass rebuilds the atlas and starts the value.
            world.Ticks.Set(200, 10);
            await world.PassAsync();
            if (world.Population.ValueRouting && value is not null)
            {
                await world.Feature.ZoneValueTask;
                await WaitUntilAsync(() => world.Feature.ZoneValue is not null, "the zone value to be in force");
            }

            return (world, bot, log);
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    private static void SeedStay(PopulationTestHost world, uint zone, long ageSeconds)
        => world.Members.Seed(Assert.Single(world.Members.Snapshot) with { ZoneId = zone, UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ageSeconds });

    private static async Task<PopulationStatus> PassAsync(PopulationTestHost world)
    {
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        await world.WaitForSettledAsync(1);
        return status;
    }

    private static readonly (uint, int, int)[] PoorAndRich = [(Poor, 55, Capped), (Rich, 55, Capped)];

    // A (RED): the low-value trigger never fired because its list excluded every bot already listed as a candidate.
    [Fact]
    public async Task ACappedBotInAZoneWorthLittle_StayedLongEnough_MovesToTheValuedZone()
    {
        (PopulationTestHost world, Guid bot, _) = await StartAsync(Value(Facts(Poor, 1, false), Facts(Rich, 6, true)), PoorAndRich);
        await using PopulationTestHost guard = world;
        await PlaceBotAsync(world, bot, Capped, Poor);
        SeedStay(world, Poor, ageSeconds: (world.Population.ValueRelocateMinStayMinutes + 5) * 60L);

        PopulationStatus status = await PassAsync(world);

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(Rich, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    // B: the same bot, but it arrived a minute ago.
    [Fact]
    public async Task ACappedBotInAZoneWorthLittle_BeforeTheMinimumStay_IsNotMoved()
    {
        (PopulationTestHost world, Guid bot, _) = await StartAsync(Value(Facts(Poor, 1, false), Facts(Rich, 6, true)), PoorAndRich);
        await using PopulationTestHost guard = world;
        await PlaceBotAsync(world, bot, Capped, Poor);
        SeedStay(world, Poor, ageSeconds: 60);

        PopulationStatus status = await PassAsync(world);

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(Poor, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    // C (RED): zone MaxLevel 56 is within the cap reach of level 60 but below 60 - OutlevelMargin; the best valued zone must not evict its own visitor.
    [Fact]
    public async Task ACappedBotInTheBestValuedZoneWithinTheCapReach_IsNeverRelocated_OverSeveralPasses()
    {
        (uint, int, int)[] zones = [(Reach56, 50, 56), (Poor, 55, Capped)];
        (PopulationTestHost world, Guid bot, CapturedLog log) = await StartAsync(Value(Facts(Reach56, 6, true), Facts(Poor, 1, false)), zones);
        await using PopulationTestHost guard = world;
        await PlaceBotAsync(world, bot, Capped, Reach56);
        SeedStay(world, Reach56, ageSeconds: (world.Population.ValueRelocateMinStayMinutes + 5) * 60L);
        Assert.True(Capped > 56 + world.Population.OutlevelMargin && 56 >= Capped - world.Population.ValueCapReach, "scenario precondition");

        for (int pass = 1; pass <= 3; pass++)
        {
            PopulationStatus status = await PassAsync(world);
            Assert.DoesNotContain("moved", status.Reason);
            Assert.Equal(Reach56, Assert.Single(world.Members.Snapshot).ZoneId);
        }

        Assert.DoesNotContain(log.Lines, l => l.Contains("no safe zone", StringComparison.Ordinal));
    }

    public enum MustMove { OutLevelled, Excluded }

    private static async Task<(PopulationTestHost World, Guid Bot, CapturedLog Log)> StartMustMoveAsync(MustMove kind, bool valueRouting)
    {
        // No zone Fits level 60 (Reach56 tops at 56, Low at 40); Reach56 is the only valued zone and is within the cap reach.
        (uint, int, int)[] zones = [(Low, 30, 40), (Reach56, 50, 56)];
        (PopulationTestHost world, Guid bot, CapturedLog log) = await StartAsync(Value(Facts(Reach56, 6, true)), zones, o =>
        {
            o.Population.ValueRouting = valueRouting;
            if (kind == MustMove.Excluded) o.Population.ExcludedZones = [Low];
        });
        if (kind == MustMove.Excluded) world.Population.ExcludedZones = [Low]; // StartAsync empties the list after the first bot
        await PlaceBotAsync(world, bot, Capped, Low);
        SeedStay(world, Low, ageSeconds: 10);
        return (world, bot, log);
    }

    // D: nothing fits by the old rule, one valued zone is within the cap reach: the bot moves there and the "no safe zone" line is not logged.
    [Theory]
    [InlineData(MustMove.OutLevelled)]
    [InlineData(MustMove.Excluded)]
    public async Task ABotThatMustMove_WithNoFittingZoneButAValuedOneInReach_IsMoved_AndNoNoSafeZoneLineIsLogged(MustMove kind)
    {
        (PopulationTestHost world, _, CapturedLog log) = await StartMustMoveAsync(kind, valueRouting: true);
        await using PopulationTestHost guard = world;

        PopulationStatus status = await PassAsync(world);

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(Reach56, Assert.Single(world.Members.Snapshot).ZoneId);
        Assert.DoesNotContain(log.Lines, l => l.Contains("no safe zone for out-levelled bot", StringComparison.Ordinal));
    }

    // E: with ValueRouting off the same bot gets the old behaviour: no valued move, the old "no safe zone" line, and the builder is never asked.
    [Fact]
    public async Task WithValueRoutingOff_TheSameBotStaysAndTheOldNoSafeZoneLineIsLogged()
    {
        int builds = 0;
        (PopulationTestHost world, Guid bot, CapturedLog log) = await StartMustMoveAsync(MustMove.OutLevelled, valueRouting: false);
        await using PopulationTestHost guard = world;
        world.Feature.ZoneValueBuilder = _ => { Interlocked.Increment(ref builds); return Value(Facts(Reach56, 6, true)); };

        PopulationStatus status = await PassAsync(world);

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(Low, Assert.Single(world.Members.Snapshot).ZoneId);
        Assert.Contains(log.Lines, l => l.Contains("no safe zone for out-levelled bot " + bot, StringComparison.Ordinal));
        Assert.Equal(0, Volatile.Read(ref builds));
        Assert.Null(world.Feature.ZoneValue);
    }

    // F: the build runs once per key, again when the key changes, never per pass.
    [Fact]
    public async Task TheZoneValue_IsBuiltOncePerKey_AcrossPasses()
    {
        int builds = 0;
        var value = Value(Facts(Rich, 6, true));
        var log = new CapturedLog();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true,
            configureServices: s => s.AddSingleton<ILogger<PlayerbotPopulationFeature>>(log));
        world.Feature.ZoneValueBuilder = _ => { Interlocked.Increment(ref builds); return value; };

        for (int pass = 0; pass < 4; pass++)
        {
            world.Ticks.Set(200, 10);
            await world.PassAsync();
            await world.Feature.ZoneValueTask;
        }

        Assert.Equal(1, Volatile.Read(ref builds));
        await WaitUntilAsync(() => ReferenceEquals(world.Feature.ZoneValue, value), "the first value to be in force");

        world.Feature.ZoneValueBuilderVersion = 1;
        for (int pass = 0; pass < 4; pass++)
        {
            world.Ticks.Set(200, 10);
            await world.PassAsync();
            await world.Feature.ZoneValueTask;
        }

        Assert.Equal(2, Volatile.Read(ref builds));
    }
}
