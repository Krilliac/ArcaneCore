using ArcaneCore.Game.Maps;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Parallel;

/// <summary>
/// Bot think per map (World:Parallel:BotThinkPerMap, default off; docs/research/map-local-stages-design-20261024.md, section 5.7): the
/// stage and the scheduler give the same per-bot results as one serial pass over all bots, at one and at several map threads, gated and
/// ungated; with the option off nothing changes; a map's bots are rotated fairly under a small budget.
/// </summary>
public sealed class BotThinkPerMapTests
{
    private sealed class TestBot(int index, uint mapId, uint instanceId)
    {
        public readonly int Index = index;
        public readonly uint MapId = mapId;
        public readonly uint InstanceId = instanceId;
        public long State = index + 1;
        public int Thinks;
    }

    /// <summary>One think: touches only the bot's own state, as the stage's contract requires. Pure in (seed, bot, think number).</summary>
    private static bool Think(TestBot bot, int seed)
    {
        bot.Thinks++;
        unchecked
        {
            bot.State = (bot.State * 6364136223846793005L) + (seed * 1442695040888963407L) + (bot.Index * 31L) + bot.Thinks;
        }

        return true;
    }

    private static List<TestBot> SeededBots(int seed, out (uint Map, uint Instance)[] maps)
    {
        maps = [(0u, 0u), (1u, 0u), (33u, 1u), (36u, 2u), (489u, 3u)];
        var random = new Random(seed);
        var bots = new List<TestBot>();
        for (int i = 0; i < 60; i++)
        {
            (uint map, uint instance) = maps[random.Next(maps.Length)];
            bots.Add(new TestBot(i, map, instance));
        }

        return bots;
    }

    private static WorldRuntime CreateRuntime(int threads, bool botThinkPerMap, string ungated = "")
        => new(
            new WorldRuntimeOptions
            {
                UpdateCompressionThreshold = 0,
                AutosaveIntervalMs = 0,
                CommandTimeBudgetMs = 0,
                Parallel = new ParallelMapOptions
                {
                    MapUpdateThreads = threads,
                    OwnerGuard = OwnerGuardMode.Throw,
                    BotThinkPerMap = botThinkPerMap,
                    Ungated = ungated,
                },
            },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);

    private sealed class SchedulerThinker(PlayerbotThinkScheduler<TestBot> scheduler, int seed) : IMapBotThinker
    {
        public int Calls;

        public void ThinkBots(Map map, uint diffMs)
        {
            Interlocked.Increment(ref Calls);
            scheduler.ThinkMap(
                PlayerbotThinkScheduler<TestBot>.MapKey(map.MapId, map.InstanceId),
                double.PositiveInfinity,
                bot => Think(bot, seed),
                () => 0);
        }
    }

    private static long[] RunOff(int seed, int ticks)
    {
        List<TestBot> bots = SeededBots(seed, out _);
        for (int tick = 0; tick < ticks; tick++)
        {
            foreach (TestBot bot in bots) Think(bot, seed);
        }

        return bots.Select(b => b.State).ToArray();
    }

    private static long[] RunOn(int seed, int ticks, int threads, string ungated)
    {
        List<TestBot> bots = SeededBots(seed, out (uint Map, uint Instance)[] maps);
        using WorldRuntime world = CreateRuntime(threads, botThinkPerMap: true, ungated);
        var scheduler = new PlayerbotThinkScheduler<TestBot>();
        scheduler.Assign(bots, b => PlayerbotThinkScheduler<TestBot>.MapKey(b.MapId, b.InstanceId));
        var thinker = new SchedulerThinker(scheduler, seed);
        foreach ((uint map, uint instance) in maps)
        {
            Assert.NotNull(MapBotThinkStage.AttachIfEnabled(world.GetMap(map, instance), world.Options.Parallel, thinker));
        }

        for (int tick = 0; tick < ticks; tick++) world.RunTick(50);
        Assert.Equal(ticks * maps.Length, thinker.Calls);
        return bots.Select(b => b.State).ToArray();
    }

    [Theory]
    [InlineData(1, 1, "")]
    [InlineData(2, 4, "")]
    [InlineData(3, 4, "MapBotThinkStage")]
    [InlineData(4, 2, "MapBotThinkStage")]
    public void ResultsAreIdentical_WithTheOptionOnAndOff_ForASeededMultiMapWorld(int seed, int threads, string ungated)
    {
        long[] off = RunOff(seed, ticks: 12);
        long[] on = RunOn(seed, ticks: 12, threads, ungated);
        Assert.Equal(off, on);
    }

    [Fact]
    public void DefaultConfig_IsUnchanged()
    {
        var options = new ParallelMapOptions();
        Assert.False(options.BotThinkPerMap);
        Assert.Equal(1, options.MapUpdateThreads);
        Assert.False(options.GateAllStages);
        Assert.Equal(string.Empty, options.Ungated);
        Assert.Equal(string.Empty, options.Gated);
    }

    [Fact]
    public void OptionOff_AttachesNothing_AndADisabledStageDoesNotThink()
    {
        using WorldRuntime world = CreateRuntime(1, botThinkPerMap: false);
        Map map = world.GetMap(0, 0);
        int before = map.Updaters.Count;
        var scheduler = new PlayerbotThinkScheduler<TestBot>();
        var thinker = new SchedulerThinker(scheduler, 1);
        Assert.Null(MapBotThinkStage.AttachIfEnabled(map, world.Options.Parallel, thinker));
        Assert.Equal(before, map.Updaters.Count);

        map.AddUpdater(new MapBotThinkStage(thinker, enabled: false));
        world.RunTick(50);
        Assert.Equal(0, thinker.Calls);
    }

    [Fact]
    public void Scheduler_GroupsByMap_KeepingOrder_AndDropsEmptyMaps()
    {
        var scheduler = new PlayerbotThinkScheduler<TestBot>();
        var bots = new List<TestBot> { new(0, 0, 0), new(1, 1, 0), new(2, 0, 0), new(3, 33, 7) };
        scheduler.Assign(bots, b => PlayerbotThinkScheduler<TestBot>.MapKey(b.MapId, b.InstanceId));
        Assert.Equal(4, scheduler.TotalBots);
        Assert.Equal(3, scheduler.MapCount);
        Assert.Equal(new[] { 0, 2 }, scheduler.BotsOf(PlayerbotThinkScheduler<TestBot>.MapKey(0, 0)).Select(b => b.Index).ToArray());

        scheduler.Assign(bots.Where(b => b.MapId == 1).ToList(), b => PlayerbotThinkScheduler<TestBot>.MapKey(b.MapId, b.InstanceId));
        Assert.Equal(1, scheduler.MapCount);
        Assert.Empty(scheduler.BotsOf(PlayerbotThinkScheduler<TestBot>.MapKey(0, 0)));
        Assert.Equal(0, scheduler.ThinkMap(PlayerbotThinkScheduler<TestBot>.MapKey(0, 0), 10, _ => true, () => 0));
    }

    [Fact]
    public void Scheduler_SplitsTheBudgetByHeadCount()
    {
        var scheduler = new PlayerbotThinkScheduler<TestBot>();
        var bots = new List<TestBot> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 1, 0) };
        scheduler.Assign(bots, b => PlayerbotThinkScheduler<TestBot>.MapKey(b.MapId, b.InstanceId));
        Assert.Equal(6.0, scheduler.ShareMs(PlayerbotThinkScheduler<TestBot>.MapKey(0, 0), 8), 9);
        Assert.Equal(2.0, scheduler.ShareMs(PlayerbotThinkScheduler<TestBot>.MapKey(1, 0), 8), 9);
        Assert.Equal(0.0, scheduler.ShareMs(PlayerbotThinkScheduler<TestBot>.MapKey(9, 0), 8), 9);
    }

    [Fact]
    public void Scheduler_UnderASmallBudget_ThinksAtLeastOneBotPerTick_AndRotatesFairly()
    {
        var scheduler = new PlayerbotThinkScheduler<TestBot>();
        var bots = new List<TestBot> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0) };
        scheduler.Assign(bots, b => PlayerbotThinkScheduler<TestBot>.MapKey(b.MapId, b.InstanceId));
        ulong key = PlayerbotThinkScheduler<TestBot>.MapKey(0, 0);
        for (int tick = 0; tick < 3; tick++)
        {
            // The time since the tick began is always past the share: only the first bot of the rotation is visited.
            Assert.Equal(1, scheduler.ThinkMap(key, 1, bot => Think(bot, 1), () => 5));
        }

        Assert.All(bots, b => Assert.Equal(1, b.Thinks));
    }
}
