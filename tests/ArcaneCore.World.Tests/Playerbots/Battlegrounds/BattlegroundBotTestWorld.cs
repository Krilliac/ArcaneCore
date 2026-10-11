using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// The scenario test world (real handlers, the manual clock, a SQLite character database, managed playerbots) with the battleground content
/// autonomous bots need: battlemasters of Warsong Gulch, Arathi Basin and Alterac Valley of both factions a few yards from the human start
/// (map 0, where this world logs every race in), the three battleground maps with flat floors and open paths (the server's navigation needs mmaps on 489/529/30, which the
/// test world has not), and a Warsong Gulch and an Arathi Basin laid out compactly so a bot walks between the objectives in seconds.
/// <para>
/// What is real and what is synthetic: the ids are the server's (maps 489/529/30, triggers 3646/3647, the flag stands and dropped flags, the
/// flag auras 23333/23335, the AB banner entries 180087/180085/180058 and their node events, spell 21651 and lock 1479 as read from the
/// 5875 client's Spell.dbc and Lock.dbc); the coordinates, the floor heights, the creature entries of the battlemasters, the battlemaster
/// spawns and the one-player-per-team templates are test fixtures. The neutral stables banner is a button with <c>noDamageImmune</c> here
/// as in <see cref="ArathiBasinTestContent"/> (classic-db's 180087 is a goober whose trigger spell 23932 the server does not script; see
/// docs/areas/playbots-battlegrounds.md).
/// </para>
/// </summary>
internal static class BattlegroundBotTestContent
{
    public const uint AllianceWsgMaster = 993101;
    public const uint HordeWsgMaster = 993102;
    public const uint AllianceAbMaster = 993103;
    public const uint HordeAbMaster = 993104;
    public const uint AllianceAvMaster = 993105;
    public const uint HordeAvMaster = 993106;

    /// <summary>
    /// Where a battlemaster stands: every one on map 0 around the human start, where the scenario world logs every race in (its
    /// player-create rows put the orc there too); the Alliance ones 3 yards east, north or south of the start, the Horde ones 6 yards west of those.
    /// </summary>
    public static Vector3 MasterAt(uint entry) => entry switch
    {
        AllianceWsgMaster => new(StartX + 3f, StartY, StartZ),
        HordeWsgMaster => new(StartX - 3f, StartY, StartZ),
        AllianceAbMaster => new(StartX, StartY + 3f, StartZ),
        HordeAbMaster => new(StartX - 6f, StartY + 3f, StartZ),
        AllianceAvMaster => new(StartX, StartY - 3f, StartZ),
        HordeAvMaster => new(StartX - 6f, StartY - 3f, StartZ),
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };

    public const float OrcStartX = WarsongGulchTestContent.OrcStartX;
    public const float OrcStartY = WarsongGulchTestContent.OrcStartY;
    public const float OrcStartZ = WarsongGulchTestContent.OrcStartZ;

    /// <summary>The flat floor of each battleground map (yards).</summary>
    public const float WsgFloor = 345f;
    public const float AbFloor = -50f;
    public const float AvFloor = 50f;

    /// <summary>Warsong Gulch, compact: the Alliance flag room (stand and trigger 3646) and the Horde one (3647) 60 yards apart.</summary>
    public static readonly Vector3 AllianceFlag = new(1100f, 1400f, WsgFloor);
    public static readonly Vector3 HordeFlag = new(1160f, 1400f, WsgFloor);

    /// <summary>Where a Horde opponent is parked so it is never in sight of the Alliance bot's errand (250 yards north).</summary>
    public static readonly Vector3 HordeParking = new(1130f, 1650f, WsgFloor);

    /// <summary>Arathi Basin, compact: the stables node 20 yards from the Alliance start.</summary>
    public static readonly Vector3 Stables = new(1166.79f, 1200.13f, AbFloor);

    public const uint WsgAllianceStandSpawn = 993201;
    public const uint WsgHordeStandSpawn = 993202;
    public const uint AbNeutralBanner = 993301;
    public const uint AbAllianceContestedBanner = 993302;
    public const uint AbAllianceBanner = 993303;

    /// <summary>Lock.dbc 5875 row 1479 (read 2026-10-10): case 1 is LOCK_KEY_SKILL (2) of lock type 17 (LOCKTYPE_SLOW_OPEN), no skill needed.</summary>
    public static readonly LockEntry BannerLock = new(1479, [0, 2, 0, 0, 0, 0, 0, 0], [0, 17, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]);

    /// <param name="offered">The types whose battlemasters are spawned (every type's template, map and objectives are loaded regardless).</param>
    /// <param name="realNeutralBanner">The stables' neutral banner as classic-db and vmangos have it (a goober, see <see cref="GooberBanner"/>).</param>
    /// <summary>
    /// The stables' neutral banner as the real data has it (vmangos sql/migrations/20260620100323_world.sql, 180087 patch 6; classic-db
    /// gameobject_template): a goober (type 10) whose lock (data0) is 1479 and whose spell (data10) is 23932 "A-Mid Trigger", with
    /// <c>noDamageImmune</c> (data11) set. The server does not route its use to the node click (MatchRuntime.RoutesBannerUse).
    /// </summary>
    public static GameObjectTemplate GooberBanner()
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[0] = BannerLock.Id;
        data[3] = 196608;
        data[10] = 23932;
        data[11] = 1;
        return new GameObjectTemplate { Entry = 180087, Type = (uint)GameObjectType.Goober, DisplayId = 6271, Name = "Stable Banner", Data = data };
    }

    public static void Register(IServiceCollection services, IReadOnlyCollection<BattlegroundType> offered, bool realNeutralBanner = false)
    {
        services.AddSingleton<IMapDataStore>(new Maps());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
        services.AddSingleton<IBattlegroundContentStore>(new Battlegrounds());
        var store = new Content(offered, realNeutralBanner);
        services.AddSingleton<ICreatureDataStore>(store);
        services.AddScoped<IGameObjectDataStore>(_ => store);
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(Spells()));
        // The skills feature installs the open-lock effect (GatheringSpells: spell 21651 opens a banner) only with a skill catalog; lock type
        // 17 needs no skill, so an empty catalog is enough.
        services.AddSingleton(new ArcaneCore.Kernel.Skills.SkillCatalog([], [], [], []));
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),   // human player
            new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),   // orc player
            new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),  // monster
            new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),  // Stormwind
            new FactionTemplateRecord(29, 76, 0, OwnMask: 4, FriendlyMask: 4, HostileMask: 8),  // Orgrimmar
        ]));
        // Stormwind (72) and Orgrimmar (76) are reputation factions: a battlemaster's reaction is read through reputation (the scenario
        // content lists Stormwind alone, so a Horde battlemaster would refuse its own side).
        services.AddSingleton(new ArcaneCore.Kernel.Reputation.FactionCatalog(
        [
            new ArcaneCore.Kernel.Reputation.FactionRecord(72, 0, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Stormwind"),
            new ArcaneCore.Kernel.Reputation.FactionRecord(76, 1, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Orgrimmar"),
        ]));
    }

    /// <summary>
    /// Spell 21651 "Opening" as the 5875 client's Spell.dbc has it (read 2026-10-10): attributes 0x190, cast time index 7 (10 s), range index
    /// 12 (0-5 yards), interrupt flags 0x1F, effect 33 OPEN_LOCK at TARGET_GAMEOBJECT (23) with lock type 17 and base points 99.
    /// </summary>
    public static SpellTemplateRow BannerSpell() => new()
    {
        Id = PlayerbotBattlegroundAI.BannerSpell, SpellName = "Opening", Attributes = 0x190, CastingTimeIndex = 7, RangeIndex = 12, InterruptFlags = 0x1F,
        Targets = 0x4000, Effect1 = 33, EffectImplicitTargetA1 = 23, EffectMiscValue1 = 17, EffectBasePoints1 = 99,
    };

    private static SpellContent Spells() => new(
        [
            FlagAura(WarsongGulch.SpellWarsongFlag, "Warsong Flag"),
            FlagAura(WarsongGulch.SpellSilverwingFlag, "Silverwing Flag"),
            Aura(BattlegroundConstants.SpellWaitingToResurrect, "Waiting to Resurrect", durationIndex: 21, attributes: 0x00800100),
            Aura(BattlegroundConstants.SpellDeserter, "Deserter", durationIndex: 4),
            BannerSpell(),
        ],
        [new SpellCastTimeRow { Id = 7, CastTime = 10_000, MinCastTime = 10_000 }],
        [new SpellDurationRow { Id = 21, Duration = -1, MaxDuration = -1 }, new SpellDurationRow { Id = 4, Duration = 900_000, MaxDuration = 900_000 }],
        [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 12, MinRange = 0, MaxRange = 5 }],
        [],
        // classic-db playercreateinfo_spell: every race and class learns 21651 "Opening" (here the human and the orc warrior).
        [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = PlayerbotBattlegroundAI.BannerSpell, Note = "Opening" },
         new PlayerCreateSpellRow { Race = 2, Class = 1, Spell = PlayerbotBattlegroundAI.BannerSpell, Note = "Opening" }],
        []);

    private static SpellTemplateRow Aura(uint id, string name, uint durationIndex, uint attributes = 0) => new()
    {
        Id = id, Attributes = attributes, SpellName = name, RangeIndex = 1, DurationIndex = durationIndex,
        Effect1 = 6, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 4,
    };

    private static SpellTemplateRow FlagAura(uint id, string name) => new()
    {
        Id = id, SpellName = name, RangeIndex = 1, DurationIndex = 21, Effect1 = 6, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 4,
        AuraInterruptFlags = 0x003A0000,
    };

    private sealed class Maps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                new MapTemplate(30, 0, MapType.Battleground, 2597, 80, 0, -1, 0, 0, "Alterac Valley", ""),
                new MapTemplate(489, 0, MapType.Battleground, 3277, 20, 0, -1, 0, 0, "Warsong Gulch", ""),
                new MapTemplate(529, 0, MapType.Battleground, 3358, 30, 0, -1, 0, 0, "Arathi Basin", ""),
            ],
            [],
            [
                new AreaTriggerTemplate(WarsongGulch.AreaTriggerAllianceFlagSpawn, 489, AllianceFlag.X, AllianceFlag.Y, AllianceFlag.Z, 4f, 0, 0, 0, 0, "Alliance flag room"),
                new AreaTriggerTemplate(WarsongGulch.AreaTriggerHordeFlagSpawn, 489, HordeFlag.X, HordeFlag.Y, HordeFlag.Z, 4f, 0, 0, 0, 0, "Horde flag room"),
            ],
            [],
            []));
    }

    private sealed class Graveyards : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GraveyardContent(
            [
                new WorldSafeLoc(769, 489, AllianceFlag.X - 15f, AllianceFlag.Y, WsgFloor, 0f, "WSG Alliance start"),
                new WorldSafeLoc(770, 489, HordeFlag.X + 15f, HordeFlag.Y, WsgFloor, MathF.PI, "WSG Horde start"),
                new WorldSafeLoc(771, 489, AllianceFlag.X - 20f, AllianceFlag.Y + 10f, WsgFloor, 0f, "WSG Alliance graveyard"),
                new WorldSafeLoc(772, 489, HordeFlag.X + 20f, HordeFlag.Y + 10f, WsgFloor, 0f, "WSG Horde graveyard"),
                new WorldSafeLoc(890, 529, Stables.X - 20f, Stables.Y, AbFloor, 0f, "AB Alliance start"),
                new WorldSafeLoc(889, 529, 900f, 900f, AbFloor, 0f, "AB Horde start"),
                new WorldSafeLoc(611, 30, 100f, 0f, AvFloor, 0f, "AV Alliance start"),
                new WorldSafeLoc(610, 30, -100f, 0f, AvFloor, 0f, "AV Horde start"),
            ],
            []));
    }

    private sealed class Battlegrounds : IBattlegroundContentStore
    {
        public Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BattlegroundContent(
            [
                new BattlegroundTemplateRecord(1, 1, 40, 1, 60, 611, 610, 100, 0),
                new BattlegroundTemplateRecord(2, 1, 10, 1, 60, 769, 770, 75, 0),
                new BattlegroundTemplateRecord(3, 1, 15, 1, 60, 890, 889, 75, 0),
            ],
            [],
            [
                new BattlegroundEventIndex(WsgAllianceStandSpawn, WarsongGulch.EventFlagAlliance, 0),
                new BattlegroundEventIndex(WsgHordeStandSpawn, WarsongGulch.EventFlagHorde, 0),
                new BattlegroundEventIndex(AbNeutralBanner, ArathiBasin.NodeStables, ArathiBasin.StatusNeutral),
                new BattlegroundEventIndex(AbAllianceContestedBanner, ArathiBasin.NodeStables, ArathiBasin.StatusAllianceContested),
                new BattlegroundEventIndex(AbAllianceBanner, ArathiBasin.NodeStables, ArathiBasin.StatusAllianceOccupied),
            ],
            [
                new BattlemasterRecord(AllianceAvMaster, 1), new BattlemasterRecord(HordeAvMaster, 1),
                new BattlemasterRecord(AllianceWsgMaster, 2), new BattlemasterRecord(HordeWsgMaster, 2),
                new BattlemasterRecord(AllianceAbMaster, 3), new BattlemasterRecord(HordeAbMaster, 3),
            ]));
    }

    private sealed class Content(IReadOnlyCollection<BattlegroundType> offered, bool realNeutralBanner) : ICreatureDataStore, IGameObjectDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                Master(AllianceWsgMaster, "Test WSG Battlemaster", 12), Master(HordeWsgMaster, "Test WSG Battlemaster", 29),
                Master(AllianceAbMaster, "Test AB Battlemaster", 12), Master(HordeAbMaster, "Test AB Battlemaster", 29),
                Master(AllianceAvMaster, "Test AV Battlemaster", 12), Master(HordeAvMaster, "Test AV Battlemaster", 29),
            ],
            [
                .. offered.Contains(BattlegroundType.WarsongGulch)
                    ? [Spawn(AllianceWsgMaster, MasterAt(AllianceWsgMaster)), Spawn(HordeWsgMaster, MasterAt(HordeWsgMaster))]
                    : Array.Empty<CreatureSpawn>(),
                .. offered.Contains(BattlegroundType.ArathiBasin)
                    ? [Spawn(AllianceAbMaster, MasterAt(AllianceAbMaster)), Spawn(HordeAbMaster, MasterAt(HordeAbMaster))]
                    : Array.Empty<CreatureSpawn>(),
                .. offered.Contains(BattlegroundType.AlteracValley)
                    ? [Spawn(AllianceAvMaster, MasterAt(AllianceAvMaster)), Spawn(HordeAvMaster, MasterAt(HordeAvMaster))]
                    : Array.Empty<CreatureSpawn>(),
            ], [], [], []));

        Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new GameObjectContent(
            [
                Object(WarsongGulch.AllianceFlagBaseEntry, GameObjectType.FlagStand, "Silverwing Flag"),
                Object(WarsongGulch.HordeFlagBaseEntry, GameObjectType.FlagStand, "Warsong Flag"),
                Object(WarsongGulch.AllianceFlagGroundEntry, GameObjectType.FlagDrop, "Silverwing Flag"),
                Object(WarsongGulch.HordeFlagGroundEntry, GameObjectType.FlagDrop, "Warsong Flag"),
                realNeutralBanner ? GooberBanner() : Banner(180087, "Stable Banner"), Banner(180085, "Contested Banner"), Banner(180058, "Alliance Banner"),
            ],
            [
                new GameObjectSpawn { Guid = WsgAllianceStandSpawn, Entry = WarsongGulch.AllianceFlagBaseEntry, MapId = 489, X = AllianceFlag.X, Y = AllianceFlag.Y, Z = AllianceFlag.Z },
                new GameObjectSpawn { Guid = WsgHordeStandSpawn, Entry = WarsongGulch.HordeFlagBaseEntry, MapId = 489, X = HordeFlag.X, Y = HordeFlag.Y, Z = HordeFlag.Z },
                BannerSpawn(AbNeutralBanner, 180087), BannerSpawn(AbAllianceContestedBanner, 180085), BannerSpawn(AbAllianceBanner, 180058),
            ],
            [BannerLock], [], []));

        private static CreatureSpawn Spawn(uint entry, Vector3 at)
            => new() { Guid = entry, Entry = entry, MapId = 0, X = at.X, Y = at.Y, Z = at.Z, Orientation = MathF.PI };

        private static CreatureTemplate Master(uint entry, string name, uint faction) => new()
        {
            Entry = entry, Name = name, Faction = faction, NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster), DisplayIds = [49],
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
        };

        private static GameObjectTemplate Object(uint entry, GameObjectType type, string name) => new()
        {
            Entry = entry, Type = (uint)type, DisplayId = 5912, Name = name, Data = new uint[GameObjectTemplate.DataCount],
        };

        /// <summary>
        /// An Arathi Basin banner as classic-db 180058 / 180085 have it: a button (type 1) whose lock (data1) is 1479 and whose
        /// <c>noDamageImmune</c> (data4) is set (vmangos Spell::EffectOpenLock, SpellEffects.cpp:2120-2131).
        /// </summary>
        private static GameObjectTemplate Banner(uint entry, string name)
        {
            uint[] data = new uint[GameObjectTemplate.DataCount];
            data[1] = BannerLock.Id;
            data[4] = 1;
            return new GameObjectTemplate { Entry = entry, Type = (uint)GameObjectType.Button, DisplayId = 5651, Name = name, Data = data };
        }

        private static GameObjectTemplate GooberBanner() => BattlegroundBotTestContent.GooberBanner();

        private static GameObjectSpawn BannerSpawn(uint guid, uint entry)
            => new() { Guid = guid, Entry = entry, MapId = 529, X = Stables.X, Y = Stables.Y, Z = Stables.Z };
    }
}

/// <summary>The world for the bot battleground tests: <see cref="BattlegroundBotTestContent"/>, open paths and per-map floors, and the bot options.</summary>
internal static class BattlegroundBotTestWorld
{
    /// <param name="offered">The types with battlemasters beside the starts (default: Warsong Gulch only, so two bots meet in one match).</param>
    public static Task<ScenarioTestWorld> StartAsync(Action<PlayerbotOptions>? configure = null, params BattlegroundType[] offered)
        => StartAsync(configure, realNeutralBanner: false, offered);

    /// <param name="realNeutralBanner">The stables' neutral banner as the real data has it (<see cref="BattlegroundBotTestContent.GooberBanner"/>).</param>
    public static async Task<ScenarioTestWorld> StartAsync(Action<PlayerbotOptions>? configure, bool realNeutralBanner, params BattlegroundType[] offered)
    {
        var options = new PlayerbotOptions
        {
            Enabled = true, MaxBots = 8, AllowedMaps = [0, 1], ThinkIntervalMs = 200, MaxActionsPerTick = 32, Scenarios = { Enabled = true },
            Battlegrounds = { QueueChancePerMinute = 1f },
        };
        configure?.Invoke(options);
        ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            BattlegroundBotTestContent.Register(services, offered.Length == 0 ? [BattlegroundType.WarsongGulch] : offered, realNeutralBanner);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
        });
        await world.Host.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new Floor(), pathfinder: new OpenPathfinder());
            return true;
        });
        return world;
    }

    public static PlayerbotBattlegroundCoordinator Coordinator(this ScenarioTestWorld world) => world.Services.GetRequiredService<PlayerbotBattlegroundCoordinator>();

    public static BattlegroundFeature Battlegrounds(this ScenarioTestWorld world) => world.Services.GetRequiredService<BattlegroundFeature>();

    /// <summary>Everything the coordinator noted and every bot's status, for failure messages.</summary>
    public static string Trace(this ScenarioTestWorld world) => string.Join(Environment.NewLine, world.Coordinator().Events) + Environment.NewLine
        + string.Join(Environment.NewLine, world.Coordinator().Report()) + Environment.NewLine
        + string.Join(Environment.NewLine, world.Bots.Snapshot().Select(s => $"{s.Name} goal={s.Goal} map={s.MapId} health={s.Health} {s.Group} {s.Risk} {s.ErrorCode}"));

    /// <summary>Flat ground: each battleground map at its floor, the orc start's height on Kalimdor, the human start's elsewhere.</summary>
    private sealed class Floor : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => mapId switch
        {
            489 => BattlegroundBotTestContent.WsgFloor,
            529 => BattlegroundBotTestContent.AbFloor,
            30 => BattlegroundBotTestContent.AvFloor,
            1 => BattlegroundBotTestContent.OrcStartZ,
            _ => StartZ,
        };

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
