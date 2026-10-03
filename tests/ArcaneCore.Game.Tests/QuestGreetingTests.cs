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

namespace ArcaneCore.Game.Tests;

public sealed class QuestGreetingTests
{
    [Fact]
    public void QuestOnlyGreetingListsExactlyTheEligibleStarterQuests()
    {
        QuestTemplate[] templates =
        [
            Task(10, "First task"), Task(11, "Second task"),
            Task(12, "Wrong race", races: 2),
            Task(13, "Too high", minimumLevel: 2),
            Task(14, "Requires first", previous: 10),
            Task(15, "Disabled", method: 2 | QuestConstants.MethodDisabled),
            Task(16, "Unrelated"), Task(17, "Already rewarded"),
        ];
        using var kit = new Kit(templates, [Row(17, QuestStatus.Complete) with { Rewarded = true }],
            starters: [10, 11, 12, 13, 14, 15, 17], enders: [17]);
        kit.Hello();
        Assert.Equal(new uint[] { 10, 11 }, kit.State.Menu.QuestItems.Select(item => item.QuestId));
        var packet = Assert.Single(kit.Session.Sent);
        Assert.Equal(WorldOpcode.SmsgQuestgiverQuestList, packet.Opcode);
        var reader = new PacketReader(packet.Payload);
        Assert.Equal(kit.Creature.Guid.Value, reader.ReadUInt64());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(2, reader.ReadByte());
        foreach ((uint id, string title) in new[] { (10u, "First task"), (11u, "Second task") })
        {
            Assert.Equal(id, reader.ReadUInt32());
            Assert.Equal((uint)DialogStatus.Available, reader.ReadUInt32());
            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal(title, reader.ReadCString());
        }

        Assert.Equal(0, reader.Remaining);
        Assert.Equal(0, kit.Sink.Mutations);
    }

    [Fact]
    public void GossipFlagKeepsTheEligibleQuestsInsideTheGossipMenu()
    {
        using var kit = new Kit([Task(10, "First task"), Task(11, "Second task")], [],
            starters: [10, 11], flags: NpcFlags.Gossip | NpcFlags.QuestGiver);
        kit.Hello();
        var gossip = Assert.Single(kit.Session.Sent);
        Assert.Equal(WorldOpcode.SmsgGossipMessage, gossip.Opcode);
        var reader = new PacketReader(gossip.Payload);
        Assert.Equal(kit.Creature.Guid.Value, reader.ReadUInt64());
        Assert.Equal(QuestNpcServices.DefaultGossipMessage, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        foreach (uint id in new uint[] { 10, 11 })
        {
            Assert.Equal(id, reader.ReadUInt32());
            Assert.Equal((uint)DialogStatus.Available, reader.ReadUInt32());
            reader.ReadInt32();
            reader.ReadCString();
        }

        Assert.Equal(0, reader.Remaining);
        Assert.Empty(kit.State.Menu.GossipItems);
        Assert.Equal(0, kit.Sink.Mutations);
    }

    [Theory]
    [InlineData(QuestStatus.None, 0, WorldOpcode.SmsgQuestgiverQuestDetails)]
    [InlineData(QuestStatus.Incomplete, 0, WorldOpcode.SmsgQuestgiverRequestItems)]
    [InlineData(QuestStatus.Complete, 2, WorldOpcode.SmsgQuestgiverOfferReward)]
    public void SingleQuestGreetingUsesCurrentProgressWithoutCreditingObjectives(
        QuestStatus status, uint kills, WorldOpcode expected)
    {
        using var kit = new Kit([Task(10, "A task")], status == QuestStatus.None ? [] : [Row(10, status, kills)],
            starters: [10], enders: [10]);
        kit.Hello();
        var packet = Assert.Single(kit.Session.Sent);
        Assert.Equal(expected, packet.Opcode);
        var reader = new PacketReader(packet.Payload);
        Assert.Equal(kit.Creature.Guid.Value, reader.ReadUInt64());
        Assert.Equal(10u, reader.ReadUInt32());
        Assert.Equal("A task", reader.ReadCString());
        if (expected == WorldOpcode.SmsgQuestgiverRequestItems)
        {
            Assert.Equal("Finish the task.", reader.ReadCString());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(2u, reader.ReadUInt32());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(4u, reader.ReadUInt32());
            Assert.Equal(8u, reader.ReadUInt32());
            Assert.Equal(0, reader.Remaining);
        }

        Assert.Equal(status, kit.State.Quests.GetStatus(10));
        Assert.Equal(kills, kit.State.Quests.Get(10)?.CreatureOrGOCount[0] ?? 0);
        Assert.Equal(0, kit.Sink.Mutations);
        Assert.Equal(0u, kit.Player.Money);
    }

    [Fact]
    public void EmptyRequestTextUsesTheVanillaOfferWindowWithoutCompletingOrRewardingTheQuest()
    {
        using var kit = new Kit([Task(10, "A task", requestText: string.Empty)],
            [Row(10, QuestStatus.Incomplete)], enders: [10]);
        kit.Hello();
        Assert.Equal(WorldOpcode.SmsgQuestgiverOfferReward, Assert.Single(kit.Session.Sent).Opcode);
        kit.Session.Clear();
        kit.Services.RequestReward(kit.Player, kit.Creature.Guid, 10);
        Assert.Empty(kit.Session.Sent);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 10, 0, out _));
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(10));
        Assert.False(kit.State.Quests.Get(10)!.Rewarded);
        Assert.Equal(0u, kit.State.Quests.Get(10)!.CreatureOrGOCount[0]);
        Assert.Equal(0, kit.Sink.Mutations);
        Assert.Equal(0u, kit.Player.Money);
    }

    [Theory]
    [InlineData(QuestStatus.None)]
    [InlineData(QuestStatus.Complete)]
    public void GreetingRewardsUseThePlayersLoadedItemDisplayIdsWithoutAnInjectedItemService(QuestStatus status)
    {
        var quest = new QuestTemplate
        {
            Entry = 10, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Display task",
            ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
            RequestItemsText = "Finish the task.",
            RewItemId1 = ItemTestData.ToughJerky, RewItemCount1 = 3,
            RewChoiceItemId1 = ItemTestData.Hearthstone, RewChoiceItemCount1 = 1,
        };
        using var kit = new Kit([quest], status == QuestStatus.None ? [] : [Row(10, status, kills: 2)],
            starters: [10], enders: [10]);
        Assert.Null(kit.Services.Deps.Items);
        Assert.True(kit.Player.Inventory.IsLoaded);
        kit.Hello();
        var packet = Assert.Single(kit.Session.Sent);
        Assert.Equal(status == QuestStatus.None ? WorldOpcode.SmsgQuestgiverQuestDetails
            : WorldOpcode.SmsgQuestgiverOfferReward, packet.Opcode);
        var reader = new PacketReader(packet.Payload);
        Assert.Equal(kit.Creature.Guid.Value, reader.ReadUInt64());
        Assert.Equal(10u, reader.ReadUInt32());
        Assert.Equal("Display task", reader.ReadCString());
        Assert.Equal(string.Empty, reader.ReadCString());
        if (status == QuestStatus.None)
        {
            Assert.Equal(string.Empty, reader.ReadCString());
        }

        Assert.Equal(1u, reader.ReadUInt32());
        if (status == QuestStatus.Complete)
        {
            Assert.Equal(0u, reader.ReadUInt32());
        }

        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(ItemTestData.Hearthstone, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(6418u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(ItemTestData.ToughJerky, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(2473u, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        if (status == QuestStatus.None)
        {
            Assert.Equal(4u, reader.ReadUInt32());
            reader.Skip(32);
        }
        else
        {
            Assert.Equal(0u, reader.ReadUInt32());
        }

        Assert.Equal(0, reader.Remaining);
        Assert.Empty(kit.Player.Inventory.AllItems);
        Assert.Equal(0, kit.Sink.Mutations);
    }

    [Theory]
    [InlineData("visibility")]
    [InlineData("distance")]
    [InlineData("deadnpc")]
    [InlineData("deadplayer")]
    [InlineData("hostile")]
    [InlineData("unknownfaction")]
    [InlineData("charmed")]
    [InlineData("notselectable")]
    [InlineData("notloaded")]
    [InlineData("pending")]
    [InlineData("wrongguid")]
    public void InvalidGreetingHasNoMenuPacketsOrQuestMutations(string guard)
    {
        using var kit = new Kit([Task(10, "A task")], [], starters: [10]);
        ObjectGuid requested = kit.Creature.Guid;
        switch (guard)
        {
            case "visibility": kit.Player.VisibleObjects.Remove(requested); break;
            case "distance": kit.Creature.Relocate(20, 0, kit.Player.Z, 0, 0); break;
            case "deadnpc": kit.Creature.Health = 0; break;
            case "deadplayer": kit.Player.Health = 0; break;
            case "hostile": kit.Creature.FactionTemplate = 3; break;
            case "unknownfaction": kit.Creature.FactionTemplate = 999; break;
            case "charmed": kit.Creature.SetUInt64(UpdateFields.UnitFieldCharmedby, kit.Player.Guid.Value); break;
            case "notselectable": kit.Creature.UnitFlags |= UnitFlags.NotSelectable; break;
            case "notloaded": kit.State.Loaded = false; break;
            case "pending": Assert.True(kit.Player.BeginQuestSettlement(Guid.NewGuid())); break;
            case "wrongguid": requested = ObjectGuid.WithEntry(HighGuid.GameObject, Entry, 20); break;
        }

        kit.Services.GossipHello(kit.Player, requested);
        Assert.Empty(kit.Session.Sent);
        Assert.Empty(kit.State.Menu.QuestItems);
        Assert.Equal(0, kit.Sink.Mutations);
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(10));
    }

    [Fact]
    public void NonQuestgiverHasNoQuestEntriesAndAnOldPlayerCannotOpenTheMenu()
    {
        using var kit = new Kit([Task(10, "A task")], [], starters: [10]);
        kit.Creature.NpcFlags = (uint)NpcFlags.Gossip;
        kit.Services.GossipHello(kit.Player, kit.Creature.Guid);
        Assert.Equal(WorldOpcode.SmsgGossipMessage, Assert.Single(kit.Session.Sent).Opcode);
        Assert.Empty(kit.State.Menu.QuestItems);
        kit.Session.Clear();
        kit.Creature.NpcFlags = (uint)NpcFlags.QuestGiver;
        Player replacement = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        PlayerNpcState replacementState = kit.Services.Track(replacement);
        kit.Services.CompleteLoad(replacementState, CharacterQuestData.Empty);
        kit.Services.GossipHello(kit.Player, kit.Creature.Guid);
        Assert.Empty(kit.Session.Sent);
        Assert.Empty(replacementState.Menu.QuestItems);
        Assert.Same(replacementState, kit.Services.StateOf(replacement));
        Assert.Equal(0, kit.Sink.Mutations);
    }

    private const uint Entry = 900010;

    private static QuestTemplate Task(uint id, string title, uint races = 0, byte minimumLevel = 1,
        int previous = 0, byte method = 2, string requestText = "Finish the task.") => new()
    {
        Entry = id, Method = method, MinLevel = minimumLevel, QuestLevel = 1, Title = title,
        RequiredRaces = races, PrevQuestId = previous,
        RequestItemsText = requestText,
        ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
    };

    private static CharacterQuestStatus Row(uint id, QuestStatus status, uint kills = 0) =>
        new(1, id, (byte)status, false, false, 0, kills, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class Kit : IDisposable
    {
        public Kit(IReadOnlyList<QuestTemplate> templates, IReadOnlyList<CharacterQuestStatus> rows,
            IReadOnlyList<uint>? starters = null, IReadOnlyList<uint>? enders = null, NpcFlags flags = NpcFlags.QuestGiver)
        {
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            ItemTestData.Wire(Player.Inventory);
            Player.Inventory.Load([]);
            World.AddPlayer(Player);
            var template = new CreatureTemplate { Entry = Entry, Name = "Greeting questgiver", Faction = 2, NpcFlags = (uint)flags };
            Creature = new Creature(20, template,
                new CreatureSpawn { Guid = 20, Entry = Entry, MapId = 0, X = 0, Y = 0, Z = Player.Z },
                CreatureContent.Empty, new Random(1));
            Player.Map!.AddObject(Creature);
            World.RunTick(5);
            var content = new QuestContent(templates,
                (starters ?? []).Select(id => new CreatureQuestRelation { Id = Entry, Quest = id }).ToArray(),
                (enders ?? []).Select(id => new CreatureQuestRelation { Id = Entry, Quest = id }).ToArray());
            var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0),
                new(2, 0, 0, 8, 0, 0), new(3, 0, 0, 8, 0, 1)]);
            Services = new QuestNpcServices(new QuestStore(content), NpcStore.Empty,
                new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions)),
                new QuestNpcOptions { OrdinaryRewardQuestIds = templates.Select(t => t.Entry).ToArray() },
                Sink, () => 100, NullLogger.Instance);
            State = Services.Track(Player);
            Services.CompleteLoad(State, new CharacterQuestData(rows, []));
            Session.Clear();
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeSession Session { get; } = new();
        public RecordingSink Sink { get; } = new();
        public Player Player { get; }
        public Creature Creature { get; }
        public QuestNpcServices Services { get; }
        public PlayerNpcState State { get; }
        public void Hello() => Services.GossipHello(Player, Creature.Guid);

        public void Dispose() => World.Dispose();
    }

    private sealed class RecordingSink : IQuestNpcSink
    {
        public int Mutations { get; private set; }
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) => Mutations += rows.Count;
        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) => Mutations++;
        public void CharacterChanged(Player player) => Mutations++;
    }
}
