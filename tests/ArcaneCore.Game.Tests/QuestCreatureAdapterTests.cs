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

public sealed class QuestCreatureAdapterTests
{
    [Fact]
    public void ActualVisibleCreature_CanAcceptOnce_AbandonAndReacceptWithFreshTimer()
    {
        using var kit = new Kit();
        Assert.Contains(kit.Creature.Guid, kit.Player.VisibleObjects);
        Assert.True(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900001));
        Assert.Equal(900001u, kit.State.Quests.SlotQuestId(0));
        Assert.Equal(130, kit.State.Quests.Get(900001)!.TimerEndUnix);
        Assert.False(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900001));
        Assert.Single(kit.Sink.Rows);
        Assert.True(kit.Services.AbandonQuest(kit.Player, 0));
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(900001));
        Assert.Equal(0u, kit.State.Quests.SlotQuestId(0));
        Assert.Empty(kit.State.Quests.TimedQuests);
        Assert.Equal(0, kit.Sink.Rows[^1].Timer);
        kit.Now = 110;
        Assert.True(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900001));
        Assert.Equal(140, kit.State.Quests.Get(900001)!.TimerEndUnix);
        Assert.Equal(0u, kit.State.Quests.Get(900001)!.CreatureOrGOCount[0]);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("dead")]
    [InlineData("combat")]
    [InlineData("unselectable")]
    [InlineData("charmed")]
    [InlineData("wrongflag")]
    [InlineData("unknown")]
    [InlineData("hostile")]
    [InlineData("reputation")]
    [InlineData("contested")]
    [InlineData("othermap")]
    [InlineData("deadplayer")]
    [InlineData("stunned")]
    [InlineData("confused")]
    [InlineData("fleeing")]
    [InlineData("boundary")]
    [InlineData("vertical")]
    public void InteractionGuards_RejectWithoutJournalMutation(string guard)
    {
        using var kit = new Kit();
        switch (guard)
        {
            case "hidden": kit.Player.VisibleObjects.Remove(kit.Creature.Guid); break;
            case "dead": kit.Creature.Health = 0; break;
            case "combat": kit.Creature.UnitFlags |= UnitFlags.InCombat; break;
            case "unselectable": kit.Creature.UnitFlags |= UnitFlags.NotSelectable; break;
            case "charmed": kit.Creature.SetUInt64(UpdateFields.UnitFieldCharmedby, 1); break;
            case "wrongflag": kit.Creature.NpcFlags = 0; break;
            case "unknown": kit.Creature.FactionTemplate = 999; break;
            case "hostile": kit.Creature.FactionTemplate = 3; break;
            case "reputation": kit.Creature.FactionTemplate = 4; break;
            case "contested": kit.Creature.FactionTemplate = 5; break;
            case "othermap": kit.World.GetMap(0).RemoveObject(kit.Creature); kit.World.GetMap(1).AddObject(kit.Creature); kit.Player.VisibleObjects.Add(kit.Creature.Guid); break;
            case "deadplayer": kit.Player.Health = 0; break;
            case "stunned": kit.Player.UnitFlags |= UnitFlags.Stunned; break;
            case "confused": kit.Player.UnitFlags |= UnitFlags.Confused; break;
            case "fleeing": kit.Player.UnitFlags |= UnitFlags.Fleeing; break;
            case "boundary": kit.Creature.Relocate(5 + kit.Creature.BoundingRadius + kit.Player.BoundingRadius, 0, kit.Player.Z, 0, 0); break;
            case "vertical": kit.Creature.Relocate(0, 0, kit.Player.Z + 20, 0, 0); break;
        }

        Assert.False(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900001));
        Assert.Empty(kit.Sink.Rows);
        Assert.Equal(0u, kit.State.Quests.SlotQuestId(0));
    }

    [Fact]
    public void StatusUsesVisibilityBeyondInteractionRange_DetailsRequireRealRelation()
    {
        using var kit = new Kit();
        kit.Creature.Relocate(30, 0, kit.Player.Z, 0, 0);
        kit.Services.QuestgiverStatusQuery(kit.Player, kit.Creature.Guid);
        var reader = new PacketReader(kit.Session.Next().Payload);
        Assert.Equal(kit.Creature.Guid.Value, reader.ReadUInt64());
        Assert.Equal((uint)DialogStatus.Available, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        kit.Services.QuestgiverQueryQuest(kit.Player, kit.Creature.Guid, 900001);
        Assert.Empty(kit.Session.Sent);
        kit.Creature.Relocate(0, 0, kit.Player.Z, 0, 0);
        kit.Services.QuestgiverQueryQuest(kit.Player, kit.Creature.Guid, 900002);
        Assert.Empty(kit.Session.Sent);
        Assert.False(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900002));
        Assert.Empty(kit.Sink.Rows);
    }

    [Fact]
    public void SameTemplateId_DoesNotOverrideHostileMask_AndCatalogSnapshotsItsInput()
    {
        var rows = new List<FactionTemplateRecord> { new(1, 0, 0, 1, 0, 1) };
        var catalog = new FactionTemplateCatalog(rows);
        rows.Clear();
        Assert.True(catalog.TryNpcHostility(1, 1, out bool hostile));
        Assert.True(hostile);
        Assert.False(catalog.TryNpcHostility(1, 99, out _));
    }

    [Fact]
    public void FullLogAndUnreadyJournal_DoNotAcceptOrMutate()
    {
        using var kit = new Kit();
        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            kit.State.Quests.SetSlot(slot, (uint)(slot + 1));
        }

        Assert.False(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900001));
        Assert.Contains(kit.Session.Sent, packet => packet.Opcode == WorldOpcode.SmsgQuestlogFull && packet.Payload.Length == 0);
        Assert.Empty(kit.Sink.Rows);
        kit.State.Loaded = false;
        kit.State.Quests.SetSlot(0, 0);
        Assert.False(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900001));
        Assert.False(kit.Services.AbandonQuest(kit.Player, byte.MaxValue));
        Assert.Empty(kit.Sink.Rows);
    }

    [Fact]
    public void SecondTimedQuest_IsRejected_AndAcceptanceResetsAbandonedCounters()
    {
        using var kit = new Kit();
        Assert.True(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900001));
        kit.Services.KilledMonsterCredit(kit.Player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 2));
        Assert.Equal(1u, kit.State.Quests.Get(900001)!.CreatureOrGOCount[0]);
        Assert.True(kit.Services.AbandonQuest(kit.Player, 0));
        Assert.True(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900001));
        Assert.Equal(0u, kit.State.Quests.Get(900001)!.CreatureOrGOCount[0]);
        // A known, eligible second timed quest shares the real starter but cannot overlap.
        Assert.False(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900003));
        Assert.Single(kit.State.Quests.TimedQuests);
        Assert.Equal(0u, kit.State.Quests.SlotQuestId(1));
    }

    [Fact]
    public void PreviouslyRewardedRepeatable_CannotEnterJournalOnlyFlow()
    {
        // A real persisted history row reaches the service through CompleteLoad.
        var history = new CharacterQuestStatus(1, 900004, (byte)QuestStatus.None, true, false, 0,
            5, 0, 0, 0, 0, 0, 0, 0, 0);
        using var kit = new Kit([history]);
        Assert.False(kit.Services.AcceptQuest(kit.Player, kit.Creature.Guid, 900004));
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(900004));
        Assert.True(kit.State.Quests.Get(900004)!.Rewarded);
        Assert.Equal(5u, kit.State.Quests.Get(900004)!.CreatureOrGOCount[0]);
        Assert.Equal(0u, kit.State.Quests.SlotQuestId(0));
        Assert.Empty(kit.State.Quests.TimedQuests);
        Assert.Empty(kit.Sink.Rows);
    }

    private sealed class Kit : IDisposable
    {
        public Kit(IReadOnlyList<CharacterQuestStatus>? rows = null)
        {
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            World.AddPlayer(Player);
            var template = new CreatureTemplate { Entry = 900010, Name = "Synthetic questgiver", Faction = 2, NpcFlags = 2 };
            var spawn = new CreatureSpawn { Guid = 900020, Entry = template.Entry, MapId = 0, X = 0, Y = 0, Z = Player.Z };
            Creature = new Creature(spawn.Guid, template, spawn, CreatureContent.Empty, new Random(1));
            Player.Map!.AddObject(Creature);
            World.RunTick(5);
            var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0),
                new(3, 0, 0, 8, 0, 1), new(4, 72, 0, 8, 0, 0), new(5, 0, 0x1000, 8, 0, 0)]);
            QuestTemplate[] quests = [new() { Entry = 900001, Method = 2, QuestLevel = 1, LimitTime = 30,
                ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2 }, new() { Entry = 900002, Method = 2 },
                new() { Entry = 900003, Method = 2, LimitTime = 30 },
                new() { Entry = 900004, Method = 2, SpecialFlags = (byte)QuestSpecialFlags.Repeatable, LimitTime = 30 }];
            Services = new QuestNpcServices(new QuestStore(new QuestContent(quests,
                [new CreatureQuestRelation { Id = template.Entry, Quest = 900001 },
                 new CreatureQuestRelation { Id = template.Entry, Quest = 900003 },
                 new CreatureQuestRelation { Id = template.Entry, Quest = 900004 }], [])), NpcStore.Empty,
                new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions)), new QuestNpcOptions(), Sink,
                () => Now, NullLogger.Instance);
            State = Services.Track(Player);
            Services.CompleteLoad(State, new CharacterQuestData(rows ?? [], []));
            Session.Clear();
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeSession Session { get; } = new();
        public RecordingSink Sink { get; } = new();
        public Player Player { get; }
        public Creature Creature { get; }
        public QuestNpcServices Services { get; }
        public PlayerNpcState State { get; }
        public long Now { get; set; } = 100;
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
