using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Out-of-season holiday quests (the wave-8 rehearsal: bots took and chased "Dearest Colara," 8898 and "Winter's Presents" 8827 in
/// October). A quest listed in <c>game_event_quest</c> is active only while its event runs (the server's event state, vmangos
/// GameEventMgr), and an event's own givers and enders (<c>game_event_creature</c>) are in the world only then. A bot neither takes
/// such a quest nor walks to turn it in, or to an ender who is away, while the event is off; it does both once the event runs.
/// End to end on the scenario world: the real quest handlers, the game-event feature with its spawn gate and quest state, the
/// manual clock.
/// </summary>
public sealed class PlayerbotEventQuestTests
{
    private const ushort WinterVeil = 2;
    private const uint HeraldEntry = 990910, HeraldSpawn = 990910;
    private const uint EnderEntry = 990911, EnderSpawn = 990911;
    private const uint WinterEnderEntry = 990912, WinterEnderSpawn = 990912;

    /// <summary>Listed in game_event_quest under Winter Veil (like 8827); ended by an ordinary, always-present ender.</summary>
    private const uint EventQuest = 990901;

    /// <summary>Not listed (like 8898), but ended only by a creature that is spawned while Winter Veil runs (like Colara Dean).</summary>
    private const uint SeasonalEnderQuest = 990902;

    /// <summary>Listed under Winter Veil, asking for a kill of <see cref="WolfEntry"/> (300 yards off, so it is not done at once).</summary>
    private const uint EventKillQuest = 990903;
    private const uint WolfEntry = 990913, WolfSpawn = 990913;

    [Fact]
    public async Task AnOutOfSeasonQuest_IsNeitherTakenNorTurnedIn_UntilItsEventRuns()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            var content = new HolidayContent();
            services.AddSingleton<ICreatureDataStore>(content);
            services.AddSingleton<IQuestContentStore>(content);
            services.AddSingleton<IGameEventDataStore>(content);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(new PlayerbotOptions
            {
                Enabled = true, MaxBots = 4, AllowedMaps = [0, 1], ThinkIntervalMs = 200, MaxActionsPerTick = 16,
            }));
        });
        await world.UseFlatFloorAsync();
        await world.Host.OnWorldAsync(() =>
        {
            Game.Maps.Collision.WorldCollision.Of(world.Host.World).Install(pathfinder: new OpenPathfinder());
            return true;
        });
        GameEventFeature events = world.Services.GetRequiredService<GameEventFeature>();
        await world.Host.World.AdvanceClockAsync(1_000); // the event system initialises on the first ticks: Winter Veil is not running
        Assert.False(await world.Host.OnWorldAsync(() => events.IsActiveEvent(WinterVeil)));

        PlayerbotOperationResult created = await world.Bots.CreateAsync("Seasonbot", 1, 1);
        Assert.True(created.Success, created.Code);
        PlayerbotOperationResult started = await world.Bots.StartAsync("Seasonbot");
        Assert.True(started.Success, started.Code);
        Guid bot = started.BotId!.Value;
        QuestNpcServices quests = world.Services.GetRequiredService<QuestNpcFeature>().Services;

        // Off season the event quest is not offered (the server's CanTakeQuest), the other one is taken (complete at once, a talk-to
        // quest), and its ender, a Winter Veil spawn, is away: the bot does not walk to where it would stand.
        Assert.True(await RunUntilAsync(world, bot, 60_000, () => Status(SeasonalEnderQuest) is not null), "the ordinary quest was not taken");
        Assert.False(await ChasesAsync(world, bot, 60_000, EventQuest, SeasonalEnderQuest), "the bot walked to an ender who is away off season");
        Assert.Null(await world.Host.OnWorldAsync(() => Status(EventQuest)));

        // The event runs: the bot takes both event quests. It stops before the bot turned them in: the bot does not walk to the
        // ender (who would refuse an inactive quest, vmangos PrepareQuestMenu), nor hunt the inactive quest's objective.
        await world.Host.OnWorldAsync(() => events.Service!.StartEvent(WinterVeil, overwrite: true));
        Assert.True(await RunUntilAsync(world, bot, 240_000, () => Status(EventQuest) is not null && Status(EventKillQuest) is not null),
            await world.Host.OnWorldAsync(() => "not taken in season: " + Describe(world, bot, Status(EventQuest), Status(SeasonalEnderQuest))));
        await world.Host.OnWorldAsync(() => events.Service!.StopEvent(WinterVeil, overwrite: true));
        Assert.False(await world.Host.OnWorldAsync(() => Status(EventQuest)?.Rewarded == true));
        Assert.False(await ChasesAsync(world, bot, 60_000, EventQuest, Status(SeasonalEnderQuest)?.Rewarded == true ? 0 : SeasonalEnderQuest),
            "the bot walked to turn in an out-of-season quest");
        Assert.False(await HuntsAsync(world, bot, 30_000), "the bot hunted the objective of an out-of-season quest");
        Assert.False(await world.Host.OnWorldAsync(() => Status(EventQuest)?.Rewarded == true));

        // In season again: both are turned in.
        await world.Host.OnWorldAsync(() => events.Service!.StartEvent(WinterVeil, overwrite: true));
        Assert.True(await RunUntilAsync(world, bot, 240_000,
            () => Status(EventQuest)?.Rewarded == true && Status(SeasonalEnderQuest)?.Rewarded == true),
            await world.Host.OnWorldAsync(() => Describe(world, bot, Status(EventQuest), Status(SeasonalEnderQuest))));

        QuestStatusData? Status(uint quest) => quests.StateOf(world.Bots.FindSession(bot)!.Player!) is { Loaded: true } state ? state.Quests.Get(quest) : null;
    }

    /// <summary>
    /// 200 ms of world time, then the bot's pending quest settlement (the reward is written to the character database in the
    /// background; the manual clock would otherwise run past the bot's exchange deadline while the write is still on its way).
    /// </summary>
    private static async Task StepAsync(ScenarioTestWorld world, Guid bot)
    {
        await world.Host.World.AdvanceClockAsync(200);
        int character = await world.Host.OnWorldAsync(() => (int)world.Bots.FindSession(bot)!.Player!.Guid.Low);
        await world.Services.GetRequiredService<QuestNpcFeature>().WaitForSettlementAsync(character);
    }

    private static async Task<bool> RunUntilAsync(ScenarioTestWorld world, Guid bot, uint ms, Func<bool> condition)
    {
        for (uint elapsed = 0; elapsed < ms; elapsed += 200)
        {
            await StepAsync(world, bot);
            if (await world.Host.OnWorldAsync(condition)) return true;
        }

        return false;
    }

    /// <summary>Whether, within <paramref name="ms"/>, the bot's quest objective to hunt was the out-of-season quest's wolf.</summary>
    private static async Task<bool> HuntsAsync(ScenarioTestWorld world, Guid bot, uint ms)
    {
        for (uint elapsed = 0; elapsed < ms; elapsed += 200)
        {
            await StepAsync(world, bot);
            if (await world.Host.OnWorldAsync(() => world.Bots.FindBrain(bot)?.QuestGoals.PreferredCreatureEntry == WolfEntry)) return true;
        }

        return false;
    }

    /// <summary>Whether, within <paramref name="ms"/>, the bot's goal was to walk to one of the enders for one of <paramref name="quests"/>.</summary>
    private static async Task<bool> ChasesAsync(ScenarioTestWorld world, Guid bot, uint ms, params uint[] quests)
    {
        for (uint elapsed = 0; elapsed < ms; elapsed += 200)
        {
            await StepAsync(world, bot);
            if (await world.Host.OnWorldAsync(() => world.Bots.FindBrain(bot) is { } brain && brain.TargetEntry is EnderEntry or WinterEnderEntry
                    && quests.Contains(brain.QuestId)))
                return true;
        }

        return false;
    }

    private static string Describe(ScenarioTestWorld world, Guid bot, QuestStatusData? eventQuest, QuestStatusData? seasonal)
    {
        var player = world.Bots.FindSession(bot)!.Player!;
        PlayerbotBrain? brain = world.Bots.FindBrain(bot);
        bool winterThere = player.Map!.FindUpdater<Game.Creatures.CreatureMapSystem>()?.SpawnGate?.AllowsCreature(WinterEnderSpawn) != false;
        return $"the quests were not turned in in season: event {eventQuest?.Status}/{eventQuest?.Rewarded} seasonal {seasonal?.Status}/{seasonal?.Rewarded} "
            + $"goal {brain?.Goal} target {brain?.TargetEntry} quest {brain?.QuestId} at ({player.X:F1}, {player.Y:F1}) start ({StartX}, {StartY}) "
            + $"stall {brain?.StallReport} suspended q1 {brain?.Suspensions.IsQuestSuspended(EventQuest, world.Host.World.NowMs)} q2 {brain?.Suspensions.IsQuestSuspended(SeasonalEnderQuest, world.Host.World.NowMs)} herald {brain?.Suspensions.IsEntrySuspended(HeraldEntry, world.Host.World.NowMs)} winter {brain?.Suspensions.IsEntrySuspended(WinterEnderEntry, world.Host.World.NowMs)} winter ender allowed {winterThere} visible {string.Join(",", player.VisibleObjects.Select(g => (player.Map.FindObject(g) as Game.Creatures.Creature)?.Entry).OfType<uint>())}";
    }

    /// <summary>A herald beside the start offering both quests, an ender 40 yards east (always there) and a Winter Veil ender 40 yards west.</summary>
    private sealed class HolidayContent : ICreatureDataStore, IQuestContentStore, IGameEventDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [Npc(HeraldEntry, "Holiday Herald"), Npc(EnderEntry, "Holiday Steward"), Npc(WinterEnderEntry, "Winter Steward"),
                new CreatureTemplate { Entry = WolfEntry, Name = "Winter Wolf", Faction = 14, CreatureType = 1, MinLevel = 1, MaxLevel = 1,
                    DisplayIds = [903], MinLevelHealth = 50, MaxLevelHealth = 50, MinMeleeDamage = 1, MaxMeleeDamage = 2, MeleeBaseAttackTime = 2000 }],
            [
                new CreatureSpawn { Guid = HeraldSpawn, Entry = HeraldEntry, MapId = 0, X = StartX + 3f, Y = StartY, Z = StartZ, Orientation = MathF.PI },
                new CreatureSpawn { Guid = EnderSpawn, Entry = EnderEntry, MapId = 0, X = StartX + 40f, Y = StartY, Z = StartZ },
                new CreatureSpawn { Guid = WinterEnderSpawn, Entry = WinterEnderEntry, MapId = 0, X = StartX - 40f, Y = StartY, Z = StartZ },
                new CreatureSpawn { Guid = WolfSpawn, Entry = WolfEntry, MapId = 0, X = StartX, Y = StartY - 300f, Z = StartZ },
            ], [], [], []));

        Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new QuestContent(
            [Quest(EventQuest, "Holiday Errand"), Quest(SeasonalEnderQuest, "Note for the Winter Steward"),
                Quest(EventKillQuest, "Winter Wolf Pelts", WolfEntry)],
            [new CreatureQuestRelation { Id = HeraldEntry, Quest = EventQuest }, new CreatureQuestRelation { Id = HeraldEntry, Quest = SeasonalEnderQuest },
                new CreatureQuestRelation { Id = HeraldEntry, Quest = EventKillQuest }],
            [new CreatureQuestRelation { Id = EnderEntry, Quest = EventQuest }, new CreatureQuestRelation { Id = WinterEnderEntry, Quest = SeasonalEnderQuest },
                new CreatureQuestRelation { Id = EnderEntry, Quest = EventKillQuest }]));

        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GameEventContent(
            [new GameEventRecord(WinterVeil, 1, 525600, 27360, 141, 0, "Feast of Winter Veil")],
            [new GameEventTimeRecord(WinterVeil, "2040-12-16 23:00:00", "2045-12-31 22:59:59")], // not in season now
            [new GameEventSpawnRecord(WinterEnderSpawn, WinterVeil)],
            [], [],
            [new GameEventQuestRecord(EventQuest, WinterVeil), new GameEventQuestRecord(EventKillQuest, WinterVeil)],
            []));

        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static CreatureTemplate Npc(uint entry, string name) => new()
        {
            Entry = entry, Name = name, Faction = 12, NpcFlags = (uint)NpcFlags.QuestGiver, DisplayIds = [49],
            MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
        };

        private static QuestTemplate Quest(uint id, string title, uint kill = 0) => new()
        {
            Entry = id, Method = 2, MinLevel = 1, QuestLevel = 1, RequiredRaces = 0xFF, Title = title, RewXP = 50,
            ReqCreatureOrGOId1 = (int)kill, ReqCreatureOrGOCount1 = kill == 0 ? 0u : 1u,
        };
    }
}
