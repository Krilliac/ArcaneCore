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
    /// Whether turning in <paramref name="quest"/> with <paramref name="rewardChoiceIndex"/> has bag room for its reward items
    /// after required delivery items are removed.
    /// </summary>
    public static bool CanFitQuestRewards(Player player, Quest quest, uint rewardChoiceIndex = 0, QuestNpcServices? services = null, IReadOnlySet<ObjectGuid>? offered = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(quest);
        if (!player.Inventory.IsLoaded)
        {
            return true;
        }

        if (!TryRewardGrants(quest, rewardChoiceIndex, out List<ArcaneCore.Game.Items.InventoryRewardGrant> grants, out _))
        {
            return false;
        }

        if (services is not null)
        {
            if (!services.TryRewardSpell(player, ObjectGuid.Empty, quest, out var spellGrant))
            {
                return false;
            }

            if (spellGrant.CreatedItems.Count > 0)
            {
                grants.AddRange(spellGrant.CreatedItems.Select(item => new ArcaneCore.Game.Items.InventoryRewardGrant(item.Entry, item.Count, Created: true)));
            }
        }

        if (grants.Count == 0)
        {
            return true;
        }

        ArcaneCore.Game.Items.InventoryRewardGrant[] removals = RequiredItemRemovals(quest)
            .Select(r => new ArcaneCore.Game.Items.InventoryRewardGrant(r.Entry, Math.Min(r.Count, player.Inventory.GetItemCount(r.Entry, exclude: offered is { Count: > 0 } kept ? item => kept.Contains(item.Guid) : null))))
            .Where(r => r.Count > 0)
            .ToArray();

        ArcaneCore.Game.Items.ItemGuidAllocator? previousAllocator = player.Inventory.GuidAllocator;
        player.Inventory.GuidAllocator = new ArcaneCore.Game.Items.ItemGuidAllocator(1_000_000);
        try
        {
            ItemInventoryResult result = player.Inventory.TryStageQuestRewards(grants, removals, out _, out _, keepForRemovals: offered);
            return result != ItemInventoryResult.InventoryFull;
        }
        finally
        {
            player.Inventory.GuidAllocator = previousAllocator;
        }
    }

    /// <summary>
    /// Diagnose why accepting <paramref name="quest"/> was refused for <paramref name="player"/>, or <c>null</c> when every accept gate passes.
    /// </summary>
    public string? DiagnoseAcceptRefusal(Player player, Quest quest)
    {
        if (StateOf(player) is not { } state)
        {
            return null;
        }

        QuestTemplate t = quest.Template;
        if (t.SrcItemId != 0
            && player.Inventory.IsLoaded
            && player.Inventory.GetItemCount(t.SrcItemId, inBankAlso: true) < Math.Max((uint)1, t.SrcItemCount)
            && player.Inventory.CanStoreNewItem(t.SrcItemId, Math.Max((uint)1, t.SrcItemCount), [], out _) != ItemInventoryResult.Ok)
        {
            return "InventoryFull";
        }

        if (state.Quests.FindSlot(0) == QuestConstants.MaxQuestLogSize)
        {
            return "QuestLogFull";
        }

        if (state.Quests.Get(quest.Id)?.Rewarded == true && !quest.IsRepeatable)
        {
            return "AlreadyDone";
        }

        if (state.Quests.GetStatus(quest.Id) != QuestStatus.None)
        {
            return "AlreadyOn";
        }

        if (t.MaxLevel != 0 && t.MaxLevel < player.Level)
        {
            return "MaxLevel";
        }

        if (player.Level < t.MinLevel)
        {
            return "LowLevel";
        }

        if (t.RequiredClasses != 0 && (t.RequiredClasses & Mask((byte)player.Class)) == 0)
        {
            return "WrongClass";
        }

        if (t.RequiredRaces != 0 && (t.RequiredRaces & Mask((byte)player.Race)) == 0)
        {
            return "WrongRace";
        }

        if ((quest.PrevQuests.Count > 0 && !AnyPreviousQuestSatisfied(state, quest.PrevQuests))
            || (t.PrevQuestId > 0 && state.Quests.Get((uint)t.PrevQuestId)?.Rewarded != true)
            || (t.PrevQuestId < 0 && !state.Quests.IsCurrent((uint)-t.PrevQuestId)))
        {
            return "PrevQuest";
        }

        if (t.ExclusiveGroup > 0 && ExclusiveSiblingStarted(state, quest))
        {
            return "ExclusiveGroup";
        }

        if (t.RequiredSkill != 0 && (Deps.Spells is not { } skills || skills.GetSkillValue(player, t.RequiredSkill) < t.RequiredSkillValue))
        {
            return "RequiredSkill";
        }

        if (t.RequiredCondition != 0 && !(Deps.Conditions?.IsSatisfied(t.RequiredCondition, player, null) ?? false))
        {
            return "RequiredCondition";
        }

        if (RefuseTakeQuest(state, quest, null) is { } refusal)
        {
            return refusal.Message?.ToString() ?? "CannotTakeQuest";
        }

        if (!AcceptableQuest(quest))
        {
            return "UnsupportedQuest";
        }

        return null;
    }
    /// <summary>
    /// The first half of vmangos <c>CanRewardQuest(quest, msg)</c>: an autocomplete quest must pass CanTakeQuest without
    /// its status check (prevents packet-editing exploits, Player.cpp:12684-12698; a game master bypasses it, 12688-12692, without the sysmessage); any other quest must be accepted and
    /// complete. In both cases a quest already rewarded cannot be rewarded again (12701-12703). Sends nothing.
    /// </summary>
    private bool RewardBase(PlayerNpcState state, Quest quest)
    {
        if (quest.IsAutoComplete)
        {
            if (RefuseTakeQuest(state, quest, null, skipStatusCheck: true) is not null && !state.Quests.Player.IsGameMaster)
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
    private bool RewardRequirements(Player player, Quest quest, bool msg, IReadOnlySet<ObjectGuid>? offered = null)
    {
        if (quest.HasSpecialFlag(QuestSpecialFlags.Deliver))
        {
            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                uint count = player.Inventory.IsLoaded
                    ? player.Inventory.GetItemCount(quest.ReqItemId[i], exclude: offered is { Count: > 0 } kept ? item => kept.Contains(item.Guid) : null)
                    : InventoryCount(player, quest.ReqItemId[i]);
                if (quest.ReqItemCount[i] != 0 && count < quest.ReqItemCount[i])
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
    private bool CanRewardQuest(PlayerNpcState state, Quest quest, bool msg, IReadOnlySet<ObjectGuid>? offered = null)
        => RewardBase(state, quest) && RewardRequirements(state.Quests.Player, quest, msg, offered);

    /// <summary>
    /// Whether <paramref name="player"/> currently satisfies the non-mutating turn-in requirements (<see cref="RewardBase"/> and
    /// <see cref="RewardRequirements"/>) for <paramref name="quest"/> without sending error packets.
    /// </summary>
    public bool MeetsTurnInRequirements(Player player, Quest quest, IReadOnlySet<ObjectGuid>? offered = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(quest);
        return StateOf(player) is { Loaded: true } state && CanRewardQuest(state, quest, msg: false, offered);
    }

    /// <summary>
    /// vmangos <c>CanCompleteRepeatableQuest</c> (Player.cpp:12663-12680): a repeatable quest may be completed from the
    /// ender's list without being in the log: takeable, its delivery items in the bags, and rewardable.
    /// </summary>
    private bool CanCompleteRepeatableQuest(PlayerNpcState state, Quest quest, IReadOnlySet<ObjectGuid>? offered = null)
    {
        if (!CanTakeQuest(state, quest, null))
        {
            return false;
        }

        if (quest.HasSpecialFlag(QuestSpecialFlags.Deliver))
        {
            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                if (quest.ReqItemId[i] != 0 && quest.ReqItemCount[i] != 0
                    && (state.Quests.Player.Inventory.IsLoaded ? state.Quests.Player.Inventory.GetItemCount(quest.ReqItemId[i], exclude: offered is { Count: > 0 } kept ? item => kept.Contains(item.Guid) : null) : InventoryCount(state.Quests.Player, quest.ReqItemId[i])) < quest.ReqItemCount[i])
                {
                    return false;
                }
            }
        }

        return CanRewardQuest(state, quest, msg: false, offered);
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
    /// A chosen reward that was not stored (its settlement ran out of budget, the store refused it, or no settlement slot was
    /// free): nothing was granted and the quest is still ready to turn in. The client is told the turn-in failed
    /// (SMSG_QUESTGIVER_QUEST_FAILED with vmangos' default reason, INVALIDREASON_DONT_HAVE_REQ = 0, QuestDef.h) and its quest
    /// window is closed (SMSG_GOSSIP_COMPLETE, as PlayerMenu::CloseGossip), so the player can talk to the giver again and retry.
    /// vmangos rewards synchronously and has no such case; without a reply the client would keep waiting on its offer window.
    /// </summary>
    public void RefuseUnsettledReward(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        Send(player, WorldOpcode.SmsgQuestgiverQuestFailed, QuestPackets.QuestFailed(questId, QuestInvalidReason.DontHaveReq));
        CloseGossip(player);
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

    /// <summary>
    /// Explains why a turn-in of <paramref name="questId"/> at <paramref name="guid"/> cannot complete right now, or returns null when every
    /// turn-in and reward-staging rule passes. Used by playerbots to log the exact refusal and set the quest aside instead of stalling at the ender.
    /// </summary>
    public string? DiagnoseTurnInRefusal(Player player, ObjectGuid guid, uint questId, uint choice = 0, IReadOnlySet<ObjectGuid>? offered = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Ready(player) is not { } state) return "journal not loaded";
        if (Quests.Get(questId) is not { } quest) return "quest template missing";
        if (!SupportedRewardQuest(quest)) return "quest reward adapter unsupported";
        if (!player.Inventory.IsLoaded || player.Inventory.GuidAllocator is null) return "inventory not loaded";
        if (!quest.IsAutoComplete && state.Quests.FindSlot(questId) >= QuestConstants.MaxQuestLogSize) return "quest not in journal slot";
        if (InteractableNpc(player, guid, NpcFlags.QuestGiver, out string? interactRefusal) is not { } npc)
            return interactRefusal ?? "ender not interactable";
        if (!EndersOf(npc).Contains(questId)) return "NPC does not end this quest";
        if (state.Quests.RewardStatus(quest)) return "quest already rewarded";
        if (quest.IsAutoComplete)
        {
            if (RefuseTakeQuest(state, quest, null, skipStatusCheck: true) is { } takeRefusal && !player.IsGameMaster)
                return $"autocomplete requirement not met ({takeRefusal})";
        }
        else if (state.Quests.Get(questId) is not { Status: QuestStatus.Complete } data)
        {
            return "quest status is not Complete";
        }
        else if (!Pending(quest, data))
        {
            return "quest timer expired";
        }
        else if (!ObjectivesComplete(player, quest, data))
        {
            return "quest objectives incomplete";
        }

        if (quest.HasSpecialFlag(QuestSpecialFlags.Deliver))
        {
            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                uint count = player.Inventory.IsLoaded
                    ? player.Inventory.GetItemCount(quest.ReqItemId[i], exclude: offered is { Count: > 0 } kept ? item => kept.Contains(item.Guid) : null)
                    : InventoryCount(player, quest.ReqItemId[i]);
                if (quest.ReqItemCount[i] != 0 && count < quest.ReqItemCount[i])
                    return $"missing required delivery item {quest.ReqItemId[i]} ({count}/{quest.ReqItemCount[i]})";
            }
        }

        if (quest.Template.RewOrReqMoney < 0 && player.Money < (uint)-(long)quest.Template.RewOrReqMoney)
            return "not enough money";
        if (!TryRewardGrants(quest, choice, out List<ArcaneCore.Game.Items.InventoryRewardGrant> grants, out _))
            return "invalid reward item or choice";
        if (!TryRewardMoney(player, quest, out _, out _))
            return "money cap exceeded";
        if (!TryRewardExperience(player, quest, out _, out _))
            return "experience reward unavailable";
        if (!TryRewardSpell(player, guid, quest, out var spellGrant))
            return "reward spell preflight refused";
        if (!TryRewardReputation(player, quest, out _))
            return "reputation reward preflight refused";

        grants.AddRange(spellGrant.CreatedItems.Select(item => new ArcaneCore.Game.Items.InventoryRewardGrant(item.Entry, item.Count, Created: true)));
        ItemInventoryResult result = player.Inventory.TryStageQuestRewards(grants, RequiredItemRemovals(quest),
            out ArcaneCore.Game.Items.InventoryRewardStage? stage, out uint failedEntry, offered);
        if (result != ItemInventoryResult.Ok || stage is null)
        {
            return result switch
            {
                ItemInventoryResult.InventoryFull => "inventory full",
                ItemInventoryResult.CantCarryMoreOfThis => $"duplicate unique reward item {failedEntry}",
                _ => $"reward item {failedEntry} refused ({result})",
            };
        }

        return null;
    }
}
