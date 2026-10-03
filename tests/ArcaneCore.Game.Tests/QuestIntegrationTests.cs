using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests;

public sealed class QuestIntegrationTests
{
    [Fact]
    public void KillCredit_CompletesAndPersistsOnce_WithVerifiedPacketBody()
    {
        var quest = new QuestTemplate { Entry = 10, Method = 2, ReqCreatureOrGOId1 = 123, ReqCreatureOrGOCount1 = 2 };
        var kit = new Kit([quest], [Row(10)]);
        ObjectGuid victim = ObjectGuid.WithEntry(HighGuid.Unit, 123, 6);
        kit.Services.KilledMonsterCredit(kit.Player, 999, victim);
        Assert.Empty(kit.Sink.Quests);
        kit.Services.KilledMonsterCredit(kit.Player, 123, victim);
        kit.Services.KilledMonsterCredit(kit.Player, 123, victim);
        kit.Services.KilledMonsterCredit(kit.Player, 123, victim);

        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(10));
        Assert.Equal(2u, kit.State.Quests.Get(10)!.CreatureOrGOCount[0]);
        Assert.Equal(2, kit.Sink.Quests.Count);
        Assert.Equal((byte)QuestStatus.Complete, kit.Sink.Quests[^1].Status);
        var packets = kit.Session.Sent.ToArray();
        Assert.Equal(2, packets.Length);
        Assert.Equal(WorldOpcode.SmsgQuestupdateAddKill, packets[1].Opcode);
        var reader = new PacketReader(packets[1].Payload);
        Assert.Equal(10u, reader.ReadUInt32());
        Assert.Equal(123u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(victim.Value, reader.ReadUInt64());
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(QuestConstants.SlotStateComplete, kit.Player.GetByte(UpdateFields.PlayerQuestLog12, 3));
    }

    [Fact]
    public void CastCredit_RequiresSpellAndCorrectTargetKind()
    {
        var quest = new QuestTemplate { Entry = 11, Method = 2, ReqCreatureOrGOId1 = -77, ReqCreatureOrGOCount1 = 1, ReqSpellCast1 = 42 };
        var kit = new Kit([quest], [Row(11)]);
        ObjectGuid target = ObjectGuid.WithEntry(HighGuid.GameObject, 77, 5);
        kit.Services.KilledMonsterCredit(kit.Player, 77, target);
        kit.Services.CastedCreatureOrGo(kit.Player, 77, target, true, 42);
        kit.Services.CastedCreatureOrGo(kit.Player, 77, target, false, 99);
        Assert.Empty(kit.Sink.Quests);
        kit.Services.CastedCreatureOrGo(kit.Player, 77, target, false, 42);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(11));
        var reader = new PacketReader(kit.Session.Next().Payload);
        reader.Skip(4);
        Assert.Equal(0x8000004Du, reader.ReadUInt32());
    }

    [Fact]
    public void ItemRemoval_UsesRemainingInventory_AndMoneyCanRevertCompletion()
    {
        var items = new Items { RemainingCount = 5 };
        var quest = new QuestTemplate { Entry = 12, Method = 2, ReqItemId1 = 7, ReqItemCount1 = 3, RewOrReqMoney = -10 };
        var kit = new Kit([quest], [Row(12)], items: items);
        kit.Player.Money = 20;
        kit.Services.ItemAdded(kit.Player, 7, 5);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(12));
        kit.Services.ItemRemoved(kit.Player, 7, 1);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(12));
        Assert.Equal(3u, kit.State.Quests.Get(12)!.ItemCount[0]);
        kit.Player.Money = 9;
        kit.Services.MoneyChanged(kit.Player);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(12));
        kit.Player.Money = 10;
        kit.Services.MoneyChanged(kit.Player);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(12));
        items.RemainingCount = 2;
        kit.Services.ItemRemoved(kit.Player, 7, 3);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(12));
        Assert.Equal(2u, kit.Sink.Quests[^1].ItemCount1);
    }

    [Fact]
    public void ExpiredTimedQuest_FailsDuringLoad_AndSavesClearedTimer()
    {
        var quest = new QuestTemplate { Entry = 13, Method = 2, LimitTime = 30 };
        var kit = new Kit([quest], [Row(13) with { Timer = 99 }]);
        Assert.Equal(QuestStatus.Failed, kit.State.Quests.GetStatus(13));
        Assert.Empty(kit.State.Quests.TimedQuests);
        Assert.Equal(0, kit.Sink.Quests.Single().Timer);
        Assert.Equal(1u, kit.Player.GetUInt32(UpdateFields.PlayerQuestLog11 + 2));
        Assert.Equal(WorldOpcode.SmsgQuestupdateFailedtimer, kit.Session.Next().Opcode);
        kit.Services.CheckTimers(kit.Player);
        Assert.Single(kit.Sink.Quests);
    }

    [Fact]
    public void Relog_DropsStaleLoadCreditAndUntrack()
    {
        var quest = new QuestTemplate { Entry = 14, Method = 2, ReqCreatureOrGOId1 = 123, ReqCreatureOrGOCount1 = 1 };
        var kit = new Kit([quest], []);
        Player oldPlayer = kit.Player;
        PlayerNpcState oldState = kit.State;
        Player newPlayer = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        PlayerNpcState newState = kit.Services.Track(newPlayer);
        kit.Services.CompleteLoad(newState, new CharacterQuestData([Row(14)], []));
        kit.Services.CompleteLoad(oldState, new CharacterQuestData([Row(14)], []));
        kit.Services.KilledMonsterCredit(oldPlayer, 123, ObjectGuid.Empty);
        kit.Services.Untrack(oldPlayer);
        Assert.Same(newState, kit.Services.StateOf(newPlayer));
        Assert.Equal(QuestStatus.Incomplete, newState.Quests.GetStatus(14));
        Assert.Empty(kit.Sink.Quests);
    }

    [Fact]
    public void GossipQuestMenu_HidesIneligibleQuests_AndOpensSingleEligibleDetails()
    {
        var available = new QuestTemplate { Entry = 15, Method = 2, Title = "A task", Details = "Help", Objectives = "Work" };
        var wrongRace = new QuestTemplate { Entry = 16, Method = 2, RequiredRaces = 2 };
        var previous = new QuestTemplate { Entry = 17, Method = 2, PrevQuestId = 15 };
        var kit = new Kit([available, wrongRace, previous], [], starters: [15, 16, 17], npcFlags: NpcFlags.QuestGiver);
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Single(kit.State.Menu.QuestItems);
        (WorldOpcode opcode, byte[] payload) = kit.Session.Next();
        Assert.Equal(WorldOpcode.SmsgQuestgiverQuestDetails, opcode);
        var reader = new PacketReader(payload);
        Assert.Equal(kit.Npc.Guid.Value, reader.ReadUInt64());
        Assert.Equal(15u, reader.ReadUInt32());
        Assert.Equal("A task", reader.ReadCString());
        Assert.Equal("Help", reader.ReadCString());
        Assert.Equal("Work", reader.ReadCString());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(4u, reader.ReadUInt32());
        reader.Skip(32);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void GossipQuestMenu_VendorWithQuest_KeepsGossipWindow()
    {
        // vmangos Player::SendPreparedGossip keeps vendors with quests in the gossip window.
        var quest = new QuestTemplate { Entry = 15, Method = 2, Title = "A task", QuestLevel = 1 };
        var kit = new Kit([quest], [], starters: [15]);
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        (WorldOpcode opcode, byte[] payload) = kit.Session.Next();
        Assert.Equal(WorldOpcode.SmsgGossipMessage, opcode);
        var reader = new PacketReader(payload);
        Assert.Equal(kit.Npc.Guid.Value, reader.ReadUInt64());
        Assert.Equal(QuestNpcServices.DefaultGossipMessage, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(15u, reader.ReadUInt32());
        Assert.Equal((uint)DialogStatus.Available, reader.ReadUInt32());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("A task", reader.ReadCString());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void FailedVendorStorage_DoesNotChargeOrConsumeStock()
    {
        var items = new Items();
        var content = NpcContent.Empty with
        {
            VendorItems = [new VendorItem { Entry = 1, Item = 7, MaxCount = 3, IncrTime = 60 }],
        };
        var kit = new Kit([], [], items: items, npcContent: content);
        kit.Player.Money = 100;
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, 7, 1);
        Assert.Equal(100u, kit.Player.Money);
        Assert.Equal(0, kit.Sink.CharacterChanges);
        Assert.DoesNotContain(kit.Session.Sent, packet => packet.Opcode == WorldOpcode.SmsgBuyItem);
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        var reader = new PacketReader(kit.Session.Next().Payload);
        reader.Skip(8);
        Assert.Equal(1, reader.ReadByte());
        reader.Skip(12);
        Assert.Equal(3u, reader.ReadUInt32());
    }

    private static CharacterQuestStatus Row(uint id)
        => new(1, id, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class Kit
    {
        public Kit(IReadOnlyList<QuestTemplate> templates, IReadOnlyList<CharacterQuestStatus> rows,
            Items? items = null, NpcContent? npcContent = null, IReadOnlyList<uint>? starters = null,
            NpcFlags npcFlags = NpcFlags.QuestGiver | NpcFlags.Vendor)
        {
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            TestWorld.CreateRuntime().AddPlayer(Player);
            Npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, 1, 1), 1, 1,
                npcFlags, 0, 0, 0, Player.Z, 0.5f, true, false, false, false, 0);
            var content = new QuestContent(templates,
                (starters ?? []).Select(id => new CreatureQuestRelation { Id = 1, Quest = id }).ToArray(), []);
            Services = new QuestNpcServices(new QuestStore(content), new NpcStore(npcContent ?? NpcContent.Empty),
                new QuestNpcDependencies(Creatures: new Lookup(Npc), Items: items), new QuestNpcOptions(),
                Sink, () => 100, NullLogger.Instance);
            State = Services.Track(Player);
            Session.Clear();
            Services.CompleteLoad(State, new CharacterQuestData(rows, []));
        }

        public FakeSession Session { get; } = new();
        public Sink Sink { get; } = new();
        public Player Player { get; }
        public NpcInfo Npc { get; }
        public QuestNpcServices Services { get; }
        public PlayerNpcState State { get; }
    }

    private sealed class Sink : IQuestNpcSink
    {
        public List<CharacterQuestStatus> Quests { get; } = [];
        public int CharacterChanges { get; private set; }
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) => Quests.AddRange(rows);
        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) { }
        public void CharacterChanged(Player player) => CharacterChanges++;
    }

    private sealed class Lookup(NpcInfo npc) : ICreatureLookup
    {
        public NpcInfo? Find(Player player, ObjectGuid guid) => guid == npc.Guid ? npc : null;
    }

    private sealed class Items : IItemService
    {
        public uint RemainingCount { get; set; }
        public ItemInfo? GetItem(uint itemId) => itemId == 7 ? new(7, 100, 10, 1, 0, uint.MaxValue, uint.MaxValue, 0, 0, 0, 0) : null;
        public uint GetItemCount(Player player, uint itemId, bool inBankAlso) => RemainingCount;
        public InventoryResult CanStoreNewItem(Player player, uint itemId, uint count) => InventoryResult.Ok;
        public bool StoreNewItem(Player player, uint itemId, uint count) => false;
        public void DestroyItemCount(Player player, uint itemId, uint count) { }
        public ItemSale SellToVendor(Player player, ObjectGuid vendor, ObjectGuid item, byte count) => ItemSale.Ignored;
        public void SendEquipError(Player player, InventoryResult result, uint itemId) { }
    }
}
