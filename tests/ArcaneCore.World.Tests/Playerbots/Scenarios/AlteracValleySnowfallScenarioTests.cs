using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The neutral Snowfall banner of Alterac Valley on its real classic-db shape: gameobject 90426, entry 180418, a goober locked by 1479 whose
/// data10 is 24677 (GY Mid Trigger) and whose data11 (noDamageImmune) is 0, on the event (3, 5) (Snowfall, neutral and controlled). The
/// Alliance bot casts Opening on it: the trigger spell's script clicks the banner, and the graveyard is assaulted by the Alliance (vmangos
/// BattleGroundAV.cpp:1319-1326, the first capture of Snowfall). A direct use of the banner is refused first.
/// </summary>
public sealed class AlteracValleySnowfallScenarioTests
{
    private sealed class Scenario(Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => "av-snowfall";

        public string Description => Name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    [Fact]
    public async Task Av_TheNeutralSnowfallBanner_IsAssaulted_ByTheOpeningSpell()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(AlteracValleySnowfallTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario(async context =>
        {
            (ScenarioBot ally, _, Battleground match) = await context.EnterMatchAsync(BattlegroundType.AlteracValley, "Scnsnwally", "Scnsnwhorde", TimeSpan.FromMinutes(3));
            var av = (AlteracValley)match;
            (float x, float y, float z) = AlteracValleySnowfallTestContent.Snowfall;

            await context.StepAsync("the Snowfall banner stands and Snowfall is neutral", async () =>
            {
                await context.PlaceAsync(ally, 30, x + 1f, y, z);
                await context.WaitUntilAsync("the banner stands", () => Banner(ally) is not null);
                ScenarioContext.ExpectEqual(AlteracValley.TeamNeutral, await context.ReadAsync(() => av.NodeOwner(AlteracValley.NodeSnowfallGrave)), "owner");
            });

            await context.StepAsync("a direct use of the banner is refused", async () =>
            {
                GameObjectUseResult used = await context.ReadAsync(() => Objects(ally).Use(ally.RequirePlayer(), Banner(ally)!.Guid));
                ScenarioContext.ExpectEqual(GameObjectUseResult.Locked, used, "direct use");
                ScenarioContext.ExpectEqual(AlteracValley.TeamNeutral, await context.ReadAsync(() => av.NodeOwner(AlteracValley.NodeSnowfallGrave)), "owner");
            });

            await context.StepAsync("the Opening cast assaults Snowfall for the Alliance", async () =>
            {
                (SpellCastResult opened, uint uses) = await context.ReadAsync(() =>
                {
                    GameObject banner = Banner(ally)!;
                    SpellCastResult result = context.Services.GetRequiredService<SpellFeature>().System.CastSpell(ally.RequirePlayer(),
                        BattlegroundBannerTestContent.OpeningSpell, new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = banner.Guid },
                        triggered: true);
                    return (result, banner.UseCount);
                });
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, opened, "Opening cast result");
                ScenarioContext.ExpectEqual(1u, uses, "banner uses (the open reached the goober)");
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => av.NodeOwner(AlteracValley.NodeSnowfallGrave)), "owner (Alliance)");
                ScenarioContext.ExpectEqual(AlteracValley.PointAssaulted, await context.ReadAsync(() => av.NodeState(AlteracValley.NodeSnowfallGrave)), "state");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    private static GameObjectMapSystem Objects(ScenarioBot bot) => bot.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!;

    private static GameObject? Banner(ScenarioBot bot)
        => Objects(bot).GameObjects.FirstOrDefault(g => g.Spawn?.Guid == AlteracValleySnowfallTestContent.SnowfallBanner && g.IsSpawned);
}

/// <summary>
/// Alterac Valley with only the Snowfall banner (classic-db z2815: gameobject 90426 of 180418 at its spawn, gameobject_battleground (90426, 3,
/// 5)), the start safe locations 611/610, a template of one to forty players per team, the battlemasters 7410 and 347, and the banner spells and
/// lock of <see cref="BattlegroundBannerTestContent"/>.
/// </summary>
internal static class AlteracValleySnowfallTestContent
{
    public const uint SnowfallBanner = 90426;
    private const uint Map = 30;

    public static readonly (float X, float Y, float Z) Snowfall = (-202.581f, -112.73f, 78.4876f);

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new Maps());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
        services.AddSingleton<IBattlegroundContentStore>(new Battlegrounds());
        var store = new Content();
        services.AddSingleton<ICreatureDataStore>(store);
        services.AddScoped<IGameObjectDataStore>(_ => store);
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(BattlegroundBannerTestContent.Extend(ScenarioTestContent.Spells())));
        BattlegroundBannerTestContent.RegisterSkills(services);
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
            new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),
            new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
            new FactionTemplateRecord(29, 76, 0, OwnMask: 4, FriendlyMask: 4, HostileMask: 8),
        ]));
        services.AddSingleton(new FactionCatalog(
        [
            new FactionRecord(72, 0, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Stormwind"),
            new FactionRecord(AlteracValley.FactionStormpike, 1, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Stormpike Guard"),
            new FactionRecord(AlteracValley.FactionFrostwolf, 2, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Frostwolf Clan"),
        ]));
    }

    private sealed class Maps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                new MapTemplate(Map, 0, MapType.Battleground, 2597, 80, 0, -1, 0, 0, "Alterac Valley", ""),
            ],
            [], [], [], []));
    }

    private sealed class Graveyards : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GraveyardContent(
            [
                new WorldSafeLoc(611, Map, 873.0f, -491.3f, 96.5f, 3.14f, "AV Alliance start"),
                new WorldSafeLoc(610, Map, -1437.67f, -610.09f, 51.16f, 0f, "AV Horde start"),
            ],
            []));
    }

    private sealed class Battlegrounds : IBattlegroundContentStore
    {
        public Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BattlegroundContent(
            [new BattlegroundTemplateRecord(1, 1, 40, 1, 60, 611, 610, 100, 0)],
            [],
            [new BattlegroundEventIndex(SnowfallBanner, AlteracValley.NodeSnowfallGrave, 5)],
            [new BattlemasterRecord(AlteracValleyTestContent.AllianceMasterEntry, 1), new BattlemasterRecord(AlteracValleyTestContent.HordeMasterEntry, 1)]));
    }

    private sealed class Content : ICreatureDataStore, IGameObjectDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                Npc(AlteracValleyTestContent.AllianceMasterEntry, "Thelman Slatefist", 12),
                Npc(AlteracValleyTestContent.HordeMasterEntry, "Grizzle Halfmane", 29),
            ],
            [
                new CreatureSpawn { Guid = 995001, Entry = AlteracValleyTestContent.AllianceMasterEntry, MapId = 0, X = ScenarioTestContent.StartX + 5f, Y = ScenarioTestContent.StartY, Z = ScenarioTestContent.StartZ },
                new CreatureSpawn { Guid = 995002, Entry = AlteracValleyTestContent.HordeMasterEntry, MapId = 1, X = WarsongGulchTestContent.OrcStartX + 5f, Y = WarsongGulchTestContent.OrcStartY, Z = WarsongGulchTestContent.OrcStartZ },
            ], [], [], []));

        /// <summary>
        /// classic-db gameobject_template 180418 'Snowfall Banner': type 10 (goober), display 6211, faction 0, flags 0, data0 (lockId) 1479,
        /// data3 (autoCloseTime) 196608, data10 (spellId) 24677, every other field 0.
        /// </summary>
        Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken)
        {
            uint[] data = new uint[GameObjectTemplate.DataCount];
            (data[0], data[3], data[10]) = (BattlegroundBannerTestContent.BannerLock, 196608, 24677);
            return Task.FromResult(new GameObjectContent(
                [new GameObjectTemplate { Entry = 180418, Type = (uint)GameObjectType.Goober, DisplayId = 6211, Name = "Snowfall Banner", Data = data }],
                [new GameObjectSpawn { Guid = SnowfallBanner, Entry = 180418, MapId = Map, X = Snowfall.X, Y = Snowfall.Y, Z = Snowfall.Z, Orientation = -0.715585f, SpawnTimeSeconds = 0 }],
                [BattlegroundBannerTestContent.Lock()], [], []));
        }

        private static CreatureTemplate Npc(uint entry, string name, uint faction) => new()
        {
            Entry = entry, Name = name, Faction = faction, NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster), DisplayIds = [49],
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
        };
    }
}
