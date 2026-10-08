using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Alterac Valley in the world, played by two managed bots (the "av-upgrades" scenario): both queue at their battlemasters and port into one
/// match; the Alliance bot hands its armor scraps to Murgot Deepforge through the quest opcodes and the match counts them (vmangos
/// HandleQuestComplete); with the scraps at five hundred and Stormpike honored, the bot opens Murgot's gossip, gets the quartermaster's
/// menu over the wire and buys the seasoned troops, which Murgot yells; then the Horde landmine spares the Horde bot and goes off under the
/// Alliance bot, and after the Horde landmine layer dies the mine does not come back (go_av_landmineAI).
/// </summary>
public sealed class AlteracValleyWorldScenarioTests
{
    private sealed class Scenario(Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => "av-upgrades";

        public string Description => Name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private const uint Map = 30;

    [Fact]
    public async Task Av_ScrapsTurnIn_QuartermasterUpgrade_AndTheLandmine()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(AlteracValleyTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario(async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(BattlegroundType.AlteracValley, "Scnavally", "Scnavhorde", TimeSpan.FromMinutes(3));
            var av = (AlteracValley)match;
            ObjectGuid murgot = ObjectGuid.WithEntry(HighGuid.Unit, AlteracValley.NpcMurgot, AlteracValleyTestContent.MurgotSpawn);

            await context.StepAsync("the Alliance bot hands twenty armor scraps to Murgot", async () =>
            {
                (float x, float y, float z) = AlteracValleyTestContent.Murgot;
                await context.PlaceAsync(ally, Map, x + 2f, y, z, MathF.PI);
                await context.GiveItemAsync(ally, AlteracValleyTestContent.ArmorScraps, 20);
                await context.WaitUntilAsync("Murgot is visible", () => ally.RequirePlayer().VisibleObjects.Contains(murgot));
                ScenarioContext.Expect(await ally.QuestHelloAsync(murgot), "hello refused");
                ScenarioContext.Expect(await ally.AcceptQuestAsync(murgot, AlteracValley.QuestAllianceScraps1), "accept refused");
                await context.WaitUntilAsync("the quest is taken and its scraps are in the bags",
                    () => QuestStatusOf(context, ally, AlteracValley.QuestAllianceScraps1) == QuestStatus.Complete);
                long mark = ally.Mark();
                ScenarioContext.Expect(await ally.CompleteQuestAsync(murgot, AlteracValley.QuestAllianceScraps1), "complete refused");
                ScenarioContext.Expect(await ally.ChooseQuestRewardAsync(murgot, AlteracValley.QuestAllianceScraps1), "reward refused");
                await ally.WaitForPacketAsync(WorldOpcode.SmsgQuestgiverQuestComplete, ScenarioDecoders.QuestComplete,
                    q => q.Quest == AlteracValley.QuestAllianceScraps1, mark);
                await context.WaitUntilAsync("the match counts the scraps", () => av.ArmorResources(Team.Alliance) == 20);
                ScenarioContext.Expect(av.IsActiveEvent(AlteracValley.EventSupplies100, 2), "the first turn-in clears the supply piles");
                ScenarioContext.ExpectEqual(0u, av.ArmorResources(Team.Horde), "Horde scraps");
            });

            await context.StepAsync("an honored Alliance bot buys the seasoned troops at Murgot's gossip", async () =>
            {
                await context.ReadAsync(() =>
                {
                    for (int i = 0; i < 24; i++)
                    {
                        av.HandleQuestComplete(ally.Guid, murgot, AlteracValley.QuestAllianceScraps2, AlteracValleyTestContent.ArmorScraps, 20);
                    }

                    return context.Services.GetRequiredService<ReputationFeature>().Service.SetReputation(ally.RequirePlayer(), AlteracValley.FactionStormpike, 9000);
                });
                ScenarioContext.ExpectEqual(500u, await context.ReadAsync(() => av.ArmorResources(Team.Alliance)), "scraps");

                long mark = ally.Mark();
                ScenarioContext.Expect(await ally.SendAsync(WorldOpcode.CmsgGossipHello, ScenarioPackets.Guid(murgot.Value)), "hello refused");
                GossipView menu = await ally.WaitForPacketAsync(WorldOpcode.SmsgGossipMessage, Gossip, g => g.Npc == murgot.Value, mark);
                ScenarioContext.ExpectEqual(6073u, menu.TextId, "npc text of the basic troops");
                ScenarioContext.ExpectEqual(2, menu.Options, "the next-upgrade line and the upgrade");

                mark = ally.Mark();
                ScenarioContext.Expect(await ally.SendAsync(WorldOpcode.CmsgGossipSelectOption, ScenarioPackets.GuidUInt32(murgot.Value, 1)), "select refused");
                await context.WaitUntilAsync("the troops are seasoned", () => av.ReinforcementLevel(Team.Alliance) == AlteracValley.TroopsSeasoned);
                await ally.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, p => p,
                    p => Encoding.UTF8.GetString(p).Contains("Seasoned units are entering the battle!", StringComparison.Ordinal), mark);
            });

            await context.StepAsync("the Horde landmine spares the Horde bot and goes off under the Alliance bot", async () =>
            {
                (float x, float y, float z) = AlteracValleyTestContent.Landmine;
                ScenarioContext.Expect(await context.ReadAsync(() => LandmineSpawned(ally)), "the landmine stands");
                await context.PlaceAsync(horde, Map, x, y, z);
                await context.IdleAsync(TimeSpan.FromSeconds(3));
                ScenarioContext.Expect(await context.ReadAsync(() => LandmineSpawned(ally)), "the landmine went off under a friend");
                await context.PlaceAsync(horde, Map, x + 40f, y, z);
                await context.PlaceAsync(ally, Map, x, y, z);
                await context.WaitUntilAsync("the landmine went off", () => !LandmineSpawned(ally));
            });

            await context.StepAsync("after its layer died the landmine does not come back", async () =>
            {
                await context.PlaceAsync(ally, Map, AlteracValleyTestContent.Murgot.X + 2f, AlteracValleyTestContent.Murgot.Y, AlteracValleyTestContent.Murgot.Z);
                await context.ReadAsync(() =>
                {
                    av.HandleKillUnit(AlteracValley.NpcLandminesLayerHorde, BattlegroundConstants.EventNone, ally.Guid);
                    return true;
                });
                await context.IdleAsync(TimeSpan.FromSeconds(30)); // the mine's respawn time is 5 s
                ScenarioContext.Expect(!await context.ReadAsync(() => LandmineSpawned(ally)), "the landmine came back");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(6) });
        Assert.True(report.Passed, report.ToString());
    }

    private static QuestStatus? QuestStatusOf(ScenarioContext context, ScenarioBot bot, uint quest)
        => context.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(bot.RequirePlayer())?.Quests.Get(quest)?.Status;

    private static bool LandmineSpawned(ScenarioBot bot)
        => bot.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!.GameObjects.Any(g => g.Spawn?.Guid == AlteracValleyTestContent.LandmineSpawn && g.IsSpawned);

    private sealed record GossipView(ulong Npc, uint TextId, int Options, IReadOnlyList<uint> Quests);

    /// <summary>SMSG_GOSSIP_MESSAGE (NpcPackets.GossipMessage): npc, text, the lines, the quests.</summary>
    private static GossipView Gossip(byte[] payload)
    {
        var r = new PacketReader(payload);
        ulong npc = r.ReadUInt64();
        uint text = r.ReadUInt32();
        uint options = r.ReadUInt32();
        for (uint i = 0; i < options; i++)
        {
            r.ReadUInt32();
            r.ReadByte();
            r.ReadByte();
            r.ReadCString();
        }

        uint count = r.ReadUInt32();
        var quests = new List<uint>();
        for (uint i = 0; i < count; i++)
        {
            quests.Add(r.ReadUInt32());
            r.ReadUInt32();
            r.ReadInt32();
            r.ReadCString();
        }

        return new GossipView(npc, text, (int)options, quests);
    }
}

/// <summary>
/// Synthetic Alterac Valley content: map 30 with the start safe locations 611/610, a template of one to forty players per team from level 1,
/// two battlemasters (the Warsong Gulch ones' places), Murgot Deepforge giving and taking the first armor-scraps quest, Armor Scraps, the
/// Stormpike and Frostwolf reputation factions, and one Horde landmine (179324, event (100, 0)) with a five-yard trigger and no spell.
/// </summary>
internal static class AlteracValleyTestContent
{
    public const uint AllianceMasterEntry = 7410;    // classic-db battlemaster_entry (7410, 1)
    public const uint HordeMasterEntry = 347;        // classic-db battlemaster_entry (347, 1)
    public const uint AllianceMasterSpawn = 993001;
    public const uint HordeMasterSpawn = 993002;
    public const uint MurgotSpawn = 993003;
    public const uint LandmineSpawn = 993004;
    public const uint ArmorScraps = 17422;

    public static readonly (float X, float Y, float Z) AllianceStart = (873.0f, -491.3f, 96.5f);
    public static readonly (float X, float Y, float Z) Murgot = (880.0f, -480.0f, 96.5f);
    public static readonly (float X, float Y, float Z) Landmine = (900.0f, -500.0f, 96.5f);

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new Maps());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
        services.AddSingleton<IBattlegroundContentStore>(new Battlegrounds());
        var store = new Content();
        services.AddSingleton<ICreatureDataStore>(store);
        services.AddSingleton<IQuestContentStore>(store);
        services.AddScoped<IGameObjectDataStore>(_ => store);
        var items = new InMemoryItemTemplateSource();
        items.Templates.Add(new ItemTemplate { Entry = ArmorScraps, Name = "Armor Scraps", Class = 12, Quality = 1, Stackable = 250 });
        services.AddSingleton<IItemTemplateSource>(items);
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),   // human player
            new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),   // orc player
            new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),  // Stormwind
            new FactionTemplateRecord(29, 76, 0, OwnMask: 4, FriendlyMask: 4, HostileMask: 8),  // Orgrimmar
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

    private const uint Map = 30;

    private sealed class Graveyards : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GraveyardContent(
            [
                new WorldSafeLoc(611, Map, AllianceStart.X, AllianceStart.Y, AllianceStart.Z, 3.14f, "AV Alliance start"),
                new WorldSafeLoc(610, Map, -1437.67f, -610.09f, 51.16f, 0f, "AV Horde start"),
            ],
            []));
    }

    private sealed class Battlegrounds : IBattlegroundContentStore
    {
        public Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BattlegroundContent(
            [new BattlegroundTemplateRecord(1, 1, 40, 1, 60, 611, 610, 100, 0)],
            [],
            [new BattlegroundEventIndex(LandmineSpawn, AlteracValley.EventLandminesHorde, 0)],
            [new BattlemasterRecord(AllianceMasterEntry, 1), new BattlemasterRecord(HordeMasterEntry, 1)]));
    }

    private sealed class Content : ICreatureDataStore, IQuestContentStore, IGameObjectDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                Npc(AllianceMasterEntry, "Thelman Slatefist", 12, NpcFlags.Gossip | NpcFlags.BattleMaster),
                Npc(HordeMasterEntry, "Grizzle Halfmane", 29, NpcFlags.Gossip | NpcFlags.BattleMaster),
                Npc(AlteracValley.NpcMurgot, "Murgot Deepforge", 12, NpcFlags.Gossip | NpcFlags.QuestGiver),
            ],
            [
                new CreatureSpawn { Guid = AllianceMasterSpawn, Entry = AllianceMasterEntry, MapId = 0, X = ScenarioTestContent.StartX + 5f, Y = ScenarioTestContent.StartY, Z = ScenarioTestContent.StartZ },
                new CreatureSpawn { Guid = HordeMasterSpawn, Entry = HordeMasterEntry, MapId = 1, X = WarsongGulchTestContent.OrcStartX + 5f, Y = WarsongGulchTestContent.OrcStartY, Z = WarsongGulchTestContent.OrcStartZ },
                new CreatureSpawn { Guid = MurgotSpawn, Entry = AlteracValley.NpcMurgot, MapId = Map, X = Murgot.X, Y = Murgot.Y, Z = Murgot.Z },
            ], [], [], []));

        Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new QuestContent(
            [new QuestTemplate
            {
                Entry = AlteracValley.QuestAllianceScraps1, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Armor Scraps",
                ReqItemId1 = ArmorScraps, ReqItemCount1 = 20,
            }],
            [new CreatureQuestRelation { Id = AlteracValley.NpcMurgot, Quest = AlteracValley.QuestAllianceScraps1 }],
            [new CreatureQuestRelation { Id = AlteracValley.NpcMurgot, Quest = AlteracValley.QuestAllianceScraps1 }]));

        Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken)
        {
            uint[] data = new uint[GameObjectTemplate.DataCount];
            data[2] = 5; // trap.radius
            var mine = new GameObjectTemplate { Entry = AlteracValley.GameObjectLandmineHorde, Type = (uint)GameObjectType.Trap, DisplayId = 4392, Name = "Landmine", Data = data };
            return Task.FromResult(new GameObjectContent([mine],
                [new GameObjectSpawn { Guid = LandmineSpawn, Entry = AlteracValley.GameObjectLandmineHorde, MapId = Map, X = Landmine.X, Y = Landmine.Y, Z = Landmine.Z, SpawnTimeSeconds = 5 }],
                [], [], []));
        }

        private static CreatureTemplate Npc(uint entry, string name, uint faction, NpcFlags flags) => new()
        {
            Entry = entry, Name = name, Faction = faction, NpcFlags = (uint)flags, DisplayIds = [49],
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
        };
    }
}
