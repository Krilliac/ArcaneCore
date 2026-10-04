using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Exploration quests credited by the imported <c>areatrigger_involvedrelation</c> table (docs/areas/area-triggers.md): the relation lives
/// in <see cref="QuestContent.AreaTriggerQuests"/> and the immutable <see cref="QuestStore"/>, not in <c>Quests:AreaTriggerQuests</c>
/// (which stays an additive override). Each test builds the real quest service over the real inventory.
/// </summary>
public sealed class QuestExplorationTests
{
    private const uint Quest = 910100;
    private const uint OtherQuest = 910101;
    private const uint Trigger = 4001;
    private const uint OtherTrigger = 4002;

    private static QuestTemplate Exploration(uint id) => new()
    {
        Entry = id, Method = 2, QuestLevel = 1, Title = $"Exploration {id}", SpecialFlags = (byte)QuestSpecialFlags.ExplorationOrEvent,
        RequestItemsText = "Seen it?", Details = "Go look.", Objectives = "Explore.",
    };

    private static CreatureQuestRelation Relation(uint trigger, uint quest) => new() { Id = trigger, Quest = quest };

    [Fact]
    public void Store_IndexesTheRelationsByTrigger_InTableOrder_AndDropsUnknownQuests()
    {
        var store = new QuestStore(new QuestContent([Exploration(Quest), Exploration(OtherQuest)], [], [])
        {
            AreaTriggerQuests = [Relation(Trigger, OtherQuest), Relation(Trigger, Quest), Relation(Trigger, Quest), Relation(OtherTrigger, Quest), Relation(OtherTrigger, 777)],
        });

        Assert.Equal([OtherQuest, Quest], store.AreaTriggerQuestsOf(Trigger));
        Assert.Equal([Quest], store.AreaTriggerQuestsOf(OtherTrigger));
        Assert.Empty(store.AreaTriggerQuestsOf(9999));
        Assert.True(store.HasAreaTrigger(Quest));
        Assert.True(store.HasAreaTrigger(OtherQuest));
        Assert.False(store.HasAreaTrigger(777));
        Assert.False(QuestStore.Empty.HasAreaTrigger(Quest));
    }

    [Fact]
    public void ATriggerInTheImportedTable_CompletesTheExplorationObjective_WithoutAnyConfiguration()
    {
        using var kit = new Kit([Exploration(Quest)], [Relation(Trigger, Quest)]);
        Assert.Empty(kit.Services.Options.AreaTriggerQuests);

        Assert.True(kit.Accept(Quest));
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(Quest));
        Assert.Equal(0, kit.Services.AreaTriggerReached(kit.Player, OtherTrigger));
        kit.Session.Clear();

        Assert.Equal(1, kit.Services.AreaTriggerReached(kit.Player, Trigger));

        Assert.True(kit.State.Quests.Get(Quest)!.Explored);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Quest));
        Assert.Single(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestupdateComplete);
        Assert.Equal(0, kit.Services.AreaTriggerReached(kit.Player, Trigger)); // no longer incomplete
    }

    [Fact]
    public void WithoutARelationInTheTableOrTheConfiguration_TheExplorationQuestStaysUnavailable()
    {
        using var kit = new Kit([Exploration(Quest)], []);

        Assert.False(kit.Accept(Quest));
        Assert.False(kit.Services.HasAreaTrigger(Quest));
        Assert.Equal(QuestAdapter.EventCredit, kit.Services.MissingAdapters(kit.Services.Quests.Get(Quest)!));
    }

    [Fact]
    public void TheTableRelation_IsWhatMakesTheQuestSupported()
    {
        using var kit = new Kit([Exploration(Quest)], [Relation(Trigger, Quest)]);

        Assert.True(kit.Services.HasAreaTrigger(Quest));
        Assert.True(kit.Services.Supported(kit.Services.Quests.Get(Quest)!));
    }

    [Fact]
    public void AQuestNamedByTheTableAndTheConfiguration_IsCreditedOnce_AndAConfiguredOnlyTriggerStillWorks()
    {
        using var kit = new Kit([Exploration(Quest), Exploration(OtherQuest)], [Relation(Trigger, Quest)],
            o => o.AreaTriggerQuests = [new QuestAreaTrigger { TriggerId = Trigger, QuestId = Quest }, new QuestAreaTrigger { TriggerId = Trigger, QuestId = OtherQuest }]);
        Assert.True(kit.Accept(Quest));
        Assert.True(kit.Accept(OtherQuest));

        Assert.Equal(2, kit.Services.AreaTriggerReached(kit.Player, Trigger));

        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Quest));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(OtherQuest));
    }

    [Fact]
    public void OneTriggerCreditsEveryQuestItIsRelatedTo_AndOnlyTheOnesThePlayerIsOn()
    {
        using var kit = new Kit([Exploration(Quest), Exploration(OtherQuest)], [Relation(Trigger, Quest), Relation(Trigger, OtherQuest)]);
        Assert.True(kit.Accept(Quest));

        Assert.Equal(1, kit.Services.AreaTriggerReached(kit.Player, Trigger));

        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Quest));
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(OtherQuest));
    }

    [Fact]
    public void ADeadPlayerOrTriggerZero_CreditsNothing()
    {
        using var kit = new Kit([Exploration(Quest)], [Relation(Trigger, Quest), Relation(0, Quest)]);
        Assert.True(kit.Accept(Quest));

        Assert.Equal(0, kit.Services.AreaTriggerReached(kit.Player, 0));
        kit.Player.Health = 0;
        Assert.Equal(0, kit.Services.AreaTriggerReached(kit.Player, Trigger));
        Assert.False(kit.State.Quests.Get(Quest)!.Explored);
    }

    [Fact]
    public void AReloadedStore_ReplacesTheRelationsForTheNextTrigger()
    {
        using var kit = new Kit([Exploration(Quest)], [Relation(Trigger, Quest)]);
        Assert.True(kit.Accept(Quest));

        // .reload quest_template swaps the immutable store: the quest now has its trigger elsewhere.
        kit.Services.ReplaceQuests(new QuestStore(new QuestContent([Exploration(Quest)], [Relation(Kit.Giver, Quest)], [])
        {
            AreaTriggerQuests = [Relation(OtherTrigger, Quest)],
        }));

        Assert.Equal(0, kit.Services.AreaTriggerReached(kit.Player, Trigger));
        Assert.Equal(1, kit.Services.AreaTriggerReached(kit.Player, OtherTrigger));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Quest));
    }

    private sealed class Kit : IDisposable
    {
        public const uint Giver = 910110;

        public Kit(IReadOnlyList<QuestTemplate> quests, IReadOnlyList<CreatureQuestRelation> triggers, Action<QuestNpcOptions>? configure = null)
        {
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            Player.Level = 5;
            ItemTestData.Wire(Player.Inventory);
            Player.Inventory.Load([]);
            World.AddPlayer(Player);
            var template = new CreatureTemplate { Entry = Giver, Name = "Exploration giver", Faction = 2, NpcFlags = 2 };
            Creature = new Creature(910120, template,
                new CreatureSpawn { Guid = 910120, Entry = Giver, MapId = 0, X = 0, Y = 0, Z = Player.Z }, CreatureContent.Empty, new Random(1));
            Player.Map!.AddObject(Creature);
            World.RunTick(5);
            var options = new QuestNpcOptions();
            configure?.Invoke(options);
            CreatureQuestRelation[] starters = [.. quests.Select(q => new CreatureQuestRelation { Id = Giver, Quest = q.Entry })];
            var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0)]);
            Services = new QuestNpcServices(new QuestStore(new QuestContent(quests, starters, starters) { AreaTriggerQuests = triggers }), NpcStore.Empty,
                new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions)), options, new Sink(), () => 100, NullLogger.Instance);
            State = Services.Track(Player);
            Services.CompleteLoad(State, new CharacterQuestData([], []));
            Session.Clear();
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();

        public FakeSession Session { get; } = new();

        public Player Player { get; }

        public Creature Creature { get; }

        public QuestNpcServices Services { get; }

        public PlayerNpcState State { get; }

        public bool Accept(uint questId) => Services.AcceptQuest(Player, Creature.Guid, questId);

        public void Dispose() => World.Dispose();
    }

    private sealed class Sink : IQuestNpcSink
    {
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows)
        {
        }

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask)
        {
        }

        public void CharacterChanged(Player player)
        {
        }
    }
}
