using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

public sealed class ReputationNpcInteractionTests
{
    [Theory]
    [InlineData(-3001, true, false)]
    [InlineData(-3000, false, false)]
    [InlineData(-1, false, false)]
    [InlineData(0, false, true)]
    public void RankBoundary_PreservesReaction_ButGatesDetailsAndJournalAcceptance(
        int standing, bool hostile, bool eligible)
    {
        using var kit = new Kit();
        Assert.True(kit.Reputation.SetReputation(kit.Player, Stormwind, standing));
        kit.Session.Clear();

        // An Unfriendly NPC remains visible and non-hostile: interaction is a separate rule.
        NpcInfo info = Assert.IsType<NpcInfo>(kit.Lookup.Find(kit.Player, kit.Creature.Guid));
        Assert.Equal(Stormwind, info.FactionId);
        Assert.Equal(hostile, info.IsHostile);
        Assert.Equal(eligible, kit.Services.InteractableNpc(kit.Player, kit.Creature.Guid, NpcFlags.QuestGiver) is not null);

        kit.Services.QuestgiverQueryQuest(kit.Player, kit.Creature.Guid, Kit.QuestId);
        if (eligible)
        {
            Assert.Equal(WorldOpcode.SmsgQuestgiverQuestDetails, Assert.Single(kit.Session.Sent).Opcode);
        }
        else
        {
            Assert.Empty(kit.Session.Sent);
        }

        AssertAcceptance(kit, eligible);
    }

    [Theory]
    [InlineData(2u, false, true)]   // neutral, no faction reputation
    [InlineData(3u, false, false)]  // template-hostile
    [InlineData(5u, false, true)]   // contested guard, uncontested player
    [InlineData(5u, true, false)]   // contested guard, contested player
    [InlineData(11u, false, true)]  // known faction with no reputation list
    [InlineData(7u, false, false)]  // nonzero faction missing from Faction.dbc
    [InlineData(999u, false, false)] // unknown faction template
    public void ExistingLookupContracts_StillGateActualQuestAcceptance(
        uint factionTemplate, bool contested, bool eligible)
    {
        using var kit = new Kit();
        Assert.True(kit.Reputation.SetReputation(kit.Player, Stormwind, -3000));
        kit.Creature.FactionTemplate = factionTemplate;
        if (contested)
        {
            kit.Player.Flags |= PlayerFlags.ContestedPvp;
        }

        Assert.Equal(eligible, kit.Services.InteractableNpc(kit.Player, kit.Creature.Guid, NpcFlags.QuestGiver) is not null);
        AssertAcceptance(kit, eligible);
    }

    [Fact]
    public void UnloadedReputationState_RejectsActualReputationNpcWithoutJournalMutation()
    {
        using var kit = new Kit();
        kit.Reputation.Untrack(kit.Player);
        Assert.Null(kit.Lookup.Find(kit.Player, kit.Creature.Guid));
        Assert.Null(kit.Services.InteractableNpc(kit.Player, kit.Creature.Guid, NpcFlags.QuestGiver));
        AssertAcceptance(kit, eligible: false);
    }

    private static void AssertAcceptance(Kit kit, bool eligible)
    {
        Assert.Equal(eligible, kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, Kit.QuestId));
        if (eligible)
        {
            Assert.Equal(Kit.QuestId, kit.State.Quests.SlotQuestId(0));
            Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(Kit.QuestId));
            Assert.Equal(0u, kit.State.Quests.Get(Kit.QuestId)!.CreatureOrGOCount[0]);
            CharacterQuestStatus row = Assert.Single(kit.Sink.Rows);
            Assert.Equal((int)kit.Player.Guid.Low, row.CharacterId);
            Assert.Equal(Kit.QuestId, row.Quest);
            Assert.Equal((byte)QuestStatus.Incomplete, row.Status);
            Assert.Equal(0u, row.MobCount1);
            Assert.False(row.Rewarded);
        }
        else
        {
            Assert.Equal(0u, kit.State.Quests.SlotQuestId(0));
            Assert.Null(kit.State.Quests.Get(Kit.QuestId));
            Assert.Empty(kit.Sink.Rows);
        }
    }

    private sealed class Kit : IDisposable
    {
        public const uint QuestId = 900331;

        public Kit()
        {
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            World.AddPlayer(Player);
            var template = new CreatureTemplate
            {
                Entry = 900332, Name = "Synthetic reputation questgiver", Faction = StormwindNpc.Id, NpcFlags = 2,
            };
            var spawn = new CreatureSpawn { Guid = 900333, Entry = template.Entry, MapId = 0, X = 0, Y = 0, Z = Player.Z };
            Creature = new Creature(spawn.Guid, template, spawn, CreatureContent.Empty, new Random(1));
            Player.Map!.AddObject(Creature);
            World.RunTick(5);
            Reputation.Track(Player, Reputation.Create(Player, CharacterReputationData.Empty));
            var templates = new FactionTemplateCatalog([PlayerTemplate, NeutralNpc, HostileNpc, StormwindNpc,
                ContestedGuard, UnknownNpc, new FactionTemplateRecord(11, Defias, 0, 2, 1, 0)]);
            Lookup = new CreatureQuestLookup(templates, Reputation);
            var quest = new QuestTemplate
            {
                Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1,
                ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
            };
            Services = new QuestNpcServices(new QuestStore(new QuestContent([quest],
                [new CreatureQuestRelation { Id = template.Entry, Quest = QuestId }], [])), NpcStore.Empty,
                new QuestNpcDependencies(Creatures: Lookup, Reputation: Reputation), new QuestNpcOptions(), Sink,
                () => 100, NullLogger.Instance);
            State = Services.Track(Player);
            Services.CompleteLoad(State, new CharacterQuestData([], []));
            Session.Clear();
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeSession Session { get; } = new();
        public RecordingSink Sink { get; } = new();
        public ReputationService Reputation { get; } = new(Factions);
        public Player Player { get; }
        public Creature Creature { get; }
        public CreatureQuestLookup Lookup { get; }
        public QuestNpcServices Services { get; }
        public PlayerNpcState State { get; }
        public void Dispose() => World.Dispose();
    }

    private sealed class RecordingSink : IQuestNpcSink
    {
        public List<CharacterQuestStatus> Rows { get; } = [];
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) => Rows.AddRange(rows);
        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) { }
        public void CharacterChanged(Player player) { }
    }
}
