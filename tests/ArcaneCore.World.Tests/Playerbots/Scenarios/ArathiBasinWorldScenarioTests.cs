using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Arathi Basin in the world: two managed bots queue at their battlemasters, port into one Arathi Basin match, and the stables banner is opened
/// (the open-lock path a client's opening spell takes): the node is claimed, its banner events swap the neutral banner for the contested one a
/// second later (the event spawn gate), a minute later the node is occupied and its banner appears five seconds after, and resources tick to
/// the world state every client of the match receives. The Horde then assaults the node.
/// </summary>
public sealed class ArathiBasinWorldScenarioTests
{
    private sealed class Scenario(Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => "ab-node";

        public string Description => Name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    [Fact]
    public async Task Ab_TheStablesBanner_IsClaimed_Occupied_TicksResources_AndIsAssaulted()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(ArathiBasinTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario(async context =>
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
                GameObjectUseResult opened = await context.ReadAsync(() => OpenBanner(ally, ArathiBasinTestContent.NeutralBanner));
                ScenarioContext.ExpectEqual(GameObjectUseResult.Ok, opened, "open-lock result");
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
                GameObjectUseResult opened = await context.ReadAsync(() => OpenBanner(horde, ArathiBasinTestContent.AllianceBanner));
                ScenarioContext.ExpectEqual(GameObjectUseResult.Ok, opened, "open-lock result");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusHordeContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AbScore)ab.ScoreOf(horde.Guid)!).BasesAssaulted), "Horde bases assaulted");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    private static GameObjectMapSystem Objects(ScenarioBot bot) => bot.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!;

    private static bool Spawned(ScenarioBot bot, uint spawnGuid) => Objects(bot).GameObjects.Any(g => g.Spawn?.Guid == spawnGuid && g.IsSpawned);

    /// <summary>What the opening spell's open-lock effect does (Skills/GatheringSpells: <c>GameObjectMapSystem.OpenLock</c>).</summary>
    private static GameObjectUseResult OpenBanner(ScenarioBot bot, uint spawnGuid)
    {
        GameObjectMapSystem objects = Objects(bot);
        GameObject banner = objects.GameObjects.Single(g => g.Spawn?.Guid == spawnGuid && g.IsSpawned);
        return objects.OpenLock(bot.RequirePlayer(), banner.Guid, LockType.Open);
    }
}

/// <summary>
/// Synthetic Arathi Basin content: map 529, the start safe locations 890/889 and the stables graveyard 895, a template of one to fifteen players
/// per team from level 1, the classic-db battlemasters 857 and 907, and the stables' three banners (buttons with <c>noDamageImmune</c>) on the
/// events (0, 0) neutral, (0, 1) Alliance contested and (0, 3) Alliance occupied.
/// </summary>
internal static class ArathiBasinTestContent
{
    public const uint NeutralBanner = 5290025;
    public const uint AllianceContestedBanner = 5290015;
    public const uint AllianceBanner = 5290005;

    public static readonly (float X, float Y, float Z) Stables = (1166.79f, 1200.13f, -56.70f);

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new Maps());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
        services.AddSingleton<IBattlegroundContentStore>(new Battlegrounds());
        var store = new Content();
        services.AddSingleton<ICreatureDataStore>(store);
        services.AddScoped<IGameObjectDataStore>(_ => store);
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
            new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),
            new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
            new FactionTemplateRecord(29, 76, 0, OwnMask: 4, FriendlyMask: 4, HostileMask: 8),
        ]));
    }

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
                new BattlegroundEventIndex(NeutralBanner, ArathiBasin.NodeStables, ArathiBasin.StatusNeutral),
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
            [Banner(180087, "Stable Banner"), Banner(180085, "Contested Banner"), Banner(180058, "Alliance Banner")],
            [
                Spawn(NeutralBanner, 180087),
                Spawn(AllianceContestedBanner, 180085),
                Spawn(AllianceBanner, 180058),
            ],
            [], [], []));

        private static GameObjectSpawn Spawn(uint guid, uint entry) => new()
        {
            Guid = guid, Entry = entry, MapId = 529, X = Stables.X, Y = Stables.Y, Z = Stables.Z, SpawnTimeSeconds = 0,
        };

        private static CreatureTemplate Master(uint entry, string name, uint faction) => new()
        {
            Entry = entry, Name = name, Faction = faction, NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster), DisplayIds = [49],
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
        };

        /// <summary>An Arathi Basin banner: a button whose data4 (<c>noDamageImmune</c>) is set (vmangos Spell::EffectOpenLock).</summary>
        private static GameObjectTemplate Banner(uint entry, string name)
        {
            uint[] data = new uint[GameObjectTemplate.DataCount];
            data[4] = 1;
            return new GameObjectTemplate { Entry = entry, Type = (uint)GameObjectType.Button, DisplayId = 5651, Name = name, Data = data };
        }
    }
}
