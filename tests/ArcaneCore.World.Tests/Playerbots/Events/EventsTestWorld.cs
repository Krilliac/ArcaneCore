using ArcaneCore.World.Bots;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.GridTerrain;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>One festival NPC of <see cref="FestivalContent"/>: where it stands (relative to the start), what it offers and how it is flagged.</summary>
internal sealed record FestivalNpc(uint Entry, string Name, NpcFlags Flags, float Dx, float Dy, float Dz = 0f, uint UnitFlags = 0,
    CreatureBehaviourFlags Behaviour = CreatureBehaviourFlags.None, bool EventBound = true, ushort Event = EventsTestWorld.WinterVeil);

/// <summary>
/// Synthetic festival content around the scenario start (-8949.95, -132.49, 83.53, map 0): Winter Veil (event 2, holiday 141) and
/// the hourly bells (event 1024, holiday 0), neither running at the start, and the festival NPCs of a test. An event-bound NPC is spawned
/// only while its event runs (a positive <c>game_event_creature</c> row).
/// </summary>
internal sealed class FestivalContent(IReadOnlyList<FestivalNpc> npcs) : ICreatureDataStore, IQuestContentStore, IGameEventDataStore
{
    public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new CreatureContent(
            [.. npcs.Select(Template)],
            [.. npcs.Select(npc => new CreatureSpawn { Guid = npc.Entry, Entry = npc.Entry, MapId = 0, X = StartX + npc.Dx, Y = StartY + npc.Dy, Z = StartZ + npc.Dz })],
            [], [], []));

    Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new QuestContent([], [], []));

    Task<GameEventContent> IGameEventDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new GameEventContent(
        [
            new GameEventRecord(EventsTestWorld.WinterVeil, 1, 525_600, 27_360, EventsTestWorld.WinterVeilHoliday, 0, "Feast of Winter Veil"),
            new GameEventRecord(EventsTestWorld.HourlyBells, 1, 60, 1, 0, 0, "Hourly Bells"),
            new GameEventRecord(EventsTestWorld.Hallows, 1, 525_600, 21_600, 324, 0, "Hallow's End"),
            new GameEventRecord(EventsTestWorld.WickermanEmbers, 1, 1_440, 60, 0, EventsTestWorld.Hallows, "Wickerman Ember's"),
        ],
        [
            new GameEventTimeRecord(EventsTestWorld.WinterVeil, "2040-12-16 23:00:00", "2045-12-31 22:59:59"), // not in season now
            new GameEventTimeRecord(EventsTestWorld.HourlyBells, "2040-01-01 00:00:00", "2045-12-31 22:59:59"),
            new GameEventTimeRecord(EventsTestWorld.Hallows, "2040-10-18 23:00:00", "2045-11-01 22:59:59"),
            new GameEventTimeRecord(EventsTestWorld.WickermanEmbers, "2040-01-01 00:00:00", "2045-12-31 22:59:59"),
        ],
        [.. npcs.Where(npc => npc.EventBound).Select(npc => new GameEventSpawnRecord(npc.Entry, npc.Event))],
        [], [], [], []));

    public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static CreatureTemplate Template(FestivalNpc npc) => new()
    {
        Entry = npc.Entry, Name = npc.Name, Faction = 12, NpcFlags = (uint)npc.Flags, UnitFlags = npc.UnitFlags, DisplayIds = [49],
        MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
    };
}

/// <summary>
/// The world of the P7a world tests: the scenario world (real handlers, manual clock, real quest and game-event features) with
/// <see cref="FestivalContent"/>, a flat floor with open paths, and capital-homed bots standing at their inn (the start). A wait steps
/// the manual clock 200 ms at a time and then waits for quest settlement, as <c>PlayerbotEventQuestTests</c> does; every wait keeps its
/// own game-time budget.
/// </summary>
internal sealed class EventsTestWorld : IAsyncDisposable
{
    internal const ushort WinterVeil = 2, HourlyBells = 1024, Hallows = 12, WickermanEmbers = 24;
    internal const uint WinterVeilHoliday = 141;

    /// <summary>A town home of 300 yards around the start: the inn, where an idle bot of a capital stays when no festival runs.</summary>
    internal static readonly Vector3 Inn = new(StartX, StartY, StartZ);

    private EventsTestWorld(ScenarioTestWorld world, PlayerbotOptions options)
    {
        World = world;
        Options = options;
    }

    internal ScenarioTestWorld World { get; }

    internal PlayerbotOptions Options { get; }

    internal GameEventFeature Events => World.Services.GetRequiredService<GameEventFeature>();

    internal static async Task<EventsTestWorld> StartAsync(IReadOnlyList<FestivalNpc> npcs, Action<PlayerbotOptions>? configure = null, int maxBots = 16)
    {
        var content = new FestivalContent(npcs);
        var options = new PlayerbotOptions { Enabled = true, MaxBots = maxBots, AllowedMaps = [0, 1], ThinkIntervalMs = 200, MaxActionsPerTick = 16 };
        configure?.Invoke(options);
        ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            services.AddSingleton<ICreatureDataStore>(content);
            services.AddSingleton<IQuestContentStore>(content);
            services.AddSingleton<IGameEventDataStore>(content);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Microsoft.Extensions.Options.Options.Create(options));
        });
        await world.UseFlatFloorAsync();
        // The map fixture of the teleport tests puts a "Test shortcut" trigger (a teleport to the capital) 10 yards east of the start, on the line most
        // festival walks take: a bot crossing it was sent 690 yards away and walked back. No trigger belongs in these worlds.
        await world.Host.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(world.Host.World);
            maps.ReplaceAreaTriggerTables(maps.BuildAreaTriggerTables(InMemoryMapDataStore.Content with { AreaTriggers = [], AreaTriggerTeleports = [] }));
            return true;
        });
        await world.Host.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.Host.World).Install(pathfinder: new OpenPathfinder());
            return true;
        });
        await world.Host.World.AdvanceClockAsync(1_000); // the event system initialises on the first ticks
        return new EventsTestWorld(world, options);
    }

    /// <summary>A bot of a town (Stay = <paramref name="stay"/>, the capital by default; 300 yards around the inn at the start), started and standing at the inn.</summary>
    internal async Task<Guid> AddHomedBotAsync(string name, TownStay stay = TownStay.Capital)
    {
        PlayerbotOperationResult created = await World.Bots.CreateAsync(name, 1, 1);
        Xunit.Assert.True(created.Success, created.Code);
        PlayerbotOperationResult started = await World.Bots.StartAsync(name);
        Xunit.Assert.True(started.Success, started.Code);
        Guid bot = started.BotId!.Value;
        await World.Host.OnWorldAsync(() =>
        {
            Player player = World.Bots.FindSession(bot)!.Player!;
            World.Bots.FindBrain(bot)!.Home = new PlayerbotHomeArea(player.MapId, Inn, 300f, "town:stormwind") { Stay = stay };
            return true;
        });
        return bot;
    }

    internal Task StartEventAsync(ushort id) => World.Host.OnWorldAsync(() => Events.Service!.StartEvent(id, overwrite: true));

    internal Task StopEventAsync(ushort id) => World.Host.OnWorldAsync(() => Events.Service!.StopEvent(id, overwrite: true));

    /// <summary>The bot's position on the ground plane, and its height, as seen from the world thread.</summary>
    internal Task<Vector3> PositionAsync(Guid bot) => World.Host.OnWorldAsync(() =>
    {
        Player player = World.Bots.FindSession(bot)!.Player!;
        return new Vector3(player.X, player.Y, player.Z);
    });

    internal static float Distance2D(Vector3 a, Vector3 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>200 ms of world time, then the pending quest settlement of a bot (the reward would otherwise outlast the manual clock).</summary>
    internal async Task StepAsync(Guid anyBot)
    {
        await World.Host.World.AdvanceClockAsync(200);
        int character = await World.Host.OnWorldAsync(() => (int)World.Bots.FindSession(anyBot)!.Player!.Guid.Low);
        await World.Services.GetRequiredService<QuestNpcFeature>().WaitForSettlementAsync(character);
    }

    /// <summary>Step until <paramref name="condition"/> (read on the world thread) holds or <paramref name="ms"/> of world time have passed.</summary>
    internal async Task<bool> RunUntilAsync(Guid anyBot, uint ms, Func<bool> condition)
    {
        for (uint elapsed = 0; elapsed < ms; elapsed += 200)
        {
            await StepAsync(anyBot);
            if (await World.Host.OnWorldAsync(condition)) return true;
        }

        return false;
    }

    /// <summary>Run <paramref name="ms"/> of world time and return (so a test can look at where bots are afterwards).</summary>
    internal async Task RunForAsync(Guid anyBot, uint ms)
    {
        for (uint elapsed = 0; elapsed < ms; elapsed += 200) await StepAsync(anyBot);
    }

    public ValueTask DisposeAsync() => World.DisposeAsync();

    private sealed class OpenPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }
}
