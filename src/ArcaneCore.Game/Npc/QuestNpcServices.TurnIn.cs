using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using ItemInventoryResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// The retail turn-in rules, reimplemented from vmangos Objects/Player.cpp (CanCompleteQuest 12590-12661,
/// CanCompleteRepeatableQuest 12663-12680, CanRewardQuest 12682-12739 and 12741-12818) and
/// Handlers/QuestHandler.cpp (HandleQuestgiverChooseRewardOpcode 239-280, HandleQuestgiverRequestRewardOpcode 283-312,
/// HandleQuestgiverCompleteQuest 383-397) plus GossipDef.cpp SendQuestGiverRequestItems (484-497).
/// An autocomplete quest (Method 0) is turned in without a journal entry and without its objectives: the rules only need
/// <c>CanTakeQuest(skipStatusCheck)</c>, the required items in the bags and the required money.
/// </summary>
public sealed partial class QuestNpcServices
{
    /// <summary>
    /// The first half of vmangos <c>CanRewardQuest(quest, msg)</c>: an autocomplete quest must pass CanTakeQuest without
    /// its status check (prevents packet-editing exploits, Player.cpp:12684-12698); any other quest must be accepted and
    /// complete. In both cases a quest already rewarded cannot be rewarded again (12701-12703). Sends nothing.
    /// </summary>
    private bool RewardBase(PlayerNpcState state, Quest quest)
    {
        if (quest.IsAutoComplete)
        {
            if (RefuseTakeQuest(state, quest, [], skipStatusCheck: true) is not null)
            {
                return false;
            }
        }
        else if (state.Quests.Get(quest.Id) is not { Status: QuestStatus.Complete } data || !Pending(quest, data)
            || !ObjectivesComplete(state.Quests.Player, quest, data))
        {
            return false;
        }

        return !state.Quests.RewardStatus(quest);
    }

    /// <summary>
    /// The second half of <c>CanRewardQuest</c> (Player.cpp:12705-12726): the required items must be in the bags, not the
    /// bank (a miss is an EQUIP_ERR_ITEM_NOT_FOUND naming the item when <paramref name="msg"/>), and a negative
    /// RewOrReqMoney must be payable (QUESTGIVER_QUEST_INVALID reason 22 regardless of <paramref name="msg"/>, line 12722-12726).
    /// </summary>
    private bool RewardRequirements(Player player, Quest quest, bool msg)
    {
        if (quest.HasSpecialFlag(QuestSpecialFlags.Deliver))
        {
            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                if (quest.ReqItemCount[i] != 0 && InventoryCount(player, quest.ReqItemId[i]) < quest.ReqItemCount[i])
                {
                    if (msg)
                    {
                        player.Inventory.SendEquipError(ItemInventoryResult.ItemNotFound, null, null, entry: quest.ReqItemId[i]);
                    }

                    return false;
                }
            }
        }

        if (quest.Template.RewOrReqMoney < 0 && player.Money < (uint)-(long)quest.Template.RewOrReqMoney)
        {
            Send(player, WorldOpcode.SmsgQuestgiverQuestInvalid, QuestPackets.QuestInvalid(QuestInvalidReason.NotEnoughMoney));
            return false;
        }

        return true;
    }

    /// <summary>vmangos <c>CanRewardQuest(quest, msg)</c>.</summary>
    private bool CanRewardQuest(PlayerNpcState state, Quest quest, bool msg)
        => RewardBase(state, quest) && RewardRequirements(state.Quests.Player, quest, msg);

    /// <summary>
    /// vmangos <c>CanCompleteRepeatableQuest</c> (Player.cpp:12663-12680): a repeatable quest may be completed from the
    /// ender's list without being in the log: takeable, its delivery items in the bags, and rewardable.
    /// </summary>
    private bool CanCompleteRepeatableQuest(PlayerNpcState state, Quest quest)
    {
        if (!CanTakeQuest(state, quest, []))
        {
            return false;
        }

        if (quest.HasSpecialFlag(QuestSpecialFlags.Deliver))
        {
            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                if (quest.ReqItemId[i] != 0 && quest.ReqItemCount[i] != 0
                    && InventoryCount(state.Quests.Player, quest.ReqItemId[i]) < quest.ReqItemCount[i])
                {
                    return false;
                }
            }
        }

        return CanRewardQuest(state, quest, msg: false);
    }

    /// <summary>
    /// The row the reward transaction expects for an autocomplete quest: the live row (a quest that was accepted) or a
    /// blank one, always read as COMPLETE, because the store inserts or claims it (see <c>InsertIfMissing</c>).
    /// </summary>
    private static CharacterQuestStatus VirtualRow(Player player, uint questId, QuestStatusData? data)
        => data is null
            ? new CharacterQuestStatus((int)player.Guid.Low, questId, (byte)QuestStatus.Complete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            : RewardRow(player, questId, data) with { Status = (byte)QuestStatus.Complete };

    /// <summary>
    /// vmangos PlayerMenu::SendQuestGiverRequestItems (GossipDef.cpp:484-497): the request-items window is skipped for
    /// the offer-reward window when the quest has no request text, or has no items and is complete.
    /// </summary>
    private void SendRequestItems(Player player, ObjectGuid guid, Quest quest, bool complete, bool closeOnCancel)
    {
        if (quest.RequestItemsText.Length == 0 || (quest.ReqItemsCount == 0 && complete))
        {
            Send(player, WorldOpcode.SmsgQuestgiverOfferReward,
                QuestPackets.OfferReward(guid, quest, Options.RateDropMoney, RewardDisplayOf(player)));
        }
        else
        {
            Send(player, WorldOpcode.SmsgQuestgiverRequestItems,
                QuestPackets.RequestItems(guid, quest, complete, RewardDisplayOf(player), closeOnCancel));
        }
    }

    /// <summary>
    /// A refused reward (Player.cpp:12741-12818, QuestHandler.cpp:262-271): the client has just been told why (items,
    /// money, bag space) and the offer window is sent again so the player can retry after making room.
    /// </summary>
    private void ResendOfferReward(Player player, ObjectGuid guid, Quest quest)
        => Send(player, WorldOpcode.SmsgQuestgiverOfferReward,
            QuestPackets.OfferReward(guid, quest, Options.RateDropMoney, RewardDisplayOf(player)));

    /// <summary>
    /// The refusal of a staged inventory (Player.cpp:12755-12774): bag space is a QUESTGIVER_QUEST_FAILED reason 4, a
    /// unique-item clash reason 17, anything else the ordinary equip error naming the item.
    /// </summary>
    private void SendRewardStageRefusal(Player player, uint questId, ItemInventoryResult result, uint failedEntry)
    {
        switch (result)
        {
            case ItemInventoryResult.InventoryFull:
                Send(player, WorldOpcode.SmsgQuestgiverQuestFailed, QuestPackets.QuestFailed(questId, QuestInvalidReason.InventoryFull));
                break;
            case ItemInventoryResult.CantCarryMoreOfThis:
                Send(player, WorldOpcode.SmsgQuestgiverQuestFailed, QuestPackets.QuestFailed(questId, QuestInvalidReason.DuplicateItem));
                break;
            default:
                player.Inventory.SendEquipError(result, null, null, entry: failedEntry);
                break;
        }
    }
}
