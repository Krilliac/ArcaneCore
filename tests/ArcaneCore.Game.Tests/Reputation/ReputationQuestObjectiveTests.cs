using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// Reputation objectives (quest_template RepObjectiveFaction / RepObjectiveValue): the quest completes when the standing
/// reaches the value and reverts when it falls below (vmangos Player::ReputationChanged, Player.cpp:14239-14264).
/// </summary>
public sealed class ReputationQuestObjectiveTests : IDisposable
{
    private const uint Giver = 900010;
    private const uint RepQuest = 910201;
    private const uint RewardQuest = 910202;

    private readonly WorldRuntime _world = TestWorld.CreateRuntime();
    private readonly FakeSession _session = new();
    private readonly ReputationService _service;
    private readonly QuestNpcServices _services;
    private readonly Player _player;
    private readonly Creature _giver;
    private readonly List<uint> _events = [];
    private readonly PlayerNpcState _state;

    public ReputationQuestObjectiveTests()
    {
        _service = new ReputationService(Factions, roll: () => 0.0)
        {
            Content = ReputationContent.Create(new ReputationContentRows(
                [new ReputationSpilloverTemplate(Stormwind, [new ReputationSpillover(BootyBay, 1f, 7)])], []), Factions),
        };
        _player = TestWorld.CreatePlayer(1, 0, 0, _session);
        ItemTestData.Wire(_player.Inventory);
        _player.Inventory.Load([]);
        _world.AddPlayer(_player);
        _giver = new Creature(900020, new CreatureTemplate { Entry = Giver, Name = "Giver", Faction = 2, NpcFlags = 2 },
            new CreatureSpawn { Guid = 900020, Entry = Giver, MapId = 0, X = 0, Y = 0, Z = _player.Z }, CreatureContent.Empty, new Random(1));
        _player.Map!.AddObject(_giver);
        _world.RunTick(5);

        QuestTemplate[] quests =
        [
            new QuestTemplate { Entry = RepQuest, Method = 2, RepObjectiveFaction = BootyBay, RepObjectiveValue = 3000 },
            new QuestTemplate { Entry = RewardQuest, Method = 2, RewRepFaction1 = BootyBay, RewRepValue1 = 3000 },
        ];
        CreatureQuestRelation[] relations = [.. quests.Select(q => new CreatureQuestRelation { Id = Giver, Quest = q.Entry })];
        var options = new QuestNpcOptions { OrdinaryRewardQuestIds = [RepQuest, RewardQuest] };
        var templates = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0)]);
        _services = new QuestNpcServices(new QuestStore(new QuestContent(quests, relations, relations)), NpcStore.Empty,
            new QuestNpcDependencies(Creatures: new CreatureQuestLookup(templates), Reputation: _service, ReputationRewards: _service),
            options, new Sink(), () => 100, NullLogger.Instance);
        _state = _services.Track(_player);
        _services.CompleteLoad(_state, new CharacterQuestData([], []));
        _service.Track(_player, _service.Create(_player, new CharacterReputationData([], -1)));
        _service.BuildInitializeFactions(_player);
        _service.ReputationChanged += (player, faction) =>
        {
            _events.Add(faction);
            _services.ReputationChanged(player, faction);
        };
        _session.Clear();
    }

    public void Dispose() => _world.Dispose();

    private QuestStatus Status(uint quest) => _state.Quests.GetStatus(quest);

    [Fact]
    public void RepObjectiveQuest_CompletesWhenTheStandingReachesTheValue_AndRevertsBelowIt()
    {
        Assert.True(_services.AcceptQuest(_player, _giver.Guid, RepQuest));
        Assert.Equal(QuestStatus.Incomplete, Status(RepQuest));

        _service.ModifyReputation(_player, BootyBay, 2999);
        Assert.Equal(QuestStatus.Incomplete, Status(RepQuest));
        _service.ModifyReputation(_player, BootyBay, 1);
        Assert.Equal(3000, _service.GetReputation(_player, BootyBay));
        Assert.Equal(QuestStatus.Complete, Status(RepQuest));

        _service.ModifyReputation(_player, BootyBay, -1);
        Assert.Equal(QuestStatus.Incomplete, Status(RepQuest)); // Player.cpp:14257-14262
    }

    [Fact]
    public void ASpilloverGain_CompletesTheObjectiveOfTheSpilledFaction()
    {
        Assert.True(_services.AcceptQuest(_player, _giver.Guid, RepQuest));
        _service.ModifyReputation(_player, Stormwind, 3000); // Booty Bay receives the same amount through the template
        Assert.Equal(3000, _service.GetReputation(_player, BootyBay));
        Assert.Equal(QuestStatus.Complete, Status(RepQuest));
        Assert.Contains(BootyBay, _events);
        Assert.Contains(Stormwind, _events);
    }

    [Fact]
    public void AnUnrelatedFaction_DoesNothing()
    {
        Assert.True(_services.AcceptQuest(_player, _giver.Guid, RepQuest));
        _service.ModifyReputation(_player, ClassBased, 5000);
        Assert.Equal(QuestStatus.Incomplete, Status(RepQuest));
        _service.ReputationChanged += (_, _) => { };
        _services.ReputationChanged(_player, 0);
        Assert.Equal(QuestStatus.Incomplete, Status(RepQuest));
    }

    [Fact]
    public void QuestSettlementPublish_RaisesTheEventOnce_AndCompletesAnotherQuestInTheLog()
    {
        Assert.True(_services.AcceptQuest(_player, _giver.Guid, RepQuest));
        Assert.True(_services.AcceptQuest(_player, _giver.Guid, RewardQuest));
        Assert.True(_services.TryPrepareReward(_player, _giver.Guid, RewardQuest, 0, out QuestRewardPlan? plan));
        Assert.Empty(_events); // staging raises nothing

        Guid operation = Guid.NewGuid();
        Assert.True(_player.BeginQuestSettlement(operation));
        using (_player.BeginQuestSettlementPublication(operation))
        {
            _services.ApplyReward(plan);
        }

        Assert.True(_player.EndQuestSettlement(operation));
        Assert.Equal(1, _events.Count(f => f == BootyBay));
        Assert.Equal(QuestStatus.Complete, Status(RepQuest));
    }

    private sealed class Sink : IQuestNpcSink
    {
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) { }

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) { }

        public void CharacterChanged(Player player) { }
    }
}
