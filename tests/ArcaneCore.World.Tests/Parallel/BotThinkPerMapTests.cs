using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Parallel;

/// <summary>
/// Bot think per map (World:Parallel:BotThinkPerMap, default off; docs/research/map-local-stages-design-20261024.md, section 5.7). The
/// feature test runs the real <see cref="ManagedPlayerbotFeature"/> in a seeded two-continent world with the option off and on, at one and
/// at two map threads, and compares what the bots did. The scheduler tests pin the rotation (per-map cursors, bots changing maps) and the
/// action and time shares. The gate tests pin that the stage never runs outside the serial gate.
/// </summary>
public sealed class BotThinkPerMapTests
{
    private sealed class TestBot(int index)
    {
        public readonly int Index = index;

        /// <summary>Ascending with the index, so the list order is the id order the rotation re-anchors by.</summary>
        public readonly Guid Id = new(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static readonly Func<TestBot, Guid> IdOf = static bot => bot.Id;

    private struct TestDriver(TestBot[] bots, List<int> order, Func<double> elapsedMs) : PlayerbotThinkRotation.IThinkLaneDriver
    {
        public PlayerbotThinkRotation.ThinkLane Classify(int index) => PlayerbotThinkRotation.ThinkLane.Normal;

        public bool Visit(int index, PlayerbotThinkRotation.ThinkLane lane)
        {
            order.Add(bots[index].Index);
            return true;
        }

        public double ElapsedMs() => elapsedMs();
    }

    private static ulong Key(uint mapId, uint instanceId = 0) => PlayerbotThinkScheduler<TestBot>.MapKey(mapId, instanceId);

    /// <summary>One tick of one map over a clock that is always past the map's share: only the first bot of the rotation is thought for.</summary>
    private static int OneThink(PlayerbotThinkScheduler<TestBot> scheduler, ulong key, List<int> order)
    {
        var driver = new TestDriver(scheduler.BotsOf(key), order, () => 1000);
        return scheduler.ThinkMap(key, 8, 0.7, 1, ref driver, out _);
    }

    private static PlayerbotThinkScheduler<TestBot> Scheduler() => new(IdOf);

    private static void Assign(PlayerbotThinkScheduler<TestBot> scheduler, List<TestBot> bots, Dictionary<int, uint> mapOfBot)
        => scheduler.Assign(bots, bot => Key(mapOfBot[bot.Index]));

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
    public void Scheduler_GroupsByMap_KeepingOrder_AndDropsEmptyMaps()
    {
        PlayerbotThinkScheduler<TestBot> scheduler = Scheduler();
        var bots = new List<TestBot> { new(0), new(1), new(2), new(3) };
        var maps = new Dictionary<int, uint> { [0] = 0, [1] = 1, [2] = 0, [3] = 33 };
        Assign(scheduler, bots, maps);
        Assert.Equal(4, scheduler.TotalBots);
        Assert.Equal(3, scheduler.MapCount);
        Assert.Equal(new[] { 0, 2 }, scheduler.BotsOf(Key(0)).Select(b => b.Index).ToArray());

        Assign(scheduler, bots.Where(b => b.Index == 1).ToList(), maps);
        Assert.Equal(1, scheduler.MapCount);
        Assert.Empty(scheduler.BotsOf(Key(0)));
        var order = new List<int>();
        Assert.Equal(0, OneThink(scheduler, Key(0), order));
        Assert.Empty(order);
    }

    [Fact]
    public void Scheduler_KeepsTheArrayWhileAMapIsUnchanged_AndReplacesItWhenItChanges()
    {
        PlayerbotThinkScheduler<TestBot> scheduler = Scheduler();
        var bots = new List<TestBot> { new(0), new(1), new(2) };
        var maps = new Dictionary<int, uint> { [0] = 0, [1] = 0, [2] = 1 };
        Assign(scheduler, bots, maps);
        TestBot[] first = scheduler.BotsOf(Key(0));
        Assign(scheduler, bots, maps);
        Assert.Same(first, scheduler.BotsOf(Key(0)));

        maps[2] = 0;
        Assign(scheduler, bots, maps);
        Assert.NotSame(first, scheduler.BotsOf(Key(0)));
        Assert.Equal(new[] { 0, 1, 2 }, scheduler.BotsOf(Key(0)).Select(b => b.Index).ToArray());
    }

    [Fact]
    public void Scheduler_SplitsTheTimeBudgetByHeadCount()
    {
        PlayerbotThinkScheduler<TestBot> scheduler = Scheduler();
        var bots = new List<TestBot> { new(0), new(1), new(2), new(3) };
        var maps = new Dictionary<int, uint> { [0] = 0, [1] = 0, [2] = 0, [3] = 1 };
        Assign(scheduler, bots, maps);
        Assert.Equal(6.0, scheduler.ShareMs(Key(0), 8), 9);
        Assert.Equal(2.0, scheduler.ShareMs(Key(1), 8), 9);
        Assert.Equal(0.0, scheduler.ShareMs(Key(9), 8), 9);
    }

    [Theory]
    [InlineData(3, 2, 4, 4, 3, 2)] // every bot on one map: the totals, as the serial pass has them
    [InlineData(3, 2, 2, 4, 1, 1)] // half the bots: half of each, rounded down, the main lane keeps its one action
    [InlineData(3, 2, 1, 4, 0, 1)]
    [InlineData(8, 8, 3, 6, 4, 4)]
    [InlineData(0, 1, 1, 5, 0, 1)] // the first lane idle (MaxActionsPerTick 1): the main lane keeps its one
    public void ActionsShare_GivesEachMapItsHeadCountShareOfBothBudgets(int combatTotal, int reserveTotal, int mapBots, int totalBots, int combat, int reserve)
        => Assert.Equal((combat, reserve), PlayerbotThinkScheduler<TestBot>.ActionsShare(combatTotal, reserveTotal, mapBots, totalBots));

    [Fact]
    public void ActionsShare_NeverGivesAMapLessThanOneMainLaneAction_AndNeverMoreThanTheTotalsToAllMapsTogetherPlusTheFloor()
    {
        foreach (int total in new[] { 1, 2, 5, 17 })
        {
            foreach (int maps in new[] { 1, 2, 3, 7 })
            {
                int bots = total * maps;
                int combatSum = 0;
                for (int m = 0; m < maps; m++)
                {
                    (int combat, int reserve) = PlayerbotThinkScheduler<TestBot>.ActionsShare(total, total, total, bots);
                    Assert.True(reserve >= 1);
                    combatSum += combat;
                }

                Assert.True(combatSum <= total, $"{maps} maps of {total} bots shared {combatSum} combat actions of {total}");
            }
        }
    }

    [Fact]
    public void ARotatingMap_UnderASmallBudget_ThinksEveryBotInTurn_AsTheSerialPassDoes()
    {
        PlayerbotThinkScheduler<TestBot> scheduler = Scheduler();
        var bots = new List<TestBot> { new(0), new(1), new(2) };
        var maps = new Dictionary<int, uint> { [0] = 0, [1] = 0, [2] = 0 };
        Assign(scheduler, bots, maps);
        var order = new List<int>();
        for (int tick = 0; tick < 6; tick++)
        {
            Assert.Equal(1, OneThink(scheduler, Key(0), order));
            Assign(scheduler, bots, maps); // the feature regroups every tick: an unchanged map keeps its rotation
        }

        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2 }, order);
    }

    [Fact]
    public void ABotLeavingAMap_BetweenTicks_DoesNotMakeTheNextBotOfThatMapWaitAWholePass()
    {
        // Map 0 holds bots 1..4 (ids ascending). One think a tick: 1, 2. Then bot 1 moves to map 1: the cursor was at index 2, which in the
        // shortened list [2, 3, 4] is bot 4. The rotation re-anchors by id, so bot 3 goes next and nobody is skipped.
        PlayerbotThinkScheduler<TestBot> scheduler = Scheduler();
        var bots = new List<TestBot> { new(1), new(2), new(3), new(4) };
        var maps = new Dictionary<int, uint> { [1] = 0, [2] = 0, [3] = 0, [4] = 0 };
        Assign(scheduler, bots, maps);
        var order = new List<int>();
        OneThink(scheduler, Key(0), order);
        Assign(scheduler, bots, maps);
        OneThink(scheduler, Key(0), order);
        Assert.Equal(new[] { 1, 2 }, order);

        maps[1] = 1;
        Assign(scheduler, bots, maps);
        var mapZero = new List<int>();
        for (int tick = 0; tick < 3; tick++)
        {
            OneThink(scheduler, Key(0), mapZero);
            Assign(scheduler, bots, maps);
        }

        Assert.Equal(new[] { 3, 4, 2 }, mapZero);
        var mapOne = new List<int>();
        OneThink(scheduler, Key(1), mapOne);
        Assert.Equal(new[] { 1 }, mapOne);
    }

    [Fact]
    public void ABotJoiningAMap_BetweenTicks_DoesNotThinkTwiceInARow()
    {
        // Bots 2..4 on map 0, one think a tick: 2, 3. Bot 1 (the lowest id) arrives from map 1: the cursor index would point at bot 3 again.
        // Re-anchored by id, bot 4 is next.
        PlayerbotThinkScheduler<TestBot> scheduler = Scheduler();
        var bots = new List<TestBot> { new(1), new(2), new(3), new(4) };
        var maps = new Dictionary<int, uint> { [1] = 1, [2] = 0, [3] = 0, [4] = 0 };
        Assign(scheduler, bots, maps);
        var order = new List<int>();
        OneThink(scheduler, Key(0), order);
        Assign(scheduler, bots, maps);
        OneThink(scheduler, Key(0), order);
        Assert.Equal(new[] { 2, 3 }, order);

        maps[1] = 0;
        Assign(scheduler, bots, maps);
        var after = new List<int>();
        for (int tick = 0; tick < 4; tick++)
        {
            OneThink(scheduler, Key(0), after);
            Assign(scheduler, bots, maps);
        }

        Assert.Equal(new[] { 4, 1, 2, 3 }, after);
    }

    [Fact]
    public void AMapWithNoThink_IsCollectedForTheWorldThreadsPass_AndEveryServedMapIsNot()
    {
        PlayerbotThinkScheduler<TestBot> scheduler = Scheduler();
        var bots = new List<TestBot> { new(0), new(1), new(2) };
        var maps = new Dictionary<int, uint> { [0] = 0, [1] = 1, [2] = 1 };
        Assign(scheduler, bots, maps);
        var order = new List<int>();
        OneThink(scheduler, Key(0), order);
        var unthought = new List<ulong>();
        scheduler.CollectUnthought(unthought);
        Assert.Equal(new[] { Key(1) }, unthought);

        OneThink(scheduler, Key(1), order);
        scheduler.CollectUnthought(unthought);
        Assert.Empty(unthought);

        Assign(scheduler, bots, maps); // a new pass: nobody is served yet
        scheduler.CollectUnthought(unthought);
        Assert.Equal(2, unthought.Count);
    }

    [Fact]
    public void WithRoomForEveryone_AMapThinksAllItsBotsEachTick()
    {
        PlayerbotThinkScheduler<TestBot> scheduler = Scheduler();
        var bots = Enumerable.Range(0, 5).Select(i => new TestBot(i)).ToList();
        var maps = bots.ToDictionary(b => b.Index, _ => 0u);
        Assign(scheduler, bots, maps);
        var order = new List<int>();
        var driver = new TestDriver(scheduler.BotsOf(Key(0)), order, () => 0);
        Assert.Equal(5, scheduler.ThinkMap(Key(0), double.PositiveInfinity, 0.7, 1, ref driver, out bool timeLimited));
        Assert.False(timeLimited);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
    }

    // ---- The stage and the gate -----------------------------------------------------------------------------------------------

    private sealed class CountingThinker : IMapBotThinker
    {
        public int Calls;

        public void ThinkBots(Map map, uint diffMs) => Calls++;
    }

    private sealed class GatedProbeUpdater : IMapUpdater
    {
        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private static WorldRuntime CreateRuntime(int threads, bool botThinkPerMap)
        => new(
            new WorldRuntimeOptions
            {
                UpdateCompressionThreshold = 0,
                AutosaveIntervalMs = 0,
                CommandTimeBudgetMs = 0,
                Parallel = new ParallelMapOptions { MapUpdateThreads = threads, BotThinkPerMap = botThinkPerMap },
            },
            new RecordingSaveQueue(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldRuntime>.Instance);

    [Fact]
    public void OptionOff_AttachesNothing_AndADisabledStageDoesNotThink()
    {
        using WorldRuntime world = CreateRuntime(1, botThinkPerMap: false);
        Map map = world.GetMap(0, 0);
        int before = map.Updaters.Count;
        var thinker = new CountingThinker();
        Assert.Null(MapBotThinkStage.AttachIfEnabled(map, world.Options.Parallel, thinker));
        Assert.Equal(before, map.Updaters.Count);

        map.AddUpdater(new MapBotThinkStage(thinker, enabled: false));
        world.RunTick(50);
        Assert.Equal(0, thinker.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OptionOn_EveryMapThinksOncePerTick_OnItsOwnUpdate(int threads)
    {
        using WorldRuntime world = CreateRuntime(threads, botThinkPerMap: true);
        var thinker = new CountingThinker();
        (uint, uint)[] maps = [(0, 0), (1, 0), (33, 1), (489, 2)];
        foreach ((uint mapId, uint instanceId) in maps)
        {
            Assert.NotNull(MapBotThinkStage.AttachIfEnabled(world.GetMap(mapId, instanceId), world.Options.Parallel, thinker));
        }

        for (int tick = 0; tick < 5; tick++) world.RunTick(50);
        Assert.Equal(5 * maps.Length, thinker.Calls);
    }

    [Fact]
    public void TheStage_StaysInsideTheSerialGate_WhateverUngatedSays_WhileAnotherUpdaterCanBeUngated()
    {
        var options = new ParallelMapOptions { Ungated = "MapBotThinkStage,GatedProbeUpdater" };
        var gates = new MapStageGates(options);
        Assert.True(gates.IsGated(new MapBotThinkStage(new CountingThinker(), enabled: true)));
        Assert.False(gates.IsGated(new GatedProbeUpdater())); // the control: the override still works for any other unclassified updater
        Assert.Contains("stays gated", gates.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStage_IsAFileUnderTheBotsSubsystem_OfThePerMapBreakdown()
        => Assert.Equal("bots (MapBotThinkStage)", MapUpdateDiagnostics.SubsystemOf(new MapBotThinkStage(new CountingThinker(), enabled: true)));

    // ---- The real feature ------------------------------------------------------------------------------------------------------

    /// <summary>A scripted bot that acts on every think and logs what the world shows afterwards. World thread or the map thread that owns it, one at a time.</summary>
    private sealed class Recorder : IPlayerbotController
    {
        public readonly List<string> Log = [];
        public volatile bool Armed;
        private int _thinks;

        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            if (!Armed)
            {
                // Setup: only answer the server's teleport orders, as a client does.
                context.AcknowledgeServerOrders();
                return;
            }

            int n = ++_thinks;
            uint state = n % 2 == 0 ? (uint)StandState.Stand : (uint)StandState.Sit;
            bool acted = context.TryAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(state));
            Player? player = context.Player;
            Log.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{n}:{acted}:{player?.StandState}:{player?.Map?.MapId}:{player?.X:F2}:{player?.Y:F2}"));
        }

        public void Detached(Guid botId)
        {
        }
    }

    private static readonly string[] BotNames = ["Mapthinka", "Mapthinkb", "Mapthinkc", "Mapthinkd", "Mapthinke", "Mapthinkf"];

    /// <summary>Races of the six bots (the test world's valid warriors: human, orc, dwarf, undead). They all start on map 0; the odd ones are then sent to map 1.</summary>
    private static readonly byte[] BotRaces = [1, 2, 3, 5, 1, 2];

    private sealed record Outcome(Dictionary<string, List<string>> Logs, int MapThinks, int FallbackThinks, bool MapMode, int[] StagesPerMap, int MapsWithBots);

    private static async Task<Outcome> RunAsync(bool botThinkPerMap, int threads, int steps)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = WorldTestHost.Start(
            clock: TestClock.Manual(),
            configure: options => options.Parallel = new ParallelMapOptions { MapUpdateThreads = threads, BotThinkPerMap = botThinkPerMap },
            configureServices: services =>
            {
                var playerbots = new PlayerbotOptions
                {
                    Enabled = true, MaxBots = 8, ThinkIntervalMs = 100, MaxActionsPerTick = 8, MaxPathPoints = 32, MaxRouteYards = 100, AllowedMaps = [0, 1],
                };
                services.AddSingleton<IAccountStore>(accounts);
                services.AddSingleton<IAccountAdmin>(accounts);
                services.AddSingleton<ICharacterStore>(characters);
                services.AddSingleton<ICharacterLifeStore>(characters);
                services.AddSingleton<IManagedPlayerbotStore>(owners);
                services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(playerbots));
                services.AddSingleton<IManagedPlayerbotProvisionStore>(new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts));
            });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        feature.ThinkBudgetMs = double.PositiveInfinity; // manual clock: no wall-time think budget
        await feature.StartupAsync(default);
        var recorders = new Dictionary<string, Recorder>();
        for (int i = 0; i < BotNames.Length; i++)
        {
            Guid id = Assert.IsType<Guid>((await feature.CreateAsync(BotNames[i], BotRaces[i], 1)).BotId);
            var recorder = new Recorder();
            recorders[BotNames[i]] = recorder;
            Assert.True((await feature.StartScriptedAsync(id.ToString(), recorder)).Success);
        }

        // Send the odd bots to map 1 (a far teleport; the recorders acknowledge it), until they all stand there.
        TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
        Player? Find(string name) => host.World.FindOnlinePlayer(name);
        Assert.True(await host.World.InvokeAsync(() =>
        {
            bool all = true;
            for (int i = 1; i < BotNames.Length; i += 2)
            {
                Player player = Find(BotNames[i])!;
                all &= teleports.TeleportTo(player, 1, player.X, player.Y, player.Z, 0);
            }

            return all;
        }), "a teleport was refused");
        bool arrived = false;
        for (int tick = 0; tick < 200 && !arrived; tick++)
        {
            await host.StepAsync(1);
            arrived = await host.World.InvokeAsync(() =>
            {
                for (int i = 0; i < BotNames.Length; i++)
                {
                    Player player = Find(BotNames[i])!;
                    uint expected = i % 2 == 1 ? 1u : 0u;
                    if (!player.IsInWorld || teleports.StageOf(player) is not null || player.MapId != expected) return false;
                }

                return true;
            });
        }

        Assert.True(arrived, "the bots did not all arrive on their maps");
        (int mapBefore, int fallbackBefore) = await host.World.InvokeAsync(() =>
        {
            foreach (Recorder recorder in recorders.Values) recorder.Armed = true;
            return (feature.MapThinkVisits, feature.FallbackThinkVisits);
        });
        await host.StepAsync(steps);
        Outcome outcome = await host.World.InvokeAsync(() => new Outcome(
            recorders.ToDictionary(pair => pair.Key, pair => new List<string>(pair.Value.Log)),
            feature.MapThinkVisits - mapBefore,
            feature.FallbackThinkVisits - fallbackBefore,
            feature.MapThinkMode,
            host.World.Maps.Select(map => map.Updaters.OfType<MapBotThinkStage>().Count()).ToArray(),
            host.World.Maps.Count(map => map.PlayerCount > 0)));
        await feature.ShutdownBeforeWorldStopAsync();
        return outcome;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TheBots_DoTheSame_WithTheOptionOnAndOff_InASeededMultiMapWorld(int threads)
    {
        const int steps = 14;
        Outcome off = await RunAsync(botThinkPerMap: false, threads, steps);
        Outcome on = await RunAsync(botThinkPerMap: true, threads, steps);

        // The control: the option is what changed. Off thinks in the feature's own pass; on thinks inside the maps, none left to the fallback
        // once every bot stands on a map.
        Assert.False(off.MapMode);
        Assert.Equal(0, off.MapThinks);
        Assert.All(off.StagesPerMap, count => Assert.Equal(0, count));
        Assert.True(on.MapMode);
        Assert.True(on.MapThinks > 0, "no think ran inside a map's update");
        Assert.Equal(0, on.FallbackThinks);
        Assert.All(on.StagesPerMap, count => Assert.Equal(1, count));
        Assert.True(on.MapsWithBots >= 2, $"the bots stood on {on.MapsWithBots} map(s); the test needs a multi-map world");
        Assert.True(off.MapsWithBots >= 2);

        // Every bot thought, acted and was observed on the same sequence: a bot's log is a function of its own think number.
        Assert.Equal(BotNames.Length, on.Logs.Count);
        foreach (string name in BotNames)
        {
            List<string> expected = off.Logs[name];
            List<string> actual = on.Logs[name];
            int common = Math.Min(expected.Count, actual.Count);
            Assert.True(common >= steps / 2, $"{name} thought {expected.Count} times off and {actual.Count} on");
            Assert.Equal(expected.Take(common), actual.Take(common));
            Assert.Contains(actual, line => line.Contains(":True:", StringComparison.Ordinal));
        }
    }
}
