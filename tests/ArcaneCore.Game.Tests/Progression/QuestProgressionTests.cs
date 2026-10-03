using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using InventoryResult = ArcaneCore.Game.Items.InventoryResult;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Fx = ArcaneCore.Game.Tests.Reputation.ReputationFixtures;
using ArcaneCore.Game.Tests.Reputation;

namespace ArcaneCore.Game.Tests.Progression;

/// <summary>The broadened ordinary quest vertical: XP, adapters, repeatables and adverse cases.</summary>
public sealed class QuestProgressionTests
{
    private const uint Giver = 900010;

    [Fact]
    public void QuestXp_IsPreviewedIntoThePlan_ThenPublishedWithTheCompletePacket()
    {
        var quest = new QuestTemplate { Entry = 910001, Method = 2, QuestLevel = 1, RewXP = 500 };
        using var kit = new Kit([quest]);
        Assert.True(kit.Accept(910001));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910001));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910001, 0, out QuestRewardPlan? plan));
        Assert.Equal(500u, plan.Experience);
        Assert.Equal(1, plan.LevelBefore);
        Assert.Equal(2, plan.LevelAfter);
        Assert.Equal(1, kit.Player.Level); // preparation is detached
        kit.Session.Clear();

        kit.Settle(plan);
        Assert.Equal(2, kit.Player.Level);
        Assert.Equal(100u, PlayerProgression.CurrentXp(kit.Player));
        Assert.Single(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgLogXpgain);
        Assert.Single(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgLevelupInfo);
        var reader = new PacketReader(kit.Session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete).Payload);
        Assert.Equal(910001u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(500u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
    }

    [Theory]
    [InlineData(15, 1000u)]
    [InlineData(16, 800u)]
    [InlineData(17, 600u)]
    [InlineData(18, 400u)]
    [InlineData(19, 200u)]
    [InlineData(20, 100u)]
    [InlineData(40, 100u)]
    public void QuestXp_IsReducedAboveQuestLevelPlusFive(byte playerLevel, uint xp)
    {
        var quest = new QuestTemplate { Entry = 910002, Method = 2, QuestLevel = 10, RewXP = 1000 };
        using var kit = new Kit([quest], level: playerLevel);
        Assert.True(kit.Accept(910002));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910002, 0, out QuestRewardPlan? plan));
        Assert.Equal(xp, plan.Experience);
    }

    [Theory]
    [InlineData(1, 40u)]
    [InlineData(8, 24u)]
    [InlineData(10, 8u)]
    [InlineData(12, 4u)]
    public void QuestXp_ForClassicDbData_IsDerivedFromRewMoneyMaxLevel(byte playerLevel, uint xp)
    {
        // classic-db has no RewXP column: cmangos derives the experience from RewMoneyMaxLevel (QuestDef.cpp:171-206).
        var quest = new QuestTemplate { Entry = 910008, Method = 2, QuestLevel = 1, RewMoneyMaxLevel = 24 };
        using var kit = new Kit([quest], level: playerLevel);
        Assert.True(kit.Accept(910008));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910008, 0, out QuestRewardPlan? plan));
        Assert.Equal(xp, plan.Experience);
    }

    [Fact]
    public void QuestXp_InAVmangosDataset_StaysTheRewXpColumn_EvenForAQuestThatOnlyHasMoney()
    {
        // Auto resolves per dataset: one quest with RewXP makes the whole set column-based, so a quest with only
        // RewMoneyMaxLevel earns no XP there (vmangos Quest::XPValue reads RewXP only).
        var withColumn = new QuestTemplate { Entry = 910009, Method = 2, QuestLevel = 1, RewXP = 100 };
        var moneyOnly = new QuestTemplate { Entry = 910010, Method = 2, QuestLevel = 1, RewMoneyMaxLevel = 24 };
        using var kit = new Kit([withColumn, moneyOnly]);
        Assert.True(kit.Accept(910009));
        Assert.True(kit.Accept(910010));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910009, 0, out QuestRewardPlan? column));
        Assert.Equal(100u, column.Experience);
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910010, 0, out QuestRewardPlan? money));
        Assert.Equal(0u, money.Experience);
    }

    [Theory]
    [InlineData(QuestXpSource.Derived, 40u)]
    [InlineData(QuestXpSource.RewXpColumn, 0u)]
    public void QuestXp_TheSourceCanBeForcedByConfiguration(QuestXpSource source, uint xp)
    {
        var quest = new QuestTemplate { Entry = 910011, Method = 2, QuestLevel = 1, RewMoneyMaxLevel = 24 };
        using var kit = new Kit([quest], configure: o => o.XpSource = source);
        Assert.True(kit.Accept(910011));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910011, 0, out QuestRewardPlan? plan));
        Assert.Equal(xp, plan.Experience);
    }

    [Fact]
    public void QuestXp_AppliesRateXpQuest()
    {
        var quest = new QuestTemplate { Entry = 910003, Method = 2, QuestLevel = 10, RewXP = 1000 };
        using var kit = new Kit([quest], level: 10, configure: o => o.RateXpQuest = 2.0f);
        Assert.True(kit.Accept(910003));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910003, 0, out QuestRewardPlan? plan));
        Assert.Equal(2000u, plan.Experience);
    }

    [Fact]
    public void MaximumLevel_PaysRewMoneyMaxLevelInsteadOfXp()
    {
        var quest = new QuestTemplate { Entry = 910004, Method = 2, QuestLevel = 60, RewXP = 500, RewMoneyMaxLevel = 120, RewOrReqMoney = 30 };
        using var kit = new Kit([quest], level: 60);
        Assert.True(kit.Accept(910004));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910004, 0, out QuestRewardPlan? plan));
        Assert.Equal(0u, plan.Experience);
        Assert.Equal(60, plan.LevelAfter);
        Assert.Equal(200u, plan.MoneyAfter);
        kit.Session.Clear();
        kit.Settle(plan);
        Assert.DoesNotContain(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgLogXpgain);
        var reader = new PacketReader(kit.Session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete).Payload);
        reader.ReadUInt32();
        reader.ReadUInt32();
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(150u, reader.ReadUInt32());
    }

    [Fact]
    public void XpReward_WithoutAnExperienceOwner_FailsClosed()
    {
        var quest = new QuestTemplate { Entry = 910005, Method = 2, RewXP = 500 };
        using var kit = new Kit([quest], progression: false);
        Assert.True(kit.Accept(910005));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910005, 0, out _));
    }

    [Fact]
    public void DeliveryObjective_FollowsInventoryAddAndRemove_AndTheRewardTakesTheItems()
    {
        var quest = new QuestTemplate { Entry = 910010, Method = 2, ReqItemId1 = ItemTestData.ToughJerky, ReqItemCount1 = 3, RewOrReqMoney = 10 };
        using var kit = new Kit([quest]);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky, 2);
        Assert.True(kit.Accept(910010));
        QuestStatusData data = kit.State.Quests.Get(910010)!;
        Assert.Equal(2u, data.ItemCount[0]); // AdjustQuestReqItemCount on accept
        Assert.Equal(QuestStatus.Incomplete, data.Status);

        kit.Session.Clear();
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky, 3);
        Assert.Equal(3u, data.ItemCount[0]);
        Assert.Equal(QuestStatus.Complete, data.Status);
        var add = new PacketReader(kit.Session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgQuestupdateAddItem).Payload);
        Assert.Equal(ItemTestData.ToughJerky, add.ReadUInt32());
        Assert.Equal(1u, add.ReadUInt32());

        Assert.Equal(3u, kit.Player.Inventory.DestroyItemCount(ItemTestData.ToughJerky, 3));
        Assert.Equal(2u, data.ItemCount[0]);
        Assert.Equal(QuestStatus.Incomplete, data.Status);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910010, 0, out _));

        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky, 3);
        Assert.Equal(QuestStatus.Complete, data.Status);
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910010, 0, out QuestRewardPlan? plan));
        Assert.Equal(5u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky)); // detached
        Assert.Equal(2u, plan.InventoryAfter.Items.Where(i => i.Item.Entry == ItemTestData.ToughJerky).Sum(i => (long)i.Item.Count));
        kit.Settle(plan);
        Assert.Equal(2u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.True(data.Rewarded);
        Assert.Equal(60u, kit.Player.Money);
    }

    [Fact]
    public void DeliveryReward_RemovesWholeStacksAndCountsDestroyEvents()
    {
        var quest = new QuestTemplate { Entry = 910011, Method = 2, ReqItemId1 = ItemTestData.IndestructibleRock, ReqItemCount1 = 2 };
        using var kit = new Kit([quest]);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.IndestructibleRock);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.IndestructibleRock);
        Assert.True(kit.Accept(910011));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910011, 0, out QuestRewardPlan? plan));
        var events = new List<(uint, int)>();
        kit.Player.Inventory.ItemCountChanged += (entry, delta) => events.Add((entry, delta));
        kit.Settle(plan);
        Assert.Empty(kit.Player.Inventory.AllItems);
        Assert.Equal([(ItemTestData.IndestructibleRock, -2)], events);
    }

    [Fact]
    public void SourceItem_IsGivenOnAccept_AndTakenOnAbandon()
    {
        var quest = new QuestTemplate { Entry = 910020, Method = 2, SrcItemId = ItemTestData.UniqueKey, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1 };
        using var kit = new Kit([quest]);
        Assert.True(kit.Accept(910020));
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(ItemTestData.UniqueKey));
        Assert.Contains(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgItemPushResult);
        Assert.True(kit.Services.AbandonQuest(kit.Player, 0));
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.UniqueKey));
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(910020));
    }

    [Fact]
    public void SourceItem_ThatIsAlsoRequired_IsDestroyedOnAbandon()
    {
        // vmangos TakeOrReplaceQuestStartItems (Player.cpp:13696-13759) destroys SrcItemCount whenever the player owns it;
        // there is no exception for items the quest also requires (the earlier "kept" rule was not in vmangos).
        var quest = new QuestTemplate { Entry = 910021, Method = 2, SrcItemId = ItemTestData.ToughJerky, SrcItemCount = 3,
            ReqItemId1 = ItemTestData.ToughJerky, ReqItemCount1 = 3 };
        using var kit = new Kit([quest]);
        Assert.True(kit.Accept(910021));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910021));
        Assert.DoesNotContain(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestupdateAddItem);
        Assert.True(kit.Services.AbandonQuest(kit.Player, 0));
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
    }

    [Fact]
    public void SourceItem_WithAFullBackpack_RejectsAcceptWithQuestFailedInventoryFull()
    {
        var quest = new QuestTemplate { Entry = 910023, Method = 2, SrcItemId = ItemTestData.Hearthstone };
        using var kit = new Kit([quest]);
        for (int i = 0; i < InventorySlots.ItemEnd - InventorySlots.ItemStart; i++)
        {
            ItemTestData.Give(kit.Player.Inventory, ItemTestData.IndestructibleRock);
        }

        kit.Session.Clear();
        kit.Sink.Rows.Clear();
        Assert.False(kit.Accept(910023));
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(910023));
        Assert.Empty(kit.Sink.Rows);
        // CanGiveQuestSourceItemIfNeed (Player.cpp:13643-13676) answers EQUIP_ERR_INVENTORY_FULL with
        // SMSG_QUESTGIVER_QUEST_FAILED (quest, INVALIDREASON_QUEST_FAILED_INVENTORY_FULL), not an equip error.
        Assert.Empty(ItemTestData.EquipErrors(kit.Session));
        Assert.Equal([0xC7, 0xE2, 0x0D, 0x00, 4, 0, 0, 0],
            Assert.Single(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestFailed).Payload);
    }

    [Fact]
    public void FullInventory_BlocksTheItemReward_ButADeliveryFreesItsOwnSlots()
    {
        var quest = new QuestTemplate { Entry = 910024, Method = 2, ReqItemId1 = ItemTestData.IndestructibleRock, ReqItemCount1 = 1,
            RewItemId1 = ItemTestData.Hearthstone, RewItemCount1 = 1 };
        var blocked = new QuestTemplate { Entry = 910025, Method = 2, RewItemId1 = ItemTestData.Hearthstone, RewItemCount1 = 1 };
        using var kit = new Kit([quest, blocked]);
        for (int i = 0; i < InventorySlots.ItemEnd - InventorySlots.ItemStart; i++)
        {
            ItemTestData.Give(kit.Player.Inventory, ItemTestData.IndestructibleRock);
        }

        Assert.True(kit.Accept(910025));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910025, 0, out _));
        // Bag space is QUESTGIVER_QUEST_FAILED reason 4 (Player.cpp:12755-12760), not an equip error.
        Assert.DoesNotContain(InventoryResult.InventoryFull, ItemTestData.EquipErrors(kit.Session));
        Assert.Contains(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestFailed);
        Assert.True(kit.Accept(910024));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910024, 0, out QuestRewardPlan? plan));
        kit.Settle(plan);
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(ItemTestData.Hearthstone));
        Assert.Equal(15u, kit.Player.Inventory.GetItemCount(ItemTestData.IndestructibleRock));
    }

    [Fact]
    public void Exploration_NeedsAnAreaTriggerRelation_ThenTheTriggerCompletesIt()
    {
        var quest = new QuestTemplate { Entry = 910030, Method = 2, SpecialFlags = (byte)QuestSpecialFlags.ExplorationOrEvent };
        using (var unrelated = new Kit([quest]))
        {
            Assert.False(unrelated.Accept(910030));
        }

        using var kit = new Kit([quest], configure: o => o.AreaTriggerQuests = [new QuestAreaTrigger { TriggerId = 77, QuestId = 910030 }]);
        Assert.True(kit.Accept(910030));
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(910030));
        Assert.Equal(0, kit.Services.AreaTriggerReached(kit.Player, 76));
        kit.Session.Clear();
        Assert.Equal(1, kit.Services.AreaTriggerReached(kit.Player, 77));
        Assert.True(kit.State.Quests.Get(910030)!.Explored);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910030));
        Assert.Single(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestupdateComplete);
        Assert.Equal(0, kit.Services.AreaTriggerReached(kit.Player, 77)); // no longer incomplete
    }

    [Fact]
    public void Exploration_DeadPlayersDoNotTriggerIt()
    {
        var quest = new QuestTemplate { Entry = 910031, Method = 2, SpecialFlags = (byte)QuestSpecialFlags.ExplorationOrEvent };
        using var kit = new Kit([quest], configure: o => o.AreaTriggerQuests = [new QuestAreaTrigger { TriggerId = 78, QuestId = 910031 }]);
        Assert.True(kit.Accept(910031));
        kit.Player.Health = 0;
        Assert.Equal(0, kit.Services.AreaTriggerReached(kit.Player, 78));
        Assert.False(kit.State.Quests.Get(910031)!.Explored);
    }

    [Fact]
    public void SpellCastObjective_NeedsTheSpell_AndOtherMembersOnlyProgressSharableQuests()
    {
        var personal = new QuestTemplate { Entry = 910040, Method = 2, ReqCreatureOrGOId1 = 95, ReqCreatureOrGOCount1 = 1, ReqSpellCast1 = 1234 };
        var sharable = new QuestTemplate { Entry = 910041, Method = 2, QuestFlags = (uint)QuestFlags.Sharable,
            ReqCreatureOrGOId1 = 95, ReqCreatureOrGOCount1 = 1, ReqSpellCast1 = 1234 };
        using var kit = new Kit([personal, sharable]);
        Assert.True(kit.Accept(910040));
        Assert.True(kit.Accept(910041));
        var target = new ObjectGuid(0xF130000000000095);
        kit.Services.KilledMonsterCredit(kit.Player, 95, target);
        kit.Services.CastedCreatureOrGo(kit.Player, 95, target, true, 999, originalCaster: true);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(910040));
        kit.Services.CastedCreatureOrGo(kit.Player, 95, target, true, 1234, originalCaster: false);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(910040));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910041));
        kit.Services.CastedCreatureOrGo(kit.Player, 95, target, true, 1234, originalCaster: true);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910040));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910040, 0, out _));
    }

    [Fact]
    public void GameObjectUseObjective_CountsUsesAndFlagsTheEntryInTheUpdate()
    {
        var quest = new QuestTemplate { Entry = 910042, Method = 2, ReqCreatureOrGOId1 = -300, ReqCreatureOrGOCount1 = 2 };
        using var kit = new Kit([quest]);
        Assert.True(kit.Accept(910042));
        var go = new ObjectGuid(0xF110000000000300);
        kit.Session.Clear();
        kit.Services.CastedCreatureOrGo(kit.Player, 300, go, isCreature: false, spellId: 0);
        kit.Services.KilledMonsterCredit(kit.Player, 300, go); // a creature with that entry is not the object
        Assert.Equal(1u, kit.State.Quests.Get(910042)!.CreatureOrGOCount[0]);
        var update = new PacketReader(kit.Session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgQuestupdateAddKill).Payload);
        Assert.Equal(910042u, update.ReadUInt32());
        Assert.Equal(300u | 0x80000000u, update.ReadUInt32());
        kit.Services.CastedCreatureOrGo(kit.Player, 300, go, isCreature: false, spellId: 0);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910042));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910042, 0, out _));
    }

    [Fact]
    public void RaidGroupKills_OnlyCreditRaidQuests()
    {
        var normal = new QuestTemplate { Entry = 910050, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1 };
        var raid = new QuestTemplate { Entry = 910051, Method = 2, Type = QuestNpcServices.QuestTypeRaid, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1 };
        using var kit = new Kit([normal, raid]);
        Assert.True(kit.Accept(910050));
        Assert.True(kit.Accept(910051));
        kit.Services.KilledMonsterCredit(kit.Player, 90, new ObjectGuid(0xF130000000000090), inRaidGroup: true);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(910050));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910051));
        kit.Services.KilledMonsterCredit(kit.Player, 90, new ObjectGuid(0xF130000000000091), inRaidGroup: false);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910050));
    }

    [Fact]
    public void Repeatable_ReturnsToNone_MayBeTakenAgain_AndPaysEveryTime()
    {
        var quest = new QuestTemplate { Entry = 910060, Method = 2, QuestLevel = 1, SpecialFlags = (byte)QuestSpecialFlags.Repeatable,
            RewXP = 100, RewOrReqMoney = 5, ReqItemId1 = ItemTestData.ToughJerky, ReqItemCount1 = 1 };
        using var kit = new Kit([quest]);
        for (int round = 1; round <= 2; round++)
        {
            ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
            Assert.True(kit.Accept(910060));
            Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910060));
            Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910060, 0, out QuestRewardPlan? plan));
            Assert.Equal(round > 1, plan.ExpectedQuest.Rewarded);
            Assert.Equal((byte)QuestStatus.Complete, plan.ExpectedQuest.Status);
            Assert.Equal((byte)QuestStatus.None, plan.RewardedQuest.Status);
            Assert.True(plan.RewardedQuest.Rewarded);
            kit.Settle(plan);
            Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(910060));
            Assert.True(kit.State.Quests.Get(910060)!.Rewarded);
            Assert.Equal(0u, kit.State.Quests.SlotQuestId(0));
            Assert.Equal(round * 100u, PlayerProgression.CurrentXp(kit.Player));
            Assert.Equal(50u + (round * 5u), kit.Player.Money);
            Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        }
    }

    [Fact]
    public void NonRepeatable_CannotBeTakenAgainAfterItsReward()
    {
        var quest = new QuestTemplate { Entry = 910061, Method = 2, RewOrReqMoney = 5 };
        using var kit = new Kit([quest]);
        Assert.True(kit.Accept(910061));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910061, 0, out QuestRewardPlan? plan));
        Assert.Equal((byte)QuestStatus.Complete, plan.RewardedQuest.Status);
        kit.Settle(plan);
        Assert.False(kit.Accept(910061));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910061, 0, out _));
    }

    [Fact]
    public void RelogMidProgress_KeepsKillCounters_AndReconcilesDeliveryWithTheLoadedBags()
    {
        var kill = new QuestTemplate { Entry = 910070, Method = 2, ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2 };
        var deliver = new QuestTemplate { Entry = 910071, Method = 2, ReqItemId1 = ItemTestData.ToughJerky, ReqItemCount1 = 3 };
        var overstated = new QuestTemplate { Entry = 910072, Method = 2, ReqItemId1 = ItemTestData.Hearthstone, ReqItemCount1 = 1 };
        // Persisted rows: one kill of two; the jerky delta was lost (inventory saved 3); the
        // hearthstone row claims completion although the bags no longer hold it.
        CharacterQuestStatus[] rows =
        [
            new(1, 910070, (byte)QuestStatus.Incomplete, false, false, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0),
            new(1, 910071, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0),
            new(1, 910072, (byte)QuestStatus.Complete, false, false, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0),
        ];
        using var kit = new Kit([kill, deliver, overstated], rows: rows, beforeLoad: p => ItemTestData.Give(p.Inventory, ItemTestData.ToughJerky, 3));
        kit.Services.ReconcileItemCounts(kit.Player);
        Assert.Equal(1u, kit.State.Quests.Get(910070)!.CreatureOrGOCount[0]);
        Assert.Equal(3u, kit.State.Quests.Get(910071)!.ItemCount[0]);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910071));
        Assert.Equal(0u, kit.State.Quests.Get(910072)!.ItemCount[0]);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(910072));
        Assert.Contains(kit.Sink.Rows, r => r.Quest == 910071 && r.ItemCount1 == 3);

        kit.Services.KilledMonsterCredit(kit.Player, 90, new ObjectGuid(0xF130000000000090));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(910070));
    }

    [Fact]
    public void RewardSpell_NeedsTheEffectsOwner_AndRunsOnceAfterTheSettlementReleased()
    {
        var quest = new QuestTemplate { Entry = 910080, Method = 2, RewSpell = 5555 };
        using (var missing = new Kit([quest], effects: null))
        {
            Assert.True(missing.Accept(910080));
            Assert.False(missing.Services.TryPrepareReward(missing.Player, missing.Creature.Guid, 910080, 0, out _));
        }

        using (var unknown = new Kit([quest], effects: new RecordingEffects(canCast: false)))
        {
            Assert.True(unknown.Accept(910080));
            Assert.False(unknown.Services.TryPrepareReward(unknown.Player, unknown.Creature.Guid, 910080, 0, out _));
        }

        var effects = new RecordingEffects(canCast: true);
        using var kit = new Kit([new QuestTemplate { Entry = 910080, Method = 2, RewSpell = 5555, RewSpellCast = 6666 }], effects: effects);
        Assert.True(kit.Accept(910080));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910080, 0, out QuestRewardPlan? plan));
        kit.Services.PublishRewardEffects(plan); // not applied yet
        Assert.Empty(effects.Calls);
        Guid operation = Guid.NewGuid();
        Assert.True(kit.Player.BeginQuestSettlement(operation));
        using (kit.Player.BeginQuestSettlementPublication(operation))
        {
            kit.Services.ApplyReward(plan);
        }

        kit.Services.PublishRewardEffects(plan); // still held
        Assert.Empty(effects.Calls);
        Assert.True(kit.Player.EndQuestSettlement(operation));
        kit.Services.PublishRewardEffects(plan);
        kit.Services.PublishRewardEffects(plan);
        (uint questId, ObjectGuid giver) = Assert.Single(effects.Calls);
        Assert.Equal(910080u, questId);
        Assert.Equal(kit.Creature.Guid, giver);
        Assert.Equal(6666u, QuestNpcServices.RewardSpell(kit.Services.Quests.Get(910080)!));
        Assert.Equal([6666u], effects.Checked);
    }

    [Theory]
    [InlineData(1u)] // SrcSpell
    [InlineData(2u)] // party accept
    [InlineData(3u)] // loot source counters
    [InlineData(4u)] // PvP quest type
    [InlineData(6u)] // reputation objective without a reputation owner
    public void QuestsWithoutAdapters_StillFailClosedAtAccept(uint variant)
    {
        QuestTemplate quest = variant switch
        {
            1 => new QuestTemplate { Entry = 910090, Method = 2, SrcSpell = 100 },
            2 => new QuestTemplate { Entry = 910090, Method = 2, QuestFlags = (uint)QuestFlags.PartyAccept },
            3 => new QuestTemplate { Entry = 910090, Method = 2, ReqSourceId1 = ItemTestData.ToughJerky, ReqSourceCount1 = 1 },
            4 => new QuestTemplate { Entry = 910090, Method = 2, Type = 41 },
            _ => new QuestTemplate { Entry = 910090, Method = 2, RepObjectiveFaction = 72, RepObjectiveValue = 3000 },
        };
        using var kit = new Kit([quest]);
        Assert.False(kit.Accept(910090));
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(910090));
    }

    [Fact]
    public void AllowlistOnlyMode_KeepsTheAllowlistAsTheRewardOptIn()
    {
        var quest = new QuestTemplate { Entry = 910091, Method = 2, RewXP = 100 };
        using var kit = new Kit([quest], configure: o => { o.OrdinaryRewardQuestIds = []; o.RewardMode = QuestRewardMode.AllowlistOnly; });
        Assert.True(kit.Accept(910091));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910091, 0, out _));
    }

    [Fact]
    public void IncoherentObjectives_AreNotRewarded()
    {
        var quest = new QuestTemplate { Entry = 910092, Method = 2, ReqItemId1 = ItemTestData.ToughJerky }; // count missing
        var spellWithoutTarget = new QuestTemplate { Entry = 910093, Method = 2, ReqSpellCast1 = 1234 };
        using var kit = new Kit([quest, spellWithoutTarget]);
        Assert.True(kit.Accept(910092));
        Assert.True(kit.Accept(910093));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910092, 0, out _));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910093, 0, out _));
    }

    [Fact]
    public void ReputationRewardQuest_WithoutSettlementOwner_FailsClosed()
    {
        // Previously such a quest was paid silently with no reputation at all.
        var quest = new QuestTemplate { Entry = 910095, Method = 2, RewRepFaction1 = Fx.BootyBay, RewRepValue1 = 250 };
        using var kit = new Kit([quest]);
        Assert.True(kit.Accept(910095));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910095, 0, out _));
    }

    [Fact]
    public void ReputationPairWithAZeroValueOrFaction_IsIgnoredLikeVmangos()
    {
        var quest = new QuestTemplate
        {
            Entry = 910096, Method = 2, RewRepFaction1 = Fx.BootyBay, RewRepValue1 = 0, RewRepFaction2 = 0, RewRepValue2 = 50,
        };
        using var kit = new Kit([quest]); // no owner needed: nothing is rewarded
        Assert.True(kit.Accept(910096));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910096, 0, out QuestRewardPlan? plan));
        Assert.Empty(plan.Reputation.After);
    }

    [Fact]
    public void ReputationReward_IsStagedOnACopy_PersistedAsRows_AndPublishedInsideApplyReward()
    {
        var quest = new QuestTemplate
        {
            Entry = 910097, Method = 2, RewRepFaction1 = Fx.BootyBay, RewRepValue1 = 250, RewRepFaction2 = Fx.Stormwind, RewRepValue2 = 100,
        };
        var sink = new RecordingReputationSink();
        var service = new ReputationService(Fx.Factions, sink: sink, roll: () => 0.999);
        using var kit = new Kit([quest], reputationRewards: service);
        service.Track(kit.Player, service.Create(kit.Player, new CharacterReputationData([], -1)));
        service.BuildInitializeFactions(kit.Player);
        Assert.True(kit.Accept(910097));
        kit.Session.Clear();

        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 910097, 0, out QuestRewardPlan? plan));
        // Preparation touched nothing: no standing, no packet, no queued write.
        Assert.Equal(0, service.GetReputation(kit.Player, Fx.BootyBay));
        Assert.Empty(kit.Session.Sent);
        Assert.Empty(sink.Rows);
        Assert.Equal([Fx.BootyBay, Fx.Stormwind], plan.Reputation.After.Select(r => r.Faction).Order());

        kit.Settle(plan);

        foreach (CharacterReputationRow row in plan.Reputation.After)
        {
            Assert.Equal(row.Standing, service.GetReputation(kit.Player, row.Faction) - service.For(kit.Player)!.BaseReputation(Fx.Factions.Find(row.Faction)!));
        }

        Assert.Empty(sink.Rows); // the reward transaction persisted them; the queue is not used
        Assert.Empty(service.For(kit.Player)!.TakeDirty(1));
        var opcodes = kit.Session.Sent.Select(p => p.Opcode).ToList();
        int standing = opcodes.IndexOf(WorldOpcode.SmsgSetFactionStanding);
        int complete = opcodes.IndexOf(WorldOpcode.SmsgQuestgiverQuestComplete);
        Assert.True(standing >= 0 && complete > standing, "reputation updates precede the quest completion packet");
    }

    private sealed class RecordingEffects(bool canCast) : IQuestRewardEffects
    {
        public List<(uint Quest, ObjectGuid Giver)> Calls { get; } = [];
        public List<uint> Checked { get; } = [];

        public bool CanCastRewardSpell(uint spellId)
        {
            Checked.Add(spellId);
            return canCast;
        }

        public List<uint> Prepared { get; } = [];

        public bool TryPrepareRewardSpell(Player player, ObjectGuid questGiver, uint spellId, out QuestRewardSpellGrant grant)
        {
            Prepared.Add(spellId);
            grant = new QuestRewardSpellGrant(true, [], []);
            return canCast;
        }

        public void AnnounceLearnedSpells(Player player, QuestRewardSpellGrant grant)
        {
        }

        public void PublishRewardSpell(Player player, Quest quest, ObjectGuid questGiver, QuestRewardSpellGrant grant)
            => Calls.Add((quest.Id, questGiver));
    }

    internal sealed class RecordingSink : IQuestNpcSink
    {
        public List<CharacterQuestStatus> Rows { get; } = [];
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) => Rows.AddRange(rows);
        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) { }
        public void CharacterChanged(Player player) { }
    }

    private sealed class Kit : IDisposable
    {
        public Kit(IReadOnlyList<QuestTemplate> quests, byte level = 1, bool progression = true, IQuestRewardEffects? effects = null,
            Action<QuestNpcOptions>? configure = null, IReadOnlyList<CharacterQuestStatus>? rows = null, Action<Player>? beforeLoad = null,
            IQuestReputationSettlement? reputationRewards = null)
        {
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            Player.Level = level;
            ItemTestData.Wire(Player.Inventory);
            Player.Inventory.Load([]);
            Player.Money = 50;
            World.AddPlayer(Player);
            var template = new CreatureTemplate { Entry = Giver, Name = "Progression questgiver", Faction = 2, NpcFlags = 2 };
            Creature = new Creature(900020, template,
                new CreatureSpawn { Guid = 900020, Entry = Giver, MapId = 0, X = 0, Y = 0, Z = Player.Z }, CreatureContent.Empty, new Random(1));
            Player.Map!.AddObject(Creature);
            World.RunTick(5);
            Progression = progression ? new PlayerProgression(new ProgressionOptions()) : null;
            Progression?.InitializeLoadedPlayer(Player);
            var options = new QuestNpcOptions { OrdinaryRewardQuestIds = quests.Select(q => q.Entry).ToArray() };
            configure?.Invoke(options);
            var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0)]);
            CreatureQuestRelation[] relations = quests.Select(q => new CreatureQuestRelation { Id = Giver, Quest = q.Entry }).ToArray();
            Services = new QuestNpcServices(new QuestStore(new QuestContent(quests, relations, relations)), NpcStore.Empty,
                new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions), Experience: Progression, RewardEffects: effects,
                    ReputationRewards: reputationRewards),
                options, Sink, () => 100, NullLogger.Instance);
            beforeLoad?.Invoke(Player);
            State = Services.Track(Player);
            Services.CompleteLoad(State, new CharacterQuestData(rows ?? [], []));
            // The world feature's item-collect adapter (QuestNpcFeature login wiring).
            Player.Inventory.ItemCountChanged += (entry, delta) =>
            {
                if (delta > 0)
                {
                    Services.ItemAdded(Player, entry, (uint)delta);
                }
                else
                {
                    Services.ItemRemoved(Player, entry, (uint)-delta);
                }
            };
            Session.Clear();
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeSession Session { get; } = new();
        public RecordingSink Sink { get; } = new();
        public Player Player { get; }
        public Creature Creature { get; }
        public PlayerProgression? Progression { get; }
        public QuestNpcServices Services { get; }
        public PlayerNpcState State { get; }

        public bool Accept(uint questId) => Services.AcceptQuest(Player, Creature.Guid, questId);

        public void Settle(QuestRewardPlan plan)
        {
            Guid operation = Guid.NewGuid();
            Assert.True(Player.BeginQuestSettlement(operation));
            using (Player.BeginQuestSettlementPublication(operation))
            {
                Services.ApplyReward(plan);
            }

            Assert.True(Player.EndQuestSettlement(operation));
            Services.PublishRewardEffects(plan);
        }

        public void Dispose() => World.Dispose();
    }
}
