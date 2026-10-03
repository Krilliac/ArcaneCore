using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using ItemInventoryResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// The ordinary creature kill quest item/money settlement slice. vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0 QuestHandler.cpp and Player.cpp
/// HandleQuestgiverCompleteQuestOpcode, HandleQuestgiverRequestRewardOpcode,
/// CanRewardQuest and RewardQuest. Unsupported rewards fail closed.
/// </summary>
public sealed partial class QuestNpcServices
{
    /// <summary>Open the turn-in window using current server progress; this request never credits an objective.</summary>
    public void CompleteQuest(Player player, ObjectGuid guid, uint questId)
    {
        if (!TryRewardQuest(player, guid, questId, out PlayerNpcState? state, out Quest? quest)
            || state.Quests.Get(questId) is not { Rewarded: false, Status: QuestStatus.Incomplete or QuestStatus.Complete } data)
        {
            return;
        }

        bool complete = data.Status == QuestStatus.Complete && ObjectivesComplete(player, quest, data);
        Send(player, complete ? WorldOpcode.SmsgQuestgiverOfferReward : WorldOpcode.SmsgQuestgiverRequestItems,
            complete ? QuestPackets.OfferReward(guid, quest, Options.RateDropMoney, RewardDisplayOf(player))
                : QuestPackets.RequestItems(guid, quest, false, RewardDisplayOf(player), closeOnCancel: false));
    }

    /// <summary>Only a genuinely complete accepted quest may expose its reward selection.</summary>
    public void RequestReward(Player player, ObjectGuid guid, uint questId)
    {
        if (TryRewardQuest(player, guid, questId, out PlayerNpcState? state, out Quest? quest)
            && state.Quests.Get(questId) is { Rewarded: false, Status: QuestStatus.Complete } data
            && ObjectivesComplete(player, quest, data))
        {
            Send(player, WorldOpcode.SmsgQuestgiverOfferReward,
                QuestPackets.OfferReward(guid, quest, Options.RateDropMoney, RewardDisplayOf(player)));
        }
    }

    /// <summary>Validate current progress, NPC interaction, choice, money and the complete inventory batch without live mutations.</summary>
    public bool TryPrepareReward(Player player, ObjectGuid guid, uint questId, uint choice,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out QuestRewardPlan? plan)
    {
        plan = null;
        if (!TryRewardQuest(player, guid, questId, out PlayerNpcState? state, out Quest? quest)
            || state.Quests.Get(questId) is not { Rewarded: false, Status: QuestStatus.Complete } data
            || !ObjectivesComplete(player, quest, data) || !TryRewardGrants(quest, choice, out List<InventoryRewardGrant> grants,
                out uint chosenItem) || !TryRewardMoney(player, quest, out uint moneyAfter, out uint summaryMoney))
        {
            return false;
        }

        ItemInventoryResult result = player.Inventory.TryStageQuestRewards(grants, out InventoryRewardStage? stage, out uint failedEntry);
        if (result != ItemInventoryResult.Ok || stage is null)
        {
            player.Inventory.SendEquipError(result, null, null, entry: failedEntry);
            return false;
        }

        CharacterQuestStatus expected = RewardRow(player, questId, data);
        plan = new QuestRewardPlan(this, player, choice, moneyAfter, summaryMoney, expected,
            expected with { Rewarded = true, Timer = 0, RewardChoice = chosenItem }, stage);
        return true;
    }

    /// <summary>Publish a successfully committed plan. The caller must settle persistence while the world thread excludes intervening mutations.</summary>
    public void ApplyReward(QuestRewardPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Player player = plan.Player;
        if (!ReferenceEquals(plan.Services, this) || Ready(player) is not { } state
            || Quests.Get(plan.QuestId) is not { } quest || state.Quests.Get(plan.QuestId) is not { } data
            || RewardRow(player, plan.QuestId, data) != plan.ExpectedQuest || player.Money != plan.MoneyBefore
            || !plan.Stage.MatchesBefore())
        {
            throw new InvalidOperationException("the quest reward plan no longer matches its live character");
        }

        int slot = state.Quests.FindSlot(plan.QuestId);
        if (slot >= QuestConstants.MaxQuestLogSize)
        {
            throw new InvalidOperationException("the rewarded quest no longer occupies its journal slot");
        }

        player.Inventory.ApplyQuestRewardInventory(plan.Stage);
        player.Money = plan.MoneyAfter;
        data.Rewarded = true;
        data.TimerEndUnix = 0;
        data.RewardChoice = plan.RewardedQuest.RewardChoice;
        state.Quests.RemoveTimed(quest.Id);
        state.Quests.SetSlot(slot, 0);
        // The rewarded row was durably written by the transaction; it must never enter the
        // ordinary quest delta sink before that transaction. Other objectives may change below.
        player.Inventory.NotifyQuestRewardInventory(plan.Stage);
        MoneyChanged(state, player.Money);
        Flush(state);
        Send(player, WorldOpcode.SmsgQuestgiverQuestComplete,
            QuestPackets.Complete(quest, plan.SummaryMoney));
    }

    private bool TryRewardQuest(Player player, ObjectGuid guid, uint questId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PlayerNpcState? state,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Quest? quest)
    {
        state = Ready(player);
        quest = Quests.Get(questId);
        return state is not null && quest is not null && SupportedRewardQuest(quest)
            && player.Inventory.IsLoaded && player.Inventory.GuidAllocator is not null
            && state.Quests.FindSlot(questId) < QuestConstants.MaxQuestLogSize
            && InteractableNpc(player, guid, NpcFlags.QuestGiver) is { } npc
            && Quests.EndersOf(npc.Entry).Contains(questId);
    }

    private bool SupportedRewardQuest(Quest quest) => Options.OrdinaryRewardQuestIds.Contains(quest.Id)
        && quest.IsActive && quest.Template.Method == 2 && JournalOnlyQuest(quest)
        && !quest.HasSpecialFlag(QuestSpecialFlags.ExplorationOrEvent | QuestSpecialFlags.Timed)
        && !quest.HasFlag(QuestFlags.Exploration | QuestFlags.PartyAccept | QuestFlags.AutoRewarded)
        && quest.Template.RequiredMinRepFaction == 0 && quest.Template.RequiredMaxRepFaction == 0
        && quest.Template.RewXP == 0 && quest.Template.RewMoneyMaxLevel == 0
        && quest.Template.RewSpell == 0 && quest.Template.RewSpellCast == 0
        && quest.ReqCreatureOrGOCountTotal > 0 && quest.ReqItemCount.All(count => count == 0)
        && quest.ReqSourceCount.All(count => count == 0)
        && quest.ReqSpell.All(id => id == 0) && quest.ReqCreatureOrGOId.All(id => id >= 0)
        && quest.ReqCreatureOrGOId.Where((id, i) => id != 0 || quest.ReqCreatureOrGOCount[i] != 0)
            .All(id => id > 0)
        && quest.ReqCreatureOrGOId.Select((id, i) => id == 0 || quest.ReqCreatureOrGOCount[i] is > 0 and <= 63).All(valid => valid)
        && CoherentRewards(quest.RewItemId, quest.RewItemCount, dense: false)
        && CoherentRewards(quest.RewChoiceItemId, quest.RewChoiceItemCount, dense: true);

    private static bool CoherentRewards(IReadOnlyList<uint> ids, IReadOnlyList<uint> counts, bool dense)
    {
        bool ended = false;
        for (int i = 0; i < ids.Count; i++)
        {
            if ((ids[i] == 0) != (counts[i] == 0) || counts[i] > int.MaxValue || (dense && ended && ids[i] != 0))
            {
                return false;
            }

            ended |= ids[i] == 0;
        }

        return true;
    }

    private static bool TryRewardGrants(Quest quest, uint choice, out List<InventoryRewardGrant> grants, out uint chosenItem)
    {
        grants = [];
        chosenItem = 0;
        for (int i = 0; i < QuestConstants.RewardsCount; i++)
        {
            if (quest.RewItemId[i] != 0)
            {
                grants.Add(new InventoryRewardGrant(quest.RewItemId[i], quest.RewItemCount[i]));
            }
        }

        if (quest.RewChoiceItemsCount == 0)
        {
            if (choice != 0)
            {
                return false;
            }
        }
        else
        {
            if (choice >= quest.RewChoiceItemsCount)
            {
                return false;
            }

            chosenItem = quest.RewChoiceItemId[(int)choice];
            grants.Add(new InventoryRewardGrant(chosenItem, quest.RewChoiceItemCount[(int)choice]));
        }

        return true;
    }

    private bool TryRewardMoney(Player player, Quest quest, out uint moneyAfter, out uint summaryMoney)
    {
        moneyAfter = player.Money;
        summaryMoney = 0;
        long delta = quest.Template.RewOrReqMoney;
        if (delta > 0)
        {
            float scaled = quest.Template.RewOrReqMoney * Options.RateDropMoney;
            if (!float.IsFinite(scaled) || scaled < 0 || scaled > (double)int.MaxValue)
            {
                return false;
            }

            delta = (long)scaled;
        }

        if (player.Money > MaxMoneyAmount || (delta < 0 && player.Money < -delta))
        {
            return false;
        }

        moneyAfter = (uint)Math.Clamp(player.Money + delta, 0, MaxMoneyAmount);
        summaryMoney = unchecked((uint)delta);
        return true;
    }

    private static Func<uint, uint> RewardDisplayOf(Player player) => id => player.Inventory.Templates.Find(id)?.DisplayId ?? 0;

    private static CharacterQuestStatus RewardRow(Player player, uint questId, QuestStatusData data) => new((int)player.Guid.Low,
        questId, (byte)data.Status, data.Rewarded, data.Explored, data.TimerEndUnix,
        data.CreatureOrGOCount[0], data.CreatureOrGOCount[1], data.CreatureOrGOCount[2], data.CreatureOrGOCount[3],
        data.ItemCount[0], data.ItemCount[1], data.ItemCount[2], data.ItemCount[3], data.RewardChoice);
}
