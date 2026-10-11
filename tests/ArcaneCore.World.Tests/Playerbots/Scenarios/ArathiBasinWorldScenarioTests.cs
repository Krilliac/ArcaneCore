using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Arathi Basin in the world, on the real classic-db shapes of the banners (<see cref="ArathiBasinTestContent"/>): two managed bots queue at
/// their battlemasters and port into one Arathi Basin match, and the Alliance bot casts Opening (21651) on the neutral stables banner - a goober
/// whose use casts its trigger spell 23932 at the user, which claims the node (vmangos spell_battleground_banner_trigger). The banner events
/// swap the neutral banner for the contested one a second later (the event spawn gate), a minute later the node is occupied and its banner
/// appears five seconds after, and resources tick to the world state every client of the match receives. The Horde then opens the occupied
/// banner (a button with noDamageImmune): the node is assaulted.
/// </summary>
public sealed class ArathiBasinWorldScenarioTests
{
    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => Name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    [Fact]
    public async Task Ab_TheStablesBanner_IsClaimed_Occupied_TicksResources_AndIsAssaulted()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(ArathiBasinTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario("ab-node", async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(BattlegroundType.ArathiBasin, "Scnabally", "Scnabhorde", TimeSpan.FromMinutes(3));
            var ab = (ArathiBasin)match;

            await context.StepAsync("only the neutral stables banner stands", () => context.WaitUntilAsync("the neutral banner is spawned, the others are not",
                () => Spawned(ally, ArathiBasinTestContent.NeutralBanner) && !Spawned(ally, ArathiBasinTestContent.AllianceContestedBanner)
                    && !Spawned(ally, ArathiBasinTestContent.AllianceBanner)));

            long mark = horde.Mark();
            await context.StepAsync("the Alliance bot opens the neutral banner: the stables are claimed", async () =>
            {
                await context.PlaceAsync(ally, 529, ArathiBasinTestContent.Stables.X + 1f, ArathiBasinTestContent.Stables.Y, ArathiBasinTestContent.Stables.Z);
                (SpellCastResult opened, uint uses) = await context.ReadAsync(() => OpenBanner(context, ally, ArathiBasinTestContent.NeutralBanner));
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, opened, "Opening cast result");
                ScenarioContext.ExpectEqual(1u, uses, "neutral banner uses (the open reached the goober)");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusAllianceContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
                await horde.WaitForPacketAsync(WorldOpcode.SmsgUpdateWorldState, ScenarioBattlegrounds.WorldState, s => s.Field == 1767 + 2 && s.Value == 1, mark);
            });

            await context.StepAsync("a second later the contested banner replaces the neutral one", () => context.WaitUntilAsync("the contested banner stands",
                () => Spawned(ally, ArathiBasinTestContent.AllianceContestedBanner) && !Spawned(ally, ArathiBasinTestContent.NeutralBanner)));

            await context.StepAsync("a minute later the stables are occupied and their banner appears", async () =>
            {
                await context.WaitUntilAsync("the stables are occupied", () => ab.NodeStatus(ArathiBasin.NodeStables) == ArathiBasin.StatusAllianceOccupied, TimeSpan.FromSeconds(70));
                await context.WaitUntilAsync("the occupied banner stands", () => Spawned(ally, ArathiBasinTestContent.AllianceBanner) && !Spawned(ally, ArathiBasinTestContent.AllianceContestedBanner), TimeSpan.FromSeconds(10));
            });

            await context.StepAsync("one base ticks 10 resources, seen by the Horde client", async () =>
            {
                await context.WaitUntilAsync("10 resources", () => ab.TeamScore(Team.Alliance) >= 10, TimeSpan.FromSeconds(15));
                await horde.WaitForPacketAsync(WorldOpcode.SmsgUpdateWorldState, ScenarioBattlegrounds.WorldState,
                    s => s.Field == ArathiBasin.WorldStateResourcesAlliance && s.Value == 10, mark);
            });

            await context.StepAsync("the Horde bot assaults the occupied stables", async () =>
            {
                await context.PlaceAsync(horde, 529, ArathiBasinTestContent.Stables.X - 1f, ArathiBasinTestContent.Stables.Y, ArathiBasinTestContent.Stables.Z);
                (SpellCastResult opened, _) = await context.ReadAsync(() => OpenBanner(context, horde, ArathiBasinTestContent.AllianceBanner));
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, opened, "Opening cast result");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusHordeContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AbScore)ab.ScoreOf(horde.Guid)!).BasesAssaulted), "Horde bases assaulted");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    /// <summary>
    /// Every one of the five neutral banners (classic-db 180087-180091 at their classic-db spawns, each a goober with its own trigger spell:
    /// 23932, 23936, 23935, 23938, 23937) is claimed by an Opening cast: all five nodes become Alliance-contested and the bot has five bases
    /// assaulted.
    /// </summary>
    [Fact]
    public async Task Ab_EveryNeutralBanner_IsClaimed_ByTheOpeningSpell()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(ArathiBasinTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario("ab-neutral-banners", async context =>
        {
            (ScenarioBot ally, _, Battleground match) = await context.EnterMatchAsync(BattlegroundType.ArathiBasin, "Scnabnally", "Scnabnhorde", TimeSpan.FromMinutes(3));
            var ab = (ArathiBasin)match;

            for (int node = 0; node < ArathiBasin.NodeCount; node++)
            {
                (uint spawn, uint entry, (float X, float Y, float Z) at) = ArathiBasinTestContent.NeutralBanners[node];
                int n = node;
                await context.StepAsync($"node {n}: the neutral banner {entry} is opened and the node is claimed", async () =>
                {
                    await context.PlaceAsync(ally, 529, at.X + 1f, at.Y, at.Z);
                    await context.WaitUntilAsync("the neutral banner stands", () => Spawned(ally, spawn));
                    ScenarioContext.ExpectEqual(ArathiBasin.StatusNeutral, await context.ReadAsync(() => ab.NodeStatus(n)), "status before");
                    (SpellCastResult opened, uint uses) = await context.ReadAsync(() => OpenBanner(context, ally, spawn));
                    ScenarioContext.ExpectEqual(SpellCastResult.CastOk, opened, "Opening cast result");
                    ScenarioContext.ExpectEqual(1u, uses, "banner uses (the open reached the goober)");
                    ScenarioContext.ExpectEqual(ArathiBasin.StatusAllianceContested, await context.ReadAsync(() => ab.NodeStatus(n)), "status after");
                });
            }

            await context.StepAsync("five bases assaulted", async ()
                => ScenarioContext.ExpectEqual(5u, await context.ReadAsync(() => ((AbScore)ab.ScoreOf(ally.Guid)!).BasesAssaulted), "Alliance bases assaulted"));
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    /// <summary>
    /// The routing predicate on the real templates: the neutral goobers (trigger spells 23932, 23935-23938) and the faction/contested buttons
    /// (noDamageImmune) reach the node click; the same goober with another spell, a button without noDamageImmune, a flag stand and a chest do not.
    /// </summary>
    [Fact]
    public void RoutesBannerUse_TakesTheRealBannerShapes_AndNothingElse()
    {
        foreach (GameObjectTemplate banner in ArathiBasinTestContent.Templates)
        {
            Assert.True(ArcaneCore.World.Battlegrounds.MatchRuntime.RoutesBannerUse(banner), $"{banner.Entry} {banner.Name}");
        }

        GameObjectTemplate neutral = ArathiBasinTestContent.Templates.First(t => t.Entry == 180087);
        uint[] otherSpell = [.. neutral.Data];
        otherSpell[10] = 23383; // Alliance Flag Click: a battleground spell, but not a banner trigger
        Assert.False(ArcaneCore.World.Battlegrounds.MatchRuntime.RoutesBannerUse(neutral with { Data = otherSpell }));
        GameObjectTemplate button = ArathiBasinTestContent.Templates.First(t => t.Entry == 180058);
        uint[] noImmune = [.. button.Data];
        noImmune[4] = 0;
        Assert.False(ArcaneCore.World.Battlegrounds.MatchRuntime.RoutesBannerUse(button with { Data = noImmune }));
        Assert.False(ArcaneCore.World.Battlegrounds.MatchRuntime.RoutesBannerUse(neutral with { Type = (uint)GameObjectType.FlagStand }));
        Assert.False(ArcaneCore.World.Battlegrounds.MatchRuntime.RoutesBannerUse(neutral with { Type = (uint)GameObjectType.Chest }));
    }

    private static GameObjectMapSystem Objects(ScenarioBot bot) => bot.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!;

    private static bool Spawned(ScenarioBot bot, uint spawnGuid) => Objects(bot).GameObjects.Any(g => g.Spawn?.Guid == spawnGuid && g.IsSpawned);

    /// <summary>
    /// The bot casts Opening (21651) on the banner, as a client's click on a locked banner does once its cast bar ran (the cast is triggered so
    /// the ten-second bar does not run): the open-lock effect opens lock 1479 and hands the banner to GameObjectMapSystem.OpenLock. Also returns
    /// how often the banner was used once the cast returned, so a failure tells an open that never reached the object from a use that did not click.
    /// </summary>
    private static (SpellCastResult Result, uint Uses) OpenBanner(ScenarioContext context, ScenarioBot bot, uint spawnGuid)
    {
        GameObject banner = Objects(bot).GameObjects.Single(g => g.Spawn?.Guid == spawnGuid && g.IsSpawned);
        SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;
        SpellCastResult result = spells.CastSpell(bot.RequirePlayer(), ArathiBasinTestContent.OpeningSpell,
            new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = banner.Guid }, triggered: true);
        return (result, banner.UseCount);
    }
}

/// <summary>
/// Arathi Basin content with the real shapes of classic-db z2815 (Full_DB/ClassicDB_1_12_1_z2815.sql.gz) and the 5875 client's DBCs: map 529,
/// the start safe locations 890/889 and the stables graveyard 895, a template of one to fifteen players per team from level 1, the battlemasters
/// 857 and 907, the five neutral banners (<c>gameobject</c> 5290025-5290029, entries 180087-180091: goobers locked by 1479 whose data10 is the
/// node's trigger spell) on the events (node, 0), and the stables' Alliance contested (5290010, 180059) and occupied (5290005, 180058) banners
/// (buttons locked by 1479 with <c>noDamageImmune</c>) on (0, 1) and (0, 3); the Opening spell 21651, the trigger spells and lock 1479.
/// </summary>
internal static class ArathiBasinTestContent
{
    public const uint NeutralBanner = 5290025;
    public const uint AllianceContestedBanner = 5290010;
    public const uint AllianceBanner = 5290005;

    /// <summary>Opening (Spell.dbc 21651; classic-db playercreateinfo_spell gives it to every race and class).</summary>
    public const uint OpeningSpell = 21651;

    /// <summary>The lock of every Arathi Basin banner (gameobject_template data0 of 180087-180091, data1 of 180058/180059).</summary>
    public const uint BannerLock = 1479;

    public static readonly (float X, float Y, float Z) Stables = (1166.79f, 1200.13f, -56.7086f);

    /// <summary>The neutral banners by node (classic-db gameobject and gameobject_battleground (guid, node, 0)).</summary>
    public static readonly (uint Spawn, uint Entry, (float X, float Y, float Z) At)[] NeutralBanners =
    [
        (5290025, 180087, Stables),
        (5290026, 180088, (977.016f, 1046.62f, -44.8092f)),
        (5290027, 180089, (806.182f, 874.272f, -55.9937f)),
        (5290028, 180090, (856.142f, 1148.9f, 11.1847f)),
        (5290029, 180091, (1146.92f, 848.178f, -110.917f)),
    ];

    /// <summary>The banner templates (classic-db gameobject_template rows).</summary>
    public static GameObjectTemplate[] Templates => Content.Templates;

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new Maps());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
        services.AddSingleton<IBattlegroundContentStore>(new Battlegrounds());
        var store = new Content();
        services.AddSingleton<ICreatureDataStore>(store);
        services.AddScoped<IGameObjectDataStore>(_ => store);
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(Spells(ScenarioTestContent.Spells())));
        // The skills feature owns the open-lock effect of Opening (GatheringSpells); it runs once a skill catalog exists.
        services.AddSingleton(new SkillCatalog([], [], [], []));
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
            new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),
            new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
            new FactionTemplateRecord(29, 76, 0, OwnMask: 4, FriendlyMask: 4, HostileMask: 8),
        ]));
    }

    /// <summary>
    /// Spell.dbc 5875: Opening 21651 (OPEN_LOCK (33) of lock type 17 at the object target (23), cast time index 7 (10 s), range index 12 (5 yd),
    /// attributes 0x190, targets 0x4000, interrupt flags 31) and the five trigger spells (SCRIPT_EFFECT (77) at TARGET_UNIT (25), cast time
    /// index 1, range index 6 (100 yd), attributes 0x100, attributesEx2 4).
    /// </summary>
    private static SpellContent Spells(SpellContent baseline)
    {
        SpellTemplateRow[] added =
        [
            new SpellTemplateRow
            {
                Id = OpeningSpell, SpellName = "Opening", Attributes = 0x190, Targets = 0x4000, CastingTimeIndex = 7, InterruptFlags = 31, RangeIndex = 12,
                SpellVisual = 6139, Effect1 = 33, EffectDieSides1 = 1, EffectBaseDice1 = 1, EffectBasePoints1 = 99, EffectImplicitTargetA1 = 23,
                EffectMiscValue1 = 17,
            },
            Trigger(23932, "A-Mid Trigger"),
            Trigger(23935, "H-Mid Trigger"),
            Trigger(23936, "Mid Trigger"),
            Trigger(23937, "ALT -N Trigger"),
            Trigger(23938, "ALT -S Trigger"),
        ];
        SpellCastTimeRow[] castTimes = [new SpellCastTimeRow { Id = 1 }, new SpellCastTimeRow { Id = 7, CastTime = 10_000, MinCastTime = 10_000 }];
        SpellRangeRow[] ranges = [new SpellRangeRow { Id = 6, MaxRange = 100 }, new SpellRangeRow { Id = 12, MaxRange = 5 }];
        return baseline with
        {
            Spells = [.. baseline.Spells.Where(s => added.All(a => a.Id != s.Id)), .. added],
            CastTimes = [.. baseline.CastTimes.Where(c => castTimes.All(a => a.Id != c.Id)), .. castTimes],
            Ranges = [.. baseline.Ranges.Where(r => ranges.All(a => a.Id != r.Id)), .. ranges],
        };
    }

    private static SpellTemplateRow Trigger(uint id, string name) => new()
    {
        Id = id, SpellName = name, Attributes = 0x100, AttributesEx2 = 4, CastingTimeIndex = 1, RangeIndex = 6, Effect1 = 77, EffectImplicitTargetA1 = 25,
    };

    private sealed class Maps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                new MapTemplate(529, 0, MapType.Battleground, 3358, 30, 0, -1, 0, 0, "Arathi Basin", ""),
            ],
            [], [], [], []));
    }

    private sealed class Graveyards : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GraveyardContent(
            [
                new WorldSafeLoc(890, 529, 1286.05f, 1282.53f, -15.66f, 0.71f, "AB Alliance start"),
                new WorldSafeLoc(889, 529, 708.40f, 708.82f, -17.71f, 3.92f, "AB Horde start"),
                new WorldSafeLoc(895, 529, 1201.87f, 1163.13f, -56.29f, 0f, "AB stables graveyard"),
            ],
            []));
    }

    private sealed class Battlegrounds : IBattlegroundContentStore
    {
        public Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BattlegroundContent(
            [new BattlegroundTemplateRecord(3, 1, 15, 1, 60, 890, 889, 75, 0)],
            [],
            [
                .. NeutralBanners.Select((b, node) => new BattlegroundEventIndex(b.Spawn, (byte)node, ArathiBasin.StatusNeutral)),
                new BattlegroundEventIndex(AllianceContestedBanner, ArathiBasin.NodeStables, ArathiBasin.StatusAllianceContested),
                new BattlegroundEventIndex(AllianceBanner, ArathiBasin.NodeStables, ArathiBasin.StatusAllianceOccupied),
            ],
            [new BattlemasterRecord(857, 3), new BattlemasterRecord(907, 3)]));
    }

    private sealed class Content : ICreatureDataStore, IGameObjectDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [Master(857, "Donald Osgood", 12), Master(907, "Keras Wolfheart", 29)],
            [
                new CreatureSpawn { Guid = 992001, Entry = 857, MapId = 0, X = ScenarioTestContent.StartX + 5f, Y = ScenarioTestContent.StartY, Z = ScenarioTestContent.StartZ },
                new CreatureSpawn { Guid = 992002, Entry = 907, MapId = 1, X = WarsongGulchTestContent.OrcStartX + 5f, Y = WarsongGulchTestContent.OrcStartY, Z = WarsongGulchTestContent.OrcStartZ },
            ], [], [], []));

        Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new GameObjectContent(
            Templates,
            [
                .. NeutralBanners.Select(b => Spawn(b.Spawn, b.Entry, b.At)),
                Spawn(AllianceContestedBanner, 180059, Stables),
                Spawn(AllianceBanner, 180058, Stables),
            ],
            [Lock1479()], [], []));

        public static GameObjectTemplate[] Templates =>
        [
            NeutralBannerTemplate(180087, "Stable Banner", 23932),
            NeutralBannerTemplate(180088, "Blacksmith Banner", 23936),
            NeutralBannerTemplate(180089, "Farm Banner", 23935),
            NeutralBannerTemplate(180090, "Lumber Mill Banner", 23938),
            NeutralBannerTemplate(180091, "Mine Banner", 23937),
            ButtonBannerTemplate(180059, 6252, "Contested Banner", 0, 180102),
            ButtonBannerTemplate(180058, 6251, "Alliance Banner", 196608, 180100),
        ];

        private static GameObjectSpawn Spawn(uint guid, uint entry, (float X, float Y, float Z) at) => new()
        {
            Guid = guid, Entry = entry, MapId = 529, X = at.X, Y = at.Y, Z = at.Z, SpawnTimeSeconds = 0,
        };

        private static CreatureTemplate Master(uint entry, string name, uint faction) => new()
        {
            Entry = entry, Name = name, Faction = faction, NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster), DisplayIds = [49],
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
        };

        /// <summary>
        /// A neutral node banner as classic-db has it: type 10 (goober), display 6271, faction 0, flags 0, data0 (lockId) 1479, data3
        /// (autoCloseTime) 196608, data10 (spellId) the node's trigger spell, data11 (noDamageImmune) 1, data16 1.
        /// </summary>
        private static GameObjectTemplate NeutralBannerTemplate(uint entry, string name, uint triggerSpell)
        {
            uint[] data = new uint[GameObjectTemplate.DataCount];
            (data[0], data[3], data[10], data[11], data[16]) = (BannerLock, 196608, triggerSpell, 1, 1);
            return new GameObjectTemplate { Entry = entry, Type = (uint)GameObjectType.Goober, DisplayId = 6271, Name = name, Data = data };
        }

        /// <summary>
        /// A faction banner as classic-db has it: type 1 (button), faction 83, flags 32, data1 (lockId) 1479, data2 (autoCloseTime), data3
        /// (linkedTrap: the banner aura), data4 (noDamageImmune) 1, data5 1, data8 1.
        /// </summary>
        private static GameObjectTemplate ButtonBannerTemplate(uint entry, uint displayId, string name, uint autoClose, uint linkedTrap)
        {
            uint[] data = new uint[GameObjectTemplate.DataCount];
            (data[1], data[2], data[3], data[4], data[5], data[8]) = (BannerLock, autoClose, linkedTrap, 1, 1, 1);
            return new GameObjectTemplate
            {
                Entry = entry, Type = (uint)GameObjectType.Button, DisplayId = displayId, Name = name, Faction = 83, Flags = 32, Data = data,
            };
        }

        /// <summary>Lock.dbc 1479: case 1 is LOCK_KEY_SKILL (2) of lock type 17, no skill required.</summary>
        private static LockEntry Lock1479()
        {
            uint[] types = new uint[LockEntry.Cases];
            uint[] indexes = new uint[LockEntry.Cases];
            (types[1], indexes[1]) = (2, 17);
            return new LockEntry(BannerLock, types, indexes, new uint[LockEntry.Cases]);
        }
    }
}
