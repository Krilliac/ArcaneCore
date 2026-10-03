using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests;

public sealed class QuestRewardTests
{
    [Fact]
    public void PendingSettlementRejectsGameplay_AndOnlyMatchingPublicationCanApplyThePreparedReward()
    {
        using var kit = new Kit();
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out QuestRewardPlan? plan));
        Guid operation = Guid.NewGuid();
        Assert.True(kit.Player.BeginQuestSettlement(operation));
        Assert.False(kit.Player.BeginQuestSettlement(Guid.NewGuid()));
        Assert.False(kit.Player.EndQuestSettlement(Guid.NewGuid()));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out _));
        Assert.Throws<InvalidOperationException>(() => kit.Services.ApplyReward(plan!));
        Assert.Throws<InvalidOperationException>(() => kit.Player.Money++);
        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.CantDoRightNow, kit.Player.Inventory.AddItem(ItemTestData.ToughJerky, 1, out _));
        Assert.Empty(kit.Player.Inventory.AllItems);
        Assert.Throws<InvalidOperationException>(() => kit.Player.BeginQuestSettlementPublication(Guid.NewGuid()));

        using (kit.Player.BeginQuestSettlementPublication(operation))
        {
            Assert.True(kit.Player.IsQuestSettlementPending);
            kit.Services.ApplyReward(plan!);
            Assert.Throws<InvalidOperationException>(() => kit.Player.EndQuestSettlement(operation));
        }

        Assert.True(kit.Player.IsQuestSettlementPending);
        Assert.False(kit.Player.CanMutateQuestSettlementState);
        Assert.Equal(80u, kit.Player.Money);
        Assert.True(kit.State.Quests.Get(QuestId)!.Rewarded);
        Assert.True(kit.Player.EndQuestSettlement(operation));
        Assert.False(kit.Player.EndQuestSettlement(operation));
        Assert.True(kit.Player.CanMutateQuestSettlementState);
        Assert.Single(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete);
    }

    [Fact]
    public void PreparationIsDetached_ApplicationPublishesOnce_AndPersistsChosenItemId()
    {
        using var kit = new Kit();
        var additions = new List<(uint Entry, int Count)>();
        kit.Player.Inventory.ItemCountChanged += (entry, count) =>
        {
            Assert.True(kit.State.Quests.Get(QuestId)!.Rewarded);
            additions.Add((entry, count));
        };

        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out QuestRewardPlan? plan));
        Assert.NotNull(plan);
        Assert.Equal(50u, plan.MoneyBefore);
        Assert.Equal(80u, plan.MoneyAfter);
        Assert.Equal(ItemTestData.Hearthstone, plan.RewardedQuest.RewardChoice);
        Assert.Empty(plan.BeforeInventory.Items);
        Assert.Equal(2, plan.InventoryAfter.Items.Count);
        Assert.Empty(kit.Player.Inventory.AllItems);
        Assert.Equal(50u, kit.Player.Money);
        Assert.False(kit.State.Quests.Get(QuestId)!.Rewarded);
        Assert.Empty(kit.Session.Sent);
        Assert.Empty(additions);

        kit.Services.ApplyReward(plan);
        Assert.Equal(80u, kit.Player.Money);
        Assert.True(kit.State.Quests.Get(QuestId)!.Rewarded);
        Assert.Equal(ItemTestData.Hearthstone, kit.State.Quests.Get(QuestId)!.RewardChoice);
        Assert.Equal(0u, kit.State.Quests.SlotQuestId(0));
        Assert.Equal(5u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.True(kit.Player.Inventory.AllItems.Single(i => i.Entry == ItemTestData.Hearthstone).IsSoulBound);
        Assert.Equal(2, additions.Count);
        Assert.Empty(kit.Sink.Rows);
        Assert.Equal(2, kit.Session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgItemPushResult));
        Assert.Single(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete);
        var reader = new PacketReader(kit.Session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete).Payload);
        Assert.Equal(QuestId, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(30u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(ItemTestData.ToughJerky, reader.ReadUInt32());
        Assert.Equal(5u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        Assert.Throws<InvalidOperationException>(() => kit.Services.ApplyReward(plan));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out _));
        Assert.Equal(2, additions.Count);
    }

    [Fact]
    public void RewardBatchCollectivelyNeedsTwoSlots_FailureDoesNotGrantItsFirstItem()
    {
        using var kit = new Kit();
        for (int i = 0; i < InventorySlots.ItemEnd - InventorySlots.ItemStart - 1; i++)
        {
            ItemTestData.Give(kit.Player.Inventory, ItemTestData.IndestructibleRock);
        }

        kit.World.RunTick(5);
        kit.Session.Clear();
        int events = 0;
        kit.Player.Inventory.ItemCountChanged += (_, _) => events++;
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out _));
        Assert.Equal(15, kit.Player.Inventory.AllItems.Count());
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.Hearthstone));
        Assert.Equal(50u, kit.Player.Money);
        Assert.False(kit.State.Quests.Get(QuestId)!.Rewarded);
        Assert.Equal(0, events);
        Assert.Equal(new[] { ArcaneCore.Game.Items.InventoryResult.InventoryFull }, ItemTestData.EquipErrors(kit.Session));
    }

    [Fact]
    public void StagingMergesStacksAndUsesEquippedBag_AndPreservesLiveObjectIdentity()
    {
        using var kit = new Kit();
        Item bag = ItemTestData.Give(kit.Player.Inventory, ItemTestData.SmallBrownPouch);
        Assert.True(kit.Player.Inventory.AutoEquipItem(bag.BagSlot, bag.Slot));
        Item stack = ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky, 18);
        for (int i = 0; i < InventorySlots.ItemEnd - InventorySlots.ItemStart - 1; i++)
        {
            ItemTestData.Give(kit.Player.Inventory, ItemTestData.IndestructibleRock);
        }

        kit.Session.Clear();
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out QuestRewardPlan? plan));
        Assert.NotNull(plan);
        Assert.Equal(18u, stack.Count);
        kit.Services.ApplyReward(plan);
        Assert.Same(stack, kit.Player.Inventory.GetItemByGuid(stack.Guid));
        Assert.Same(bag, kit.Player.Inventory.GetItemByGuid(bag.Guid));
        Assert.Equal(20u, stack.Count);
        Assert.Equal(23u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Same(bag, kit.Player.Inventory.AllItems.Single(i => i.Entry == ItemTestData.ToughJerky && i != stack).Container);
        Assert.Equal(plan.InventoryAfter.Items.Select(row => (row.Item.Guid, row.Item.Entry, row.Item.Count)),
            kit.Player.Inventory.CreateSnapshot().Items.Select(row => (row.Item.Guid, row.Item.Entry, row.Item.Count)));
    }

    [Fact]
    public void FixedAndChosenUniqueItemTogether_AreRejectedBeforeAnyLiveGrant()
    {
        var quest = new QuestTemplate { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
            RewItemId1 = ItemTestData.UniqueKey, RewItemCount1 = 1,
            RewChoiceItemId1 = ItemTestData.UniqueKey, RewChoiceItemCount1 = 1 };
        using var kit = new Kit(quest);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out _));
        Assert.Empty(kit.Player.Inventory.AllItems);
        Assert.Equal(new[] { ArcaneCore.Game.Items.InventoryResult.CantCarryMoreOfThis }, ItemTestData.EquipErrors(kit.Session));
    }

    [Theory]
    [InlineData("money")]
    [InlineData("inventory")]
    [InlineData("journal")]
    [InlineData("slot")]
    public void ChangedLiveStateRejectsApplicationBeforePublishing(string changed)
    {
        using var kit = new Kit();
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out QuestRewardPlan? plan));
        Assert.NotNull(plan);
        switch (changed)
        {
            case "money": kit.Player.Money++; break;
            case "inventory": ItemTestData.Give(kit.Player.Inventory, ItemTestData.IndestructibleRock); break;
            case "journal": kit.State.Quests.Get(QuestId)!.CreatureOrGOCount[0]--; break;
            case "slot": kit.State.Quests.SetSlot(0, 0); break;
        }

        kit.Session.Clear();
        Assert.Throws<InvalidOperationException>(() => kit.Services.ApplyReward(plan));
        Assert.False(kit.State.Quests.Get(QuestId)!.Rewarded);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Empty(kit.Session.Sent);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("deadnpc")]
    [InlineData("combat")]
    [InlineData("hostile")]
    [InlineData("range")]
    [InlineData("wrongnpc")]
    [InlineData("unloaded")]
    [InlineData("incomplete")]
    [InlineData("forgedcomplete")]
    [InlineData("notallowed")]
    public void RewardGuardsRejectWithoutMutation(string guard)
    {
        using var kit = new Kit(allowlisted: guard != "notallowed");
        ObjectGuid npc = kit.Creature.Guid;
        switch (guard)
        {
            case "hidden": kit.Player.VisibleObjects.Remove(npc); break;
            case "deadnpc": kit.Creature.Health = 0; break;
            case "combat": kit.Creature.UnitFlags |= UnitFlags.InCombat; break;
            case "hostile": kit.Creature.FactionTemplate = 3; break;
            case "range": kit.Creature.Relocate(40, 0, kit.Player.Z, 0, 0); break;
            case "wrongnpc": npc = ObjectGuid.WithEntry(HighGuid.Unit, 123, 456); break;
            case "unloaded": kit.State.Loaded = false; break;
            case "incomplete": kit.State.Quests.Get(QuestId)!.Status = QuestStatus.Incomplete; break;
            case "forgedcomplete": kit.State.Quests.Get(QuestId)!.CreatureOrGOCount[0] = 0; break;
        }

        Assert.False(kit.Services.TryPrepareReward(kit.Player, npc, QuestId, 0, out _));
        Assert.Equal(50u, kit.Player.Money);
        Assert.False(kit.State.Quests.Get(QuestId)!.Rewarded);
        Assert.Empty(kit.Player.Inventory.AllItems);
        Assert.Empty(kit.Session.Sent);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void InvalidChoiceCannotPrepare(uint choice)
    {
        using var kit = new Kit();
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, choice, out _));
        Assert.Empty(kit.Player.Inventory.AllItems);
    }

    [Fact]
    public void NoChoiceRequiresZero_AndNegativeMoneyRequiresAvailableFunds()
    {
        var quest = new QuestTemplate { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
            RewOrReqMoney = -60, RewItemId1 = ItemTestData.ToughJerky, RewItemCount1 = 1 };
        using var kit = new Kit(quest);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out _));
        kit.Player.Money = 70;
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 1, out _));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out QuestRewardPlan? plan));
        Assert.NotNull(plan);
        Assert.Equal(10u, plan.MoneyAfter);
        Assert.Equal(0u, plan.RewardedQuest.RewardChoice);
        kit.Services.ApplyReward(plan);
        Assert.Equal(10u, kit.Player.Money);
        var reader = new PacketReader(kit.Session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete).Payload);
        reader.Skip(12);
        Assert.Equal(unchecked((uint)-60), reader.ReadUInt32());
    }

    [Fact]
    public void MoneyCapIsAppliedToPreparedAndPublishedState()
    {
        using var kit = new Kit();
        kit.Player.Money = QuestNpcServices.MaxMoneyAmount - 2;
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out QuestRewardPlan? plan));
        Assert.NotNull(plan);
        Assert.Equal(QuestNpcServices.MaxMoneyAmount, plan.MoneyAfter);
        kit.Services.ApplyReward(plan);
        Assert.Equal(plan.MoneyAfter, kit.Player.Money);
        var reader = new PacketReader(kit.Session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete).Payload);
        reader.Skip(12);
        Assert.Equal(30u, reader.ReadUInt32()); // Configured reward, even though the balance only gained two copper.
    }

    [Theory]
    [InlineData("xp")]
    [InlineData("sparse")]
    [InlineData("missingcount")]
    [InlineData("unknownitem")]
    public void UnsupportedOrIncoherentRewardsFailClosed(string unsupported)
    {
        QuestTemplate quest = unsupported switch
        {
            "xp" => new() { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2, RewXP = 1 },
            "sparse" => new() { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
                RewChoiceItemId2 = ItemTestData.Hearthstone, RewChoiceItemCount2 = 1 },
            "missingcount" => new() { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
                RewItemId1 = ItemTestData.Hearthstone },
            _ => new() { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
                RewItemId1 = uint.MaxValue, RewItemCount1 = 1 },
        };
        using var kit = new Kit(quest);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 0, out _));
        Assert.Empty(kit.Player.Inventory.AllItems);
        Assert.False(kit.State.Quests.Get(QuestId)!.Rewarded);
    }

    [Fact]
    public void ChoiceSlotOneStoresItsItemId_AndBankOwnedUniqueItemsCountAgainstRewards()
    {
        var choices = new QuestTemplate { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
            RewChoiceItemId1 = ItemTestData.Hearthstone, RewChoiceItemCount1 = 1,
            RewChoiceItemId2 = ItemTestData.RecruitsShirt, RewChoiceItemCount2 = 1 };
        using (var kit = new Kit(choices))
        {
            Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, QuestId, 1, out QuestRewardPlan? plan));
            Assert.NotNull(plan);
            Assert.Equal(ItemTestData.RecruitsShirt, plan.RewardedQuest.RewardChoice);
            kit.Services.ApplyReward(plan);
            Assert.Equal(1u, kit.Player.Inventory.GetItemCount(ItemTestData.RecruitsShirt));
            Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.Hearthstone));
        }

        var unique = new QuestTemplate { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
            RewItemId1 = ItemTestData.UniqueKey, RewItemCount1 = 1 };
        using var bank = new Kit(unique);
        bank.Player.Inventory.Load([new InventoryItemData(0, InventorySlots.BankItemStart,
            new ItemInstanceData { Guid = 100, Entry = ItemTestData.UniqueKey })]);
        bank.Player.Inventory.GuidAllocator!.Seed(100);
        bank.Session.Clear();
        Assert.False(bank.Services.TryPrepareReward(bank.Player, bank.Creature.Guid, QuestId, 0, out _));
        Assert.Equal(1u, bank.Player.Inventory.GetItemCount(ItemTestData.UniqueKey, inBankAlso: true));
        Assert.Equal(new[] { ArcaneCore.Game.Items.InventoryResult.CantCarryMoreOfThis }, ItemTestData.EquipErrors(bank.Session));
    }

    [Fact]
    public void CombatDeathSignalIsEmittedOnceAfterTheAuthoritativeTransition()
    {
        using var kit = new Kit();
        int deaths = 0;
        Map map = kit.Player.Map!;
        map.Combat.UnitKilled += (killer, victim) =>
        {
            deaths++;
            Assert.Same(kit.Player, killer);
            Assert.Same(kit.Creature, victim);
            Assert.Equal(0u, victim.Health);
            Assert.False(victim.IsAlive);
        };
        map.Combat.Kill(kit.Player, kit.Creature);
        map.Combat.Kill(kit.Player, kit.Creature);
        Assert.Equal(1, deaths);
    }

    [Fact]
    public void TurnInRequestsCannotCreditMissingKills_AndTalkingCannotCreditOrdinaryKills()
    {
        using var kit = new Kit(complete: false);
        kit.Services.CompleteQuest(kit.Player, kit.Creature.Guid, QuestId);
        Assert.Equal(WorldOpcode.SmsgQuestgiverRequestItems, kit.Session.Next().Opcode);
        kit.Services.RequestReward(kit.Player, kit.Creature.Guid, QuestId);
        kit.Services.TalkedToCreature(kit.Player, 90, kit.Creature.Guid);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.Get(QuestId)!.Status);
        Assert.Equal(1u, kit.State.Quests.Get(QuestId)!.CreatureOrGOCount[0]);
        Assert.Empty(kit.Sink.Rows);
        Assert.Empty(kit.Session.Sent);
        kit.Services.KilledMonsterCredit(kit.Player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 2));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.Get(QuestId)!.Status);
        kit.Session.Clear();
        kit.Services.RequestReward(kit.Player, kit.Creature.Guid, QuestId);
        Assert.Equal(WorldOpcode.SmsgQuestgiverOfferReward, kit.Session.Next().Opcode);
    }

    private const uint QuestId = 900001;

    private sealed class Kit : IDisposable
    {
        public Kit(QuestTemplate? quest = null, bool allowlisted = true, bool complete = true)
        {
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            ItemTestData.Wire(Player.Inventory);
            Player.Inventory.Load([]);
            Player.Money = 50;
            World.AddPlayer(Player);
            var template = new CreatureTemplate { Entry = 900010, Name = "Reward questgiver", Faction = 2, NpcFlags = 2 };
            Creature = new Creature(900020, template,
                new CreatureSpawn { Guid = 900020, Entry = template.Entry, MapId = 0, X = 0, Y = 0, Z = Player.Z }, CreatureContent.Empty, new Random(1));
            Player.Map!.AddObject(Creature);
            World.RunTick(5);
            quest ??= new QuestTemplate { Entry = QuestId, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
                RewItemId1 = ItemTestData.ToughJerky, RewItemCount1 = 5,
                RewChoiceItemId1 = ItemTestData.Hearthstone, RewChoiceItemCount1 = 1, RewOrReqMoney = 30 };
            var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0), new(3, 0, 0, 8, 0, 1)]);
            Services = new QuestNpcServices(new QuestStore(new QuestContent([quest], [],
                [new CreatureQuestRelation { Id = template.Entry, Quest = QuestId }])), NpcStore.Empty,
                new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions)),
                new QuestNpcOptions { OrdinaryRewardQuestIds = allowlisted ? [QuestId] : [], RewardMode = QuestRewardMode.AllowlistOnly }, Sink,
                () => 100, NullLogger.Instance);
            State = Services.Track(Player);
            Services.CompleteLoad(State, new CharacterQuestData([new(1, QuestId,
                (byte)(complete ? QuestStatus.Complete : QuestStatus.Incomplete), false, false, 0,
                complete ? 2u : 1u, 0, 0, 0, 0, 0, 0, 0, 0)], []));
            Session.Clear();
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeSession Session { get; } = new();
        public RecordingSink Sink { get; } = new();
        public Player Player { get; }
        public Creature Creature { get; }
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
