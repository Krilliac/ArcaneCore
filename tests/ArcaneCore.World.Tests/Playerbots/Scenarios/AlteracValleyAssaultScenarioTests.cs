using System.Text;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
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
using ArcaneCore.World.Tests.Duel;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The Alterac Valley assaults in the world, played by two managed bots over the wire (the "av-shredder" and "av-assault" scenarios):
/// <list type="bullet">
/// <item>the Alliance bot casts Create Shredder: the shredder comes marked as made by Control Shredder with the bot as its creator
/// (AVCreateShredderScript) and the bot is the team's shredder owner; once the bot controls its shredder a second Create Shredder is refused
/// with SPELL_FAILED_SPELL_UNAVAILABLE (BattleGroundAV::CheckSpellCast);</item>
/// <item>the ground assault: with the mine supplies in, the Stormpike Quartermaster's gossip offers the assault; choosing it closes the menu,
/// the quartermaster calls the patrol and hands the bot the Stormpike Assault Orders, and summons Field Marshal Teravaine with ten commandos;
/// the bot turns the orders in to the marshal ("Begin the Attack!"), who sets off on his escort, stops at his second point for his speech
/// and six seconds later his commandos give their war cry and fall in behind him.</item>
/// </list>
/// </summary>
public sealed class AlteracValleyAssaultScenarioTests
{
    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private const uint Map = 30;

    [Fact]
    public async Task Av_CreateShredder_MarksTheShredder_AndASecondOneIsRefusedWhileTheFirstIsControlled()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(AlteracValleyAssaultTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario("av-shredder", async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(BattlegroundType.AlteracValley, "Scnshrally", "Scnshrhorde", TimeSpan.FromMinutes(3));
            var av = (AlteracValley)match;
            await context.LearnSpellAsync(ally, AlteracValley.SpellSummonShredderAlliance);
            await context.LearnSpellAsync(ally, AlteracValleyAssaultTestContent.ControlShredder);
            Creature? shredder = null;

            await context.StepAsync("the Alliance bot casts Create Shredder", async () =>
            {
                (float x, float y, float z) = AlteracValleyAssaultTestContent.Field;
                await context.PlaceAsync(ally, Map, x, y, z);
                long mark = ally.Mark();
                ScenarioContext.Expect(await ally.CastAsync(AlteracValley.SpellSummonShredderAlliance), "cast refused");
                await ally.WaitForPacketAsync(WorldOpcode.SmsgSpellGo, ScenarioDecoders.SpellGo, g => g.SpellId == AlteracValley.SpellSummonShredderAlliance, mark);
                await context.WaitUntilAsync("the shredder is in the world", () => FindShredder(ally) is not null);
                shredder = await context.ReadAsync(() => FindShredder(ally)!);
                await context.WaitUntilAsync("the bot sees its shredder", () => ally.RequirePlayer().VisibleObjects.Contains(shredder!.Guid));
                (uint createdBy, ObjectGuid creator, ObjectGuid owner) = await context.ReadAsync(() =>
                    (shredder!.GetUInt32(UpdateFields.UnitCreatedBySpell), shredder.CreatorGuid, av.ShredderOwner(Team.Alliance)));
                ScenarioContext.ExpectEqual(AlteracValleyAssaultTestContent.ControlShredderTrigger, createdBy, "UNIT_CREATED_BY_SPELL (AVCreateShredderScript)");
                ScenarioContext.ExpectEqual(ally.Guid, creator, "the creator (AVCreateShredderScript)");
                ScenarioContext.ExpectEqual(ally.Guid, owner, "the team's shredder owner (CheckSpellCast)");
            });

            await context.StepAsync("the bot takes control of its shredder", async () =>
            {
                ScenarioContext.Expect(await ally.CastAsync(AlteracValleyAssaultTestContent.ControlShredder, shredder!.Guid), "control refused");
                await context.ExpectAsync(ally, "the bot controls the shredder", p => p.CharmGuid == shredder!.Guid);
            });

            await context.StepAsync("a second shredder is refused while the bot controls the first", async () =>
            {
                long mark = ally.Mark();
                ScenarioContext.Expect(await ally.CastAsync(AlteracValley.SpellSummonShredderAlliance), "cast not sent");
                CastResultView result = await ally.WaitForPacketAsync(WorldOpcode.SmsgCastResult, ScenarioDecoders.CastResult,
                    r => r.SpellId == AlteracValley.SpellSummonShredderAlliance, mark);
                ScenarioContext.Expect(!result.Success, "the second shredder was allowed");
                ScenarioContext.ExpectEqual(SpellCastResult.SpellUnavailable, result.Reason, "the refusal");
                ScenarioContext.ExpectEqual(1, await context.ReadAsync(() => Shredders(ally).Count()), "shredders in the world");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(6) });
        Assert.True(report.Passed, report.ToString());
    }

    [Fact]
    public async Task Av_GroundAssault_TheQuartermasterLaunchesIt_AndTheMarshalLeadsTheAttack()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(AlteracValleyAssaultTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario("av-assault", async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(BattlegroundType.AlteracValley, "Scnasltally", "Scnasltorde", TimeSpan.FromMinutes(3));
            var av = (AlteracValley)match;
            ObjectGuid quartermaster = ObjectGuid.WithEntry(HighGuid.Unit, AlteracValley.NpcQuartermasterAlliance, AlteracValleyAssaultTestContent.QuartermasterSpawn);
            Creature? marshal = null;

            await context.StepAsync("with the mine supplies in, the quartermaster offers the ground assault", async () =>
            {
                (float x, float y, float z) = AlteracValleyAssaultTestContent.Quartermaster;
                await context.PlaceAsync(ally, Map, x + 2f, y, z, MathF.PI);
                await context.ReadAsync(() =>
                {
                    av.AddChallengeCounter(Team.Alliance, AlteracValley.ChallengeIrondeepGround, 280);
                    return true;
                });
                await context.WaitUntilAsync("the quartermaster is visible", () => ally.RequirePlayer().VisibleObjects.Contains(quartermaster));
                long mark = ally.Mark();
                ScenarioContext.Expect(await ally.SendAsync(WorldOpcode.CmsgGossipHello, ScenarioPackets.Guid(quartermaster.Value)), "hello refused");
                GossipView menu = await ally.WaitForPacketAsync(WorldOpcode.SmsgGossipMessage, Gossip, g => g.Npc == quartermaster.Value, mark);
                ScenarioContext.ExpectEqual(AvCollectorGossip.NpcTextQuartermaster, menu.TextId, "npc text");
                ScenarioContext.ExpectEqual(3, menu.Lines.Count, "the supplies line, the assault and the vendor");
                ScenarioContext.ExpectEqual("text 9050", menu.Lines[1], "the assault line (GOSSIP_ASSAULT_GROUND)");
                ScenarioContext.Expect(menu.Quests.Contains(AlteracValley.QuestAllianceNearMine), "the quest list");
            });

            await context.StepAsync("the bot launches it: the orders, the patrol call and the marshal with ten commandos", async () =>
            {
                long mark = ally.Mark();
                ScenarioContext.Expect(await ally.SendAsync(WorldOpcode.CmsgGossipSelectOption, ScenarioPackets.GuidUInt32(quartermaster.Value, 1)), "select refused");
                await ally.WaitForPacketAsync(WorldOpcode.SmsgGossipComplete, p => p, null, mark);
                await ally.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, p => p, p => Contains(p, "text 8907"), mark); // SAY_PATROL
                await context.WaitUntilAsync("the orders are in the bags",
                    () => ally.RequirePlayer().Inventory.GetItemCount(AlteracValley.ItemAssaultOrdersStormpike) == 1);
                await context.WaitUntilAsync("the marshal and his commandos came", () => Creatures(ally, AlteracValley.NpcFieldMarshalTeravaine).Count() == 1
                    && Creatures(ally, AlteracValley.NpcStormpikeCommando).Count() == 10);
                marshal = await context.ReadAsync(() => Creatures(ally, AlteracValley.NpcFieldMarshalTeravaine).Single());
                ScenarioContext.ExpectEqual(0u, await context.ReadAsync(() => av.ChallengeCounter(Team.Alliance, AlteracValley.ChallengeIrondeepGround)), "the supplies are spent");
                await context.WaitUntilAsync("the bot sees the marshal", () => ally.RequirePlayer().VisibleObjects.Contains(marshal!.Guid));
            });

            await context.StepAsync("the bot hands the orders to the marshal", async () =>
            {
                ObjectGuid guid = marshal!.Guid;
                (float mx, float my, float mz) = await context.ReadAsync(() => (marshal.X, marshal.Y, marshal.Z));
                await context.PlaceAsync(ally, Map, mx + 2f, my, mz, MathF.PI);
                ScenarioContext.Expect(await ally.QuestHelloAsync(guid), "hello refused");
                ScenarioContext.Expect(await ally.AcceptQuestAsync(guid, AlteracValley.QuestTroopsOrderAlliance), "accept refused");
                await context.WaitUntilAsync("the quest is complete", () => QuestStatusOf(context, ally, AlteracValley.QuestTroopsOrderAlliance) == QuestStatus.Complete);
                long mark = ally.Mark();
                ScenarioContext.Expect(await ally.CompleteQuestAsync(guid, AlteracValley.QuestTroopsOrderAlliance), "complete refused");
                ScenarioContext.Expect(await ally.ChooseQuestRewardAsync(guid, AlteracValley.QuestTroopsOrderAlliance), "reward refused");
                await ally.WaitForPacketAsync(WorldOpcode.SmsgQuestgiverQuestComplete, ScenarioDecoders.QuestComplete,
                    q => q.Quest == AlteracValley.QuestTroopsOrderAlliance, mark);
                await context.WaitUntilAsync("the marshal sets off", () => marshal.AI is AvTroopsChiefAI chief && chief.HasEscortState(EscortAI.EscortState.Escorting));
            });

            await context.StepAsync("at his second point the marshal gives his speech, and his commandos rally behind him", async () =>
            {
                long mark = ally.Mark();
                await ally.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, p => p, p => Contains(p, "text 8906"), mark); // SAY_RAMRIDER_CMD
                await ally.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, p => p, p => Contains(p, "text 8908"), mark); // SAY_WARCRY_ALLIANCE
                await context.WaitUntilAsync("the commandos follow the marshal", () => Creatures(ally, AlteracValley.NpcStormpikeCommando)
                    .All(c => c.Motion.CurrentType == MovementGeneratorType.Follow && ReferenceEquals(c.Motion.TargetedUnit, marshal)));
                await context.WaitUntilAsync("the marshal goes on", () => marshal!.AI is AvTroopsChiefAI chief
                    && !chief.HasEscortState(EscortAI.EscortState.Paused) && chief.CurrentWaypointIndex > 3);
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(40), MaxDuration = TimeSpan.FromMinutes(6) });
        Assert.True(report.Passed, report.ToString());
    }

    private static bool Contains(byte[] payload, string text) => Encoding.UTF8.GetString(payload).Contains(text, StringComparison.Ordinal);

    private static IEnumerable<Creature> Creatures(ScenarioBot bot, uint entry)
        => bot.RequirePlayer().Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Where(c => c.Entry == entry && c.IsAlive);

    private static IEnumerable<Creature> Shredders(ScenarioBot bot) => Creatures(bot, AlteracValley.NpcShredderAlliance);

    private static Creature? FindShredder(ScenarioBot bot) => Shredders(bot).FirstOrDefault();

    private static QuestStatus? QuestStatusOf(ScenarioContext context, ScenarioBot bot, uint quest)
        => context.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(bot.RequirePlayer())?.Quests.Get(quest)?.Status;

    private sealed record GossipView(ulong Npc, uint TextId, IReadOnlyList<string> Lines, IReadOnlyList<uint> Quests);

    /// <summary>SMSG_GOSSIP_MESSAGE (NpcPackets.GossipMessage): npc, text, the lines, the quests.</summary>
    private static GossipView Gossip(byte[] payload)
    {
        var r = new PacketReader(payload);
        ulong npc = r.ReadUInt64();
        uint text = r.ReadUInt32();
        uint options = r.ReadUInt32();
        var lines = new List<string>();
        for (uint i = 0; i < options; i++)
        {
            r.ReadUInt32();
            r.ReadByte();
            r.ReadByte();
            lines.Add(r.ReadCString());
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

        return new GossipView(npc, text, lines, quests);
    }
}

/// <summary>
/// Synthetic Alterac Valley assault content: map 30 with the start safe locations 611/610, a template of one to forty players per team, the
/// two battlemasters, the Stormpike Quartermaster (12096) by the Alliance troops' summoning place with its mine-supply quests, Field Marshal
/// Teravaine (13446) and the Stormpike Commandos (13524) as the quartermaster summons them, "Begin the Attack!" (6846) at the marshal for the
/// Stormpike Assault Orders, the marshal's escort path (creature_movement_template path 0, westward from his summoning place, two yards a
/// point), the Stormpike shredder (13416), Create Shredder 21565 and its Control Shredder 21566 reduced to what the scenario needs (the real
/// 21566 possesses the shredder through a script target, which this server does not resolve, so it is a dummy here and the bot takes control
/// with <see cref="ControlShredder"/>), and the texts the scripts say ("text {id}").
/// </summary>
internal static class AlteracValleyAssaultTestContent
{
    public const uint AllianceMasterSpawn = 994001;
    public const uint HordeMasterSpawn = 994002;
    public const uint QuartermasterSpawn = 994003;

    /// <summary>A possession of a single unit target (MOD_POSSESS, up to level 60) the bot takes its shredder with.</summary>
    public const uint ControlShredder = 991_401;

    /// <summary>Control Shredder (Alliance), the spell the second effect of Create Shredder triggers.</summary>
    public const uint ControlShredderTrigger = 21566;

    public const uint MineSuppliesIrondeep = 17522;
    public const uint MineSuppliesColdtooth = 17542;

    public static readonly (float X, float Y, float Z) AllianceStart = (873.0f, -491.3f, 96.5f);
    public static readonly (float X, float Y, float Z) Quartermaster = (-238.0f, -424.0f, 20.0f);
    public static readonly (float X, float Y, float Z) Field = (500.0f, -300.0f, 40.0f);

    private static readonly int[] s_texts =
    [
        (int)AvCollectorGossip.GossipGroundAssault, (int)AvCollectorGossip.GossipAssaultGround, (int)AlteracValley.GossipTextBrowseGoods,
        AvCollectorGossip.SayPatrol, AvEventAI.SayRamRiderCommander, AvEventAI.SayWarcryAlliance,
    ];

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
        items.Templates.Add(new ItemTemplate { Entry = AlteracValley.ItemAssaultOrdersStormpike, Name = "Stormpike Assault Orders", Class = 12, Quality = 1, Stackable = 1 });
        items.Templates.Add(new ItemTemplate { Entry = MineSuppliesIrondeep, Name = "Irondeep Supplies", Class = 12, Quality = 1, Stackable = 250 });
        items.Templates.Add(new ItemTemplate { Entry = MineSuppliesColdtooth, Name = "Coldtooth Supplies", Class = 12, Quality = 1, Stackable = 250 });
        services.AddSingleton<IItemTemplateSource>(items);
        SpellContent duel = DuelWorldHost.Content();
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(ClassScriptScenarioContent.Extend(duel with
        {
            Spells = [.. duel.Spells, .. ProcScenarioContent.Spells, .. UnitControlScenarioContent.Spells, .. Spells],
        })));
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

    private static IReadOnlyList<SpellTemplateRow> Spells =>
    [
        // spell_template 21565 Create Shredder (Alliance): SUMMON_WILD of 13416, then TRIGGER_SPELL 21566 on the caster; instant here.
        new SpellTemplateRow
        {
            Id = AlteracValley.SpellSummonShredderAlliance, SpellName = "Create Shredder", AttributesEx = 1025, RangeIndex = 1, SpellLevel = 60,
            Effect1 = 41, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1, EffectMiscValue1 = (int)AlteracValley.NpcShredderAlliance,
            Effect2 = 64, EffectImplicitTargetA2 = 1, EffectTriggerSpell2 = ControlShredderTrigger,
        },
        new SpellTemplateRow
        {
            Id = ControlShredderTrigger, SpellName = "Control Shredder", Attributes = 256, RangeIndex = 1,
            Effect1 = 6, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 4,
        },
        new SpellTemplateRow
        {
            Id = ControlShredder, SpellName = "Scenario Control Shredder", School = 0, RangeIndex = 4, DurationIndex = 4, SpellVisual = 1,
            Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = 59, EffectImplicitTargetA1 = 25, EffectApplyAuraName1 = 2,
        },
    ];

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
            [],
            [new BattlemasterRecord(AlteracValleyTestContent.AllianceMasterEntry, 1), new BattlemasterRecord(AlteracValleyTestContent.HordeMasterEntry, 1)]));
    }

    private sealed class Content : ICreatureDataStore, IQuestContentStore, IGameObjectDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken)
        {
            var texts = new BroadcastTextCatalog(s_texts.Select(id => new BroadcastText((uint)id, $"text {id}", string.Empty, 1, 0, 0, [0, 0, 0], [0, 0, 0])));
            (uint, uint, CreatureWaypoint)[] path =
                [.. Enumerable.Range(0, 49).Select(i => (AlteracValley.NpcFieldMarshalTeravaine, EscortAI.EscortPathId,
                    new CreatureWaypoint((uint)i, -246f - (2f * i), -431f, 20f, 0, 0)))];
            return Task.FromResult(new CreatureContent(
                [
                    Npc(AlteracValleyTestContent.AllianceMasterEntry, "Thelman Slatefist", 12, NpcFlags.Gossip | NpcFlags.BattleMaster),
                    Npc(AlteracValleyTestContent.HordeMasterEntry, "Grizzle Halfmane", 29, NpcFlags.Gossip | NpcFlags.BattleMaster),
                    Npc(AlteracValley.NpcQuartermasterAlliance, "Stormpike Quartermaster", 12, NpcFlags.Gossip | NpcFlags.QuestGiver | NpcFlags.Vendor),
                    Npc(AlteracValley.NpcFieldMarshalTeravaine, "Field Marshal Teravaine", 12, NpcFlags.Gossip | NpcFlags.QuestGiver),
                    Npc(AlteracValley.NpcStormpikeCommando, "Stormpike Commando", 12, NpcFlags.None),
                    Npc(AlteracValley.NpcShredderAlliance, "Stormpike Shredder", 12, NpcFlags.None),
                ],
                [
                    new CreatureSpawn { Guid = AllianceMasterSpawn, Entry = AlteracValleyTestContent.AllianceMasterEntry, MapId = 0, X = ScenarioTestContent.StartX + 5f, Y = ScenarioTestContent.StartY, Z = ScenarioTestContent.StartZ },
                    new CreatureSpawn { Guid = HordeMasterSpawn, Entry = AlteracValleyTestContent.HordeMasterEntry, MapId = 1, X = WarsongGulchTestContent.OrcStartX + 5f, Y = WarsongGulchTestContent.OrcStartY, Z = WarsongGulchTestContent.OrcStartZ },
                    new CreatureSpawn { Guid = QuartermasterSpawn, Entry = AlteracValley.NpcQuartermasterAlliance, MapId = Map, X = Quartermaster.X, Y = Quartermaster.Y, Z = Quartermaster.Z },
                ],
                [], [], [], new CreatureAiContent([], [], texts), entryWaypoints: path));
        }

        Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new QuestContent(
            [
                Quest(AlteracValley.QuestAllianceNearMine, "Irondeep Supplies", MineSuppliesIrondeep, 10),
                Quest(AlteracValley.QuestAllianceOtherMine, "Coldtooth Supplies", MineSuppliesColdtooth, 10),
                Quest(AlteracValley.QuestTroopsOrderAlliance, "Begin the Attack!", AlteracValley.ItemAssaultOrdersStormpike, 1),
            ],
            [
                new CreatureQuestRelation { Id = AlteracValley.NpcQuartermasterAlliance, Quest = AlteracValley.QuestAllianceNearMine },
                new CreatureQuestRelation { Id = AlteracValley.NpcQuartermasterAlliance, Quest = AlteracValley.QuestAllianceOtherMine },
                new CreatureQuestRelation { Id = AlteracValley.NpcFieldMarshalTeravaine, Quest = AlteracValley.QuestTroopsOrderAlliance },
            ],
            [
                new CreatureQuestRelation { Id = AlteracValley.NpcQuartermasterAlliance, Quest = AlteracValley.QuestAllianceNearMine },
                new CreatureQuestRelation { Id = AlteracValley.NpcQuartermasterAlliance, Quest = AlteracValley.QuestAllianceOtherMine },
                new CreatureQuestRelation { Id = AlteracValley.NpcFieldMarshalTeravaine, Quest = AlteracValley.QuestTroopsOrderAlliance },
            ]));

        Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new GameObjectContent([], [], [], [], []));

        private static QuestTemplate Quest(uint id, string title, uint item, uint count) => new()
        {
            Entry = id, Method = 2, MinLevel = 1, QuestLevel = 1, Title = title, ReqItemId1 = item, ReqItemCount1 = count,
        };

        private static CreatureTemplate Npc(uint entry, string name, uint faction, NpcFlags flags) => new()
        {
            Entry = entry, Name = name, Faction = faction, NpcFlags = (uint)flags, DisplayIds = [49],
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
        };
    }
}
