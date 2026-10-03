using Xunit;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.Npc.QuestFlowKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Quest givers that are game objects: a Wanted-poster-style object that starts a quest and a corpse-style object that
/// both ends one and starts the next (vmangos GameObject::Use QUESTGIVER GameObject.cpp:1457-1471, Player::PrepareQuestMenu
/// Player.cpp:12349-12418, HandleQuestgiverStatusQueryOpcode QuestHandler.cpp:34-70).
/// </summary>
public sealed class GameObjectQuestGiverTests
{
    private const uint PosterEntry = 68;       // starts Poster quest
    private const uint CorpseEntry = 56;       // ends Corpse quest, starts Follow-up quest
    private const uint PlainEntry = 99;        // GAMEOBJECT_TYPE_GENERIC
    private const uint PosterQuest = 176;
    private const uint CorpseQuest = 45;
    private const uint FollowUp = 71;

    private sealed class Rig : IDisposable
    {
        public Rig(IReadOnlyList<CharacterQuestStatus>? rows = null, float goX = 2, float playerX = 0, byte level = 5,
            IReadOnlyList<CreatureQuestRelation>? creatureStarters = null, bool chained = true)
        {
            Player = TestWorld.CreatePlayer(1, playerX, 0, Session);
            Player.Level = level;
            ItemTestData.Wire(Player.Inventory);
            Player.Inventory.Load([]);
            Player.Money = 50;
            GameObjectTemplate[] templates =
            [
                GoTemplate(PosterEntry, GameObjectType.QuestGiver),
                GoTemplate(CorpseEntry, GameObjectType.QuestGiver),
                GoTemplate(PlainEntry, GameObjectType.Generic),
            ];
            var content = new GameObjectContent(templates,
                [GoSpawn(1, PosterEntry, goX, 0), GoSpawn(2, CorpseEntry, goX, 1), GoSpawn(3, PlainEntry, goX, 2)], [],
                [(PosterEntry, PosterQuest), (CorpseEntry, FollowUp)], [(CorpseEntry, CorpseQuest)]);
            Map map = World.GetMap(0);
            System = new GameObjectMapSystem(map, content, null, null);
            map.AddUpdater(System);
            World.AddPlayer(Player);
            World.RunTick(50);
            QuestTemplate[] quests =
            [
                Task(PosterQuest),
                Task(CorpseQuest, nextInChain: chained ? FollowUp : 0),
                Task(FollowUp),
            ];
            var questContent = new QuestContent(quests, creatureStarters ?? [], [])
            {
                GameObjectStarters = [new() { Id = PosterEntry, Quest = PosterQuest }, new() { Id = CorpseEntry, Quest = FollowUp }],
                GameObjectEnders = [new() { Id = CorpseEntry, Quest = CorpseQuest }],
            };
            var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0), new(3, 0, 0, 8, 0, 1)]);
            Services = new QuestNpcServices(new QuestStore(questContent), NpcStore.Empty,
                new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions)),
                new QuestNpcOptions { OrdinaryRewardQuestIds = [CorpseQuest] }, new Sink(), () => 100, NullLogger.Instance);
            State = Services.Track(Player);
            Services.CompleteLoad(State, new CharacterQuestData(rows ?? [], []));
            System.QuestGiver = new Adapter(Services);
            Session.Clear();
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();

        public FakeSession Session { get; } = new();

        public Player Player { get; }

        public GameObjectMapSystem System { get; }

        public QuestNpcServices Services { get; }

        public PlayerNpcState State { get; }

        public ObjectGuid Guid(uint entry) => System.GameObjects.Single(g => g.Entry == entry).Guid;

        public List<(WorldOpcode Opcode, byte[] Payload)> Drain()
        {
            var list = new List<(WorldOpcode, byte[])>();
            CreatureTestSupport.DrainBlocks(Session, list);
            return list;
        }

        public void Dispose() => World.Dispose();
    }

    private sealed class Adapter(QuestNpcServices services) : IGameObjectQuestGiver
    {
        public bool OpenQuestMenu(Player player, GameObject go) => services.OpenGameObjectQuestMenu(player, go.Guid);
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

    [Fact]
    public void UsingAPosterWithOneQuest_OpensItsDetailsWindow()
    {
        // Menu 0 and one quest: SendPreparedGossip opens the quest directly (Player.cpp:12155-12162).
        using var rig = new Rig();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(rig.Player, rig.Guid(PosterEntry)));

        var sent = rig.Drain();
        (WorldOpcode Opcode, byte[] Payload) details = Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
        var reader = new PacketReader(details.Payload);
        Assert.Equal(rig.Guid(PosterEntry).Value, reader.ReadUInt64());
        Assert.Equal(PosterQuest, reader.ReadUInt32());
        Assert.DoesNotContain(sent, p => p.Opcode == WorldOpcode.SmsgGossipMessage);
    }

    [Fact]
    public void TheStatusQueryAnswersForAGameObjectGuid()
    {
        using var rig = new Rig();
        rig.Services.QuestgiverStatusQuery(rig.Player, rig.Guid(PosterEntry));
        var reader = new PacketReader(Assert.Single(rig.Drain(), p => p.Opcode == WorldOpcode.SmsgQuestgiverStatus).Payload);
        Assert.Equal(rig.Guid(PosterEntry).Value, reader.ReadUInt64());
        Assert.Equal((uint)DialogStatus.Available, reader.ReadUInt32());
    }

    [Fact]
    public void AcceptingFromTheObject_CreatesTheJournalEntryOnce()
    {
        using var rig = new Rig();
        Assert.True(rig.Services.AcceptQuest(rig.Player, rig.Guid(PosterEntry), PosterQuest));
        Assert.Equal(QuestStatus.Incomplete, rig.State.Quests.GetStatus(PosterQuest));
        rig.Drain();
        Assert.False(rig.Services.AcceptQuest(rig.Player, rig.Guid(PosterEntry), PosterQuest));
        (WorldOpcode Opcode, byte[] Payload) invalid = Assert.Single(rig.Drain(), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestInvalid);
        Assert.Equal((uint)QuestInvalidReason.AlreadyOn, BitConverter.ToUInt32(invalid.Payload));
    }

    [Fact]
    public void AnObjectCannotStartAQuestItDoesNotOwn()
    {
        // The poster does not start the corpse quest (relations are per entry).
        using var rig = new Rig();
        Assert.False(rig.Services.AcceptQuest(rig.Player, rig.Guid(PosterEntry), CorpseQuest));
        Assert.Equal(QuestStatus.None, rig.State.Quests.GetStatus(CorpseQuest));
    }

    [Fact]
    public void ACorpseThatEndsOneQuestAndStartsAnother_ListsBoth()
    {
        // Rolf's corpse in vanilla is the end of one quest and the start of the next.
        using var rig = new Rig([Row(CorpseQuest, QuestStatus.Complete, kills: 2)], chained: false);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(rig.Player, rig.Guid(CorpseEntry)));

        (WorldOpcode Opcode, byte[] Payload) list = Assert.Single(rig.Drain(), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestList);
        var reader = new PacketReader(list.Payload);
        Assert.Equal(rig.Guid(CorpseEntry).Value, reader.ReadUInt64());
        reader.ReadCString();
        reader.ReadUInt32();
        reader.ReadUInt32();
        Assert.Equal((byte)2, reader.ReadByte());
        Assert.Equal(CorpseQuest, reader.ReadUInt32());   // involved relations first (PrepareQuestMenu)
    }

    [Fact]
    public void TurningInAtTheObject_RewardsAndOffersTheNextQuestFromTheSameObject()
    {
        using var rig = new Rig([Row(CorpseQuest, QuestStatus.Complete, kills: 2)]);
        Assert.True(rig.Services.TryPrepareReward(rig.Player, rig.Guid(CorpseEntry), CorpseQuest, 0, out QuestRewardPlan? plan));
        rig.Drain();
        rig.Services.ApplyReward(plan!);

        var sent = rig.Drain();
        Assert.Contains(sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete);
        (WorldOpcode Opcode, byte[] Payload) next = Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
        var reader = new PacketReader(next.Payload);
        Assert.Equal(rig.Guid(CorpseEntry).Value, reader.ReadUInt64());
        Assert.Equal(FollowUp, reader.ReadUInt32());
    }

    [Fact]
    public void ACreatureAndAnObjectWithTheSameEntryNumber_DoNotShareRelations()
    {
        // vmangos keeps separate creature and game object relation maps; entry 68 as a creature starts nothing here.
        using var rig = new Rig();
        Assert.Empty(rig.Services.Quests.StartersOf(PosterEntry));
        Assert.Equal([PosterQuest], rig.Services.Quests.GameObjectStartersOf(PosterEntry));
        Assert.Empty(rig.Services.Quests.EndersOf(CorpseEntry));
        Assert.Equal([CorpseQuest], rig.Services.Quests.GameObjectEndersOf(CorpseEntry));
    }

    [Fact]
    public void AnObjectOutOfReach_OrANonQuestGiver_OpensNothing()
    {
        using (var far = new Rig(goX: 9))
        {
            Assert.Equal(GameObjectUseResult.TooFar, far.System.Use(far.Player, far.Guid(PosterEntry)));
            Assert.DoesNotContain(far.Drain(), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
        }

        using var rig = new Rig();
        Assert.False(rig.Services.OpenGameObjectQuestMenu(rig.Player, rig.Guid(PlainEntry)));
        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(rig.Player, rig.Guid(PlainEntry)));
        Assert.Empty(rig.Drain());
    }

    [Theory]
    [InlineData(5.4f, true)]
    [InlineData(5.55556f, true)]
    [InlineData(5.6f, false)]
    public void AQuestGiverObject_IsReachableWithinItsOwnInteractionDistance(float distance, bool reachable)
    {
        // vmangos GameObjectDefines.h:759-785 GetInteractionDistance(): QUESTGIVER is 5.55556, not INTERACTION_DISTANCE (5.0);
        // GameObject.cpp:2584-2609 IsAtInteractDistance compares the centre distance with '<='.
        using var rig = new Rig(goX: distance);
        GameObjectUseResult result = rig.System.Use(rig.Player, rig.Guid(PosterEntry));
        Assert.Equal(reachable, rig.Services.OpenGameObjectQuestMenu(rig.Player, rig.Guid(PosterEntry)));
        Assert.Equal(reachable ? GameObjectUseResult.Ok : GameObjectUseResult.TooFar, result);
        Assert.Equal(5.55556f, GameObjectMapSystem.InteractionDistanceFor(GameObjectType.QuestGiver));
        Assert.Equal(5.0f, GameObjectMapSystem.InteractionDistanceFor(GameObjectType.Chest));
        Assert.Equal(10.0f, GameObjectMapSystem.InteractionDistanceFor(GameObjectType.Binder));
    }

    [Fact]
    public void TheCreatureHelloOpcodesIgnoreAGameObject()
    {
        // CMSG_GOSSIP_HELLO / CMSG_QUESTGIVER_HELLO are creature requests (GetNPCIfCanInteractWith).
        using var rig = new Rig();
        rig.Services.GossipHello(rig.Player, rig.Guid(PosterEntry));
        Assert.Empty(rig.Drain());
    }

    [Fact]
    public void WithoutTheSeam_UsingAQuestGiverObjectIsStillUnsupported()
    {
        using var rig = new Rig();
        rig.System.QuestGiver = null;
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.Use(rig.Player, rig.Guid(PosterEntry)));
    }
}
