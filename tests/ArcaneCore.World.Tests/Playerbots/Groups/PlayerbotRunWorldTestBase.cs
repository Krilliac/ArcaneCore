using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Data.World.Instances;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Groups.RunFixtures;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Dungeon and raid runs on the real world (<see cref="PlayerbotGroupCoordinator"/> with the scenario test world, its manual clock and
/// managed bots): which free bots are matched for which wing (level band, roles, raid quotas, the entrance's requirements and the
/// permanent raid binds), what <see cref="PlayerbotGroupCoordinator.Waives"/> lets through, and what a stopped bot does to a run.
/// <para>
/// The world data is the Deadmines of the group tests (one boss, <see cref="BossEntry"/>, the encounter listed here) plus three raids
/// beside it whose entrances copy the requirement kinds of real classic-db <c>areatrigger_teleport</c> rows: Onyxia's Lair 2848 (level 50,
/// key item 16309), Molten Core window 3528 (level 50, quest 7848) and Naxxramas 4156 (level 51; classic-db's quest 9378 is replaced by
/// a conditions-table reference here, 9001, which no evaluator in the test world satisfies, so the kind is the condition's). The bosses,
/// their levels and encounters are the <see cref="RunFixtures"/> rows. The raid maps admit 10 players here (classic-db: 40) so ten bots
/// fill one.
/// </para>
/// </summary>
public abstract class PlayerbotRunWorldTestBase
{
    private protected const byte Human = 1;
    private protected const byte Warrior = 1, Priest = 5, Mage = 8;
    private protected const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    private protected const uint OnyxiaTrigger = 9201, MoltenCoreTrigger = 9202, NaxxramasTrigger = 9203;
    private protected const uint DrakefireAmulet = 16309;
    private protected const uint NaxxramasCondition = 9001;
    private protected const uint LifecycleTrash = 990741;

    private protected static readonly AreaTriggerTeleport DeadminesTeleport = new(DungeonEntry.EntranceTrigger, "Deadmines - Entering", "You must be at least level 10 to enter.", 10,
        DungeonEntry.Deadmines, -16.4f, -383.07f, 61.78f, 1.9f);

    private protected static readonly AreaTriggerTeleport OnyxiaTeleport = new(OnyxiaTrigger, "Onyxia's Lair - Entering", "", 50, OnyxiasLair, 30.8916f, -54.079f, -5.02784f, 0f,
        RequiredItem: DrakefireAmulet);

    private protected static readonly AreaTriggerTeleport MoltenCoreTeleport = new(MoltenCoreTrigger, "The Molten Core Window Entrance", "", 50, MoltenCore, 1091.89f, -466.985f, -105.084f, 0f,
        RequiredQuestDone: 7848);

    private protected static readonly AreaTriggerTeleport NaxxramasTeleport = new(NaxxramasTrigger, "Naxxramas (Entrance)", "", 51, Naxxramas, 3498.28f, -5349.9f, 144.968f, 0f,
        RequiredCondition: NaxxramasCondition);

    // --- bot QA and metrics ------------------------------------------------------------------------------------------

    private protected static Task<GroupTestWorld> StartWithQaAsync(PlayerbotQaSignalBus bus, Action<PlayerbotOptions>? configure = null)
        => GroupTestWorld.StartAsync([], configure, dungeon: true, services =>
        {
            Register(services);
            services.AddSingleton<IPlayerbotQaSink>(new PlayerbotQaSink(bus));
        }, Bosses());

    // --- the world ---------------------------------------------------------------------------------------------------

    private protected static async Task AssertNoted(GroupTestWorld world, string text)
        => Assert.True(await world.OnWorldAsync(() => world.Coordinator.Events.Any(e => e.EndsWith(text, StringComparison.Ordinal))), $"not noted: {text}{Environment.NewLine}{await world.TraceAsync()}");

    private protected static Task<GroupTestWorld> StartAsync(Action<PlayerbotOptions>? configure = null, bool withLifecycleTrash = false, uint[]? quests = null)
        => GroupTestWorld.StartAsync(quests ?? [], configure, dungeon: true, Register,
            withLifecycleTrash ? [.. Bosses(), LifecycleTrashSpawn()] : Bosses());

    /// <summary>On the world thread: the visible set and the map are mutated by the tick, so enumerating them from the test thread races it.</summary>
    private protected static Task<Creature> VisibleCreatureAsync(GroupTestWorld world, Guid botId, uint entry) => world.OnWorldAsync(() =>
    {
        Player player = world.Player(botId);
        return player.VisibleObjects.Select(g => player.Map!.FindObject(g)).OfType<Creature>().Single(c => c.Entry == entry);
    });

    // Scenario-only pack member on the straight entrance-to-boss route; 600 s respawn exceeds both runs.
    private protected static (CreatureTemplate Template, CreatureSpawn Spawn) LifecycleTrashSpawn() =>
    (
        new CreatureTemplate
        {
            Entry = LifecycleTrash, Name = "Lifecycle Trash", Faction = 14, CreatureType = 7, MinLevel = 12, MaxLevel = 12,
            DisplayIds = [903], MinLevelHealth = 80, MaxLevelHealth = 80, MinMeleeDamage = 2, MaxMeleeDamage = 3,
            MeleeBaseAttackTime = 2000,
        },
        new CreatureSpawn
        {
            Guid = LifecycleTrash, Entry = LifecycleTrash, MapId = DungeonEntry.Deadmines,
            X = -16.4f, Y = -365f, Z = global::ArcaneCore.World.Tests.Playerbots.Scenarios.DeadminesTestContent.InsideFloor,
            SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600,
        }
    );

    private protected static (CreatureTemplate Template, CreatureSpawn Spawn)[] Bosses() =>
    [
        Creature(OnyxiasLair, 10184, "Onyxia", 63, -4.8689f, -217.171f, -86.7104f, 2490001),
        Creature(MoltenCore, 11982, "Magmadar", 63, 1144.63f, -1020.48f, -185.663f, 4090221),
        Creature(Naxxramas, 15956, "Anub'Rekhan", 63, 3316.47f, -3476.23f, 287.26f, 5330151),
    ];

    private protected static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new RaidMaps());
        services.AddSingleton<IInstanceEncounterStore>(new Encounters());
        if (services.LastOrDefault(d => d.ServiceType == typeof(IItemTemplateSource))?.ImplementationInstance is ArcaneCore.World.Tests.Items.InMemoryItemTemplateSource items)
            items.Templates.Add(new ItemTemplate { Entry = DrakefireAmulet, Name = "Drakefire Amulet", Class = 12, Quality = 1, Stackable = 1, MaxCount = 1 });
    }

    private protected static async Task<(Guid Id, string Name)[]> AddPartyAsync(GroupTestWorld world, string prefix)
    {
        var members = new List<(Guid Id, string Name)>
        {
            await world.AddBotAsync(prefix + "tank", Human, Warrior, 12, [Taunt], EntranceArea),
            await world.AddBotAsync(prefix + "heal", Human, Priest, 12, [LesserHeal, Smite], EntranceArea),
        };
        foreach (char letter in "abc") members.Add(await world.AddBotAsync(prefix + "mage" + letter, Human, Mage, 12, [Fireball], EntranceArea));
        return [.. members];
    }

    /// <summary>Level 60 bots at the entrance area: the warriors first, then the priests, then the mages (names carry a letter, never a digit).</summary>
    private protected static async Task<(Guid Id, string Name)[]> AddRaidAsync(GroupTestWorld world, string prefix, int warriors, int priests, int mages)
    {
        var bots = new List<(Guid Id, string Name)>();
        for (int i = 0; i < warriors; i++) bots.Add(await world.AddBotAsync(prefix + "war" + (char)('a' + i), Human, Warrior, 60, [Taunt], EntranceArea));
        for (int i = 0; i < priests; i++) bots.Add(await world.AddBotAsync(prefix + "pri" + (char)('a' + i), Human, Priest, 60, [LesserHeal, Smite], EntranceArea));
        for (int i = 0; i < mages; i++) bots.Add(await world.AddBotAsync(prefix + "mag" + (char)('a' + i), Human, Mage, 60, [Fireball], EntranceArea));
        return [.. bots];
    }

    private protected static Task GiveAmuletAsync(GroupTestWorld world, IEnumerable<(Guid Id, string Name)> bots)
    {
        (Guid Id, string Name)[] list = [.. bots];
        return world.OnWorldAsync(() =>
        {
            foreach ((Guid id, string _) in list)
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, world.Player(id).Inventory.AddItem(DrakefireAmulet, 1, out _));
            return true;
        });
    }

    /// <summary>Load a saved instance of Onyxia's Lair and the bot's bind to it (the way the instance manager reads its stored binds at startup).</summary>
    private protected static Task Bind(GroupTestWorld world, Guid botId, uint instance, bool permanent)
        => world.OnWorldAsync(() =>
        {
            int character = (int)world.Player(botId).Guid.Low;
            world.Instances().Load(new InstanceStoreSnapshot([new InstanceRecord(instance, OnyxiasLair, 0)],
                [new CharacterInstanceBindRecord(character, instance, permanent)], [], []));
            Assert.Equal(permanent, world.Instances().GetPlayerBind(world.Player(botId).Guid, OnyxiasLair)?.Permanent);
            return true;
        });

    /// <summary>The Deadmines' encounter and the raids' (the classic-db rows of <see cref="RunFixtures.Encounters"/>).</summary>
    private protected sealed class Encounters : IInstanceEncounterStore
    {
        public Task<IReadOnlyList<InstanceEncounter>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstanceEncounter>>(
            [
                new InstanceEncounter(DungeonEntry.Deadmines, 0, 167, 0, BossEntry, true, "Deadmines Boss"),
                .. RunFixtures.Encounters().Where(e => e.MapId is OnyxiasLair or MoltenCore or Naxxramas),
            ]);
    }

    private protected sealed class RaidMaps : IMapDataStore
    {
        private static readonly MapTemplate[] Continents =
        [
            new(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        ];

        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                .. Continents,
                new MapTemplate(DungeonEntry.Deadmines, 0, MapType.Instance, 0, 10, 0, 0, -11208.4f, 1672.3f, "The Deadmines", ""),
                new MapTemplate(OnyxiasLair, 0, MapType.Raid, 0, 10, 5, 1, -4753.31f, -3752.42f, "Onyxia's Lair", ""),
                new MapTemplate(MoltenCore, 0, MapType.Raid, 0, 10, 7, 0, -7510.56f, -1036.7f, "Molten Core", ""),
                new MapTemplate(Naxxramas, 0, MapType.Raid, 0, 10, 7, 0, 0, 0, "Naxxramas", ""),
            ],
            [],
            [
                new AreaTriggerTemplate(DungeonEntry.EntranceTrigger, 0, -11208.6f, 1679.6f, 24.6f, 0f, 5f, 10f, 8f, 1.5f, "Deadmines Entrance"),
                new AreaTriggerTemplate(DungeonEntry.ExitTrigger, DungeonEntry.Deadmines, -14.6f, -390.5f, 62.4f, 5f, 0, 0, 0, 0, "Deadmines Exit"),
                new AreaTriggerTemplate(OnyxiaTrigger, 0, -11078f, 1679.6f, 24.6f, 5f, 0, 0, 0, 0, "Onyxia's Lair entrance"),
                new AreaTriggerTemplate(MoltenCoreTrigger, 0, -11078f, 1779.6f, 24.6f, 5f, 0, 0, 0, 0, "Molten Core window"),
                new AreaTriggerTemplate(NaxxramasTrigger, 0, -11078f, 1879.6f, 24.6f, 5f, 0, 0, 0, 0, "Naxxramas entrance"),
            ],
            [
                DeadminesTeleport,
                new AreaTriggerTeleport(DungeonEntry.ExitTrigger, "Deadmines - Exiting", "", 0, 0, -11208.7f, 1675.9f, 24.5733f, 4.71239f),
                OnyxiaTeleport,
                MoltenCoreTeleport,
                NaxxramasTeleport,
            ],
            []));
    }
}


/// <summary>The scenario world's Deadmines ids, and the instance manager of the test world.</summary>
internal static class DungeonEntry
{
    public const uint Deadmines = ArcaneCore.World.Playerbots.Scenarios.DungeonEntryScenario.Deadmines;
    public const uint EntranceTrigger = ArcaneCore.World.Playerbots.Scenarios.DungeonEntryScenario.EntranceTrigger;
    public const uint ExitTrigger = ArcaneCore.World.Playerbots.Scenarios.DungeonEntryScenario.ExitTrigger;

    public static InstanceManager Instances(this GroupTestWorld world) => world.World.Services.GetRequiredService<InstanceFeature>().Instances;
}
