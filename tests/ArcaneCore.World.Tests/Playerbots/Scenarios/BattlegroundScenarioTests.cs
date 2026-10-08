using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The Warsong Gulch bot scenario (<see cref="WarsongGulchScenario"/>) against the real world handlers on the manual clock: two managed bots of
/// opposite factions queue at their battlemasters, port into one match instance, play capture / drop / return / capture / capture and leave.
/// The content is synthetic (<see cref="WarsongGulchTestContent"/>): the real map, safe location and flag room coordinates of Warsong Gulch,
/// classic-db's battlemaster and event rows reduced to one per object, and a template with one player per team.
/// </summary>
public sealed class BattlegroundScenarioTests
{
    [Fact]
    public async Task Wsg_TwoBotsPlayAShortMatch_CaptureDropReturnWin_AndLeave()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        var scenario = new WarsongGulchScenario();
        ScenarioReport report = await world.RunAsync(scenario, new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(30),
            MaxDuration = TimeSpan.FromMinutes(5),
        });
        if (Environment.GetEnvironmentVariable("ARCANE_SCENARIO_REPORT_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, scenario.Name + ".txt"), report.ToString());
        }

        Assert.True(report.Passed, report.ToString());

        // The match is gone once both left: its map instance was released.
        BattlegroundFeature feature = world.Services.GetRequiredService<BattlegroundFeature>();
        Assert.Empty(await world.Host.OnWorldAsync(() => feature.Manager.RunningBattlegrounds.Where(b => b.PlayerCount > 0).ToArray()));
    }
}

/// <summary>
/// Synthetic Warsong Gulch content for the scenario: map 489 (a battleground map), the flag room triggers 3646/3647, the start and graveyard
/// safe locations 769-772, the four flag objects (stands with their <c>gameobject_battleground</c> events 0 and 1, the two dropped flags), the
/// flag auras (23333, 23335: <c>AURA_INTERRUPT_INVULNERABILITY_BUFF_CANCELS</c> so their removal drops the flag), a battlemaster at each start,
/// the faction templates of both sides and a Warsong Gulch template of one to ten players per team from level 1.
/// </summary>
internal static class WarsongGulchTestContent
{
    public const uint AllianceMasterEntry = 2302;    // classic-db battlemaster_entry (2302, 2)
    public const uint HordeMasterEntry = 2804;       // classic-db battlemaster_entry (2804, 2)
    public const uint AllianceMasterSpawn = 991001;
    public const uint HordeMasterSpawn = 991002;
    public const uint AllianceStandSpawn = 90000;    // classic-db gameobject_battleground (90000, 0, 0)
    public const uint HordeStandSpawn = 90001;       // classic-db gameobject_battleground (90001, 1, 0)

    public const float OrcStartX = -618.518f;
    public const float OrcStartY = -4251.67f;
    public const float OrcStartZ = 38.718f;

    public static readonly (float X, float Y, float Z) AllianceFlag = (1540.42f, 1481.32f, 351.82f);
    public static readonly (float X, float Y, float Z) HordeFlag = (916.02f, 1434.40f, 345.41f);

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new Maps());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
        services.AddSingleton<IBattlegroundContentStore>(new Battlegrounds());
        var store = new Content();
        services.AddSingleton<ICreatureDataStore>(store);
        services.AddScoped<IGameObjectDataStore>(_ => store);
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(Spells()));
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),   // human player
            new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),   // orc player
            new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),  // monster
            new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),  // Stormwind
            new FactionTemplateRecord(29, 76, 0, OwnMask: 4, FriendlyMask: 4, HostileMask: 8),  // Orgrimmar
        ]));
    }

    /// <summary>The speed buff's spell (classic-db gameobject_template 179871 data3).</summary>
    public const uint SpeedSpell = 23451;

    /// <summary>Divine Shield, rank 1: a positive school immunity (SPELL_AURA_SCHOOL_IMMUNITY = 39).</summary>
    public const uint DivineShield = 642;

    /// <summary>The speed buff trap, out of every flag's way.</summary>
    public const uint SpeedBuffSpawn = 90002;

    public static readonly (float X, float Y, float Z) SpeedBuff = (1200f, 1450f, 340f);

    private static SpellContent Spells() => new(
        [
            FlagAura(WarsongGulch.SpellWarsongFlag, "Warsong Flag"),
            FlagAura(WarsongGulch.SpellSilverwingFlag, "Silverwing Flag"),
            Aura(SpeedSpell, "Speed", durationIndex: 4, aura: 4),
            Aura(BattlegroundConstants.SpellWaitingToResurrect, "Waiting to Resurrect", durationIndex: 21, aura: 4, attributes: 0x00800100),  // Spell.dbc 2584: castable while dead
            Aura(BattlegroundConstants.SpellDeserter, "Deserter", durationIndex: 4, aura: 4),
            Aura(DivineShield, "Divine Shield", durationIndex: 4, aura: 39, miscValue: 127),
        ],
        [],
        [new SpellDurationRow { Id = 21, Duration = -1, MaxDuration = -1 }, new SpellDurationRow { Id = 4, Duration = 10_000, MaxDuration = 10_000 }],
        [new SpellRangeRow { Id = 1 }],
        [],
        [],
        []);

    private static SpellTemplateRow Aura(uint id, string name, uint durationIndex, uint aura, int miscValue = 0, uint attributes = 0) => new()
    {
        Id = id,
        Attributes = attributes,
        SpellName = name,
        RangeIndex = 1,
        DurationIndex = durationIndex,
        Effect1 = 6,
        EffectImplicitTargetA1 = 1,
        EffectApplyAuraName1 = aura,
        EffectMiscValue1 = miscValue,
    };

    private static SpellTemplateRow FlagAura(uint id, string name) => new()
    {
        Id = id,
        SpellName = name,
        RangeIndex = 1,
        DurationIndex = 21,
        Effect1 = 6,                    // SPELL_EFFECT_APPLY_AURA
        EffectImplicitTargetA1 = 1,     // TARGET_UNIT_CASTER
        EffectApplyAuraName1 = 4,       // SPELL_AURA_DUMMY
        AuraInterruptFlags = 0x003A0000,  // Spell.dbc 23333/23335 (mangos-classic Spell.sql): mount, leave world, stealth and invulnerability cancel it
    };

    private sealed class Maps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                new MapTemplate(489, 0, MapType.Battleground, 3277, 20, 0, -1, 0, 0, "Warsong Gulch", ""),
            ],
            [],
            [
                new AreaTriggerTemplate(WarsongGulch.AreaTriggerAllianceFlagSpawn, 489, AllianceFlag.X, AllianceFlag.Y, AllianceFlag.Z, 5f, 0, 0, 0, 0, "Alliance flag room"),
                new AreaTriggerTemplate(WarsongGulch.AreaTriggerHordeFlagSpawn, 489, HordeFlag.X, HordeFlag.Y, HordeFlag.Z, 5f, 0, 0, 0, 0, "Horde flag room"),
            ],
            [],
            []));
    }

    private sealed class Graveyards : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GraveyardContent(
            [
                new WorldSafeLoc(769, 489, 1415.33f, 1554.79f, 343.156f, 3.14159f, "WSG Alliance flag room"),
                new WorldSafeLoc(770, 489, 1029.14f, 1387.49f, 340.836f, 3.14159f, "WSG Horde flag room"),
                new WorldSafeLoc(771, 489, 1423.22f, 1554.03f, 342.833f, 0f, "WSG Alliance graveyard"),
                new WorldSafeLoc(772, 489, 1032.64f, 1388.68f, 340.559f, 0f, "WSG Horde graveyard"),
            ],
            []));
    }

    private sealed class Battlegrounds : IBattlegroundContentStore
    {
        public Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BattlegroundContent(
            [new BattlegroundTemplateRecord(2, 1, 10, 1, 60, 769, 770, 75, 0)],
            [],
            [new BattlegroundEventIndex(AllianceStandSpawn, WarsongGulch.EventFlagAlliance, 0), new BattlegroundEventIndex(HordeStandSpawn, WarsongGulch.EventFlagHorde, 0)],
            [new BattlemasterRecord(AllianceMasterEntry, 2), new BattlemasterRecord(HordeMasterEntry, 2)]));
    }

    private sealed class Content : ICreatureDataStore, IGameObjectDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [Master(AllianceMasterEntry, "Elfarran", 12), Master(HordeMasterEntry, "Kartra Bloodsnarl", 29)],
            [
                new CreatureSpawn { Guid = AllianceMasterSpawn, Entry = AllianceMasterEntry, MapId = 0, X = ScenarioTestContent.StartX + 5f, Y = ScenarioTestContent.StartY, Z = ScenarioTestContent.StartZ },
                new CreatureSpawn { Guid = HordeMasterSpawn, Entry = HordeMasterEntry, MapId = 1, X = OrcStartX + 5f, Y = OrcStartY, Z = OrcStartZ },
            ], [], [], []));

        Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new GameObjectContent(
            [
                Object(WarsongGulch.AllianceFlagBaseEntry, GameObjectType.FlagStand, "Silverwing Flag"),
                Object(WarsongGulch.HordeFlagBaseEntry, GameObjectType.FlagStand, "Warsong Flag"),
                Object(WarsongGulch.AllianceFlagGroundEntry, GameObjectType.FlagDrop, "Silverwing Flag"),
                Object(WarsongGulch.HordeFlagGroundEntry, GameObjectType.FlagDrop, "Warsong Flag"),
                SpeedTrap(),
            ],
            [
                new GameObjectSpawn { Guid = AllianceStandSpawn, Entry = WarsongGulch.AllianceFlagBaseEntry, MapId = 489, X = AllianceFlag.X, Y = AllianceFlag.Y, Z = AllianceFlag.Z, SpawnTimeSeconds = 0 },
                new GameObjectSpawn { Guid = HordeStandSpawn, Entry = WarsongGulch.HordeFlagBaseEntry, MapId = 489, X = HordeFlag.X, Y = HordeFlag.Y, Z = HordeFlag.Z, SpawnTimeSeconds = 0 },
                new GameObjectSpawn { Guid = SpeedBuffSpawn, Entry = BattlegroundConstants.SpeedBuffEntry, MapId = 489, X = SpeedBuff.X, Y = SpeedBuff.Y, Z = SpeedBuff.Z, SpawnTimeSeconds = 90 },
            ],
            [], [], []));

        private static CreatureTemplate Master(uint entry, string name, uint faction) => new()
        {
            Entry = entry, Name = name, Faction = faction, NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster), DisplayIds = [49],
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
        };

        /// <summary>classic-db gameobject_template 179871 (Speed Buff): a trap with radius 0, spell 23451, cooldown 3: a battleground buff.</summary>
        private static GameObjectTemplate SpeedTrap()
        {
            uint[] data = new uint[GameObjectTemplate.DataCount];
            data[3] = SpeedSpell;
            data[5] = BattlegroundConstants.BattlegroundTrapCooldownSeconds;
            return new GameObjectTemplate { Entry = BattlegroundConstants.SpeedBuffEntry, Type = (uint)GameObjectType.Trap, DisplayId = 5931, Name = "Speed Buff", Data = data };
        }

        private static GameObjectTemplate Object(uint entry, GameObjectType type, string name) => new()
        {
            Entry = entry, Type = (uint)type, DisplayId = 5912, Name = name, Data = new uint[GameObjectTemplate.DataCount],
        };
    }
}
