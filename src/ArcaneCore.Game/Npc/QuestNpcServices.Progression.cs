using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;
using ItemInventoryResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Objective adapters for the broader ordinary quest vertical, reimplemented from vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0 Objects/Player.cpp CanAddQuest, AddQuest
/// (GiveQuestSourceItemIfNeed, AdjustQuestReqItemCount), TakeQuestSourceItem,
/// RewardPlayerAndGroupAtCast/CastedCreatureOrGO, KilledMonsterCredit and
/// Handlers/MiscHandler.cpp HandleAreaTriggerOpcode (quest relation part).
/// </summary>
public sealed partial class QuestNpcServices
{
    /// <summary>vmangos QUEST_TYPE_RAID: the only quest type a raid group member may receive kill credit for.</summary>
    public const uint QuestTypeRaid = 62;

    /// <summary>
    /// vmangos Player::KilledMonsterCredit with the raid-group rule: while in a raid group,
    /// only <see cref="QuestTypeRaid"/> quests take creature kill credit.
    /// </summary>
    public void KilledMonsterCredit(Player player, uint entry, ObjectGuid guid, bool inRaidGroup)
    {
        if (Ready(player) is { } state)
        {
            CreditCreature(state, entry, guid, true, 0, false, inRaidGroup, originalCaster: true);
            Flush(state);
        }
    }

    /// <summary>
    /// vmangos Player::CastedCreatureOrGO(entry, guid, spell, original_caster): a group member
    /// that did not cast only progresses <see cref="QuestFlags.Sharable"/> quests.
    /// </summary>
    public void CastedCreatureOrGo(Player player, uint entry, ObjectGuid guid, bool isCreature, uint spellId, bool originalCaster)
    {
        if (Ready(player) is { } state)
        {
            CreditCreature(state, entry, guid, isCreature, spellId, false, inRaidGroup: false, originalCaster);
            Flush(state);
        }
    }

    /// <summary>
    /// The quest relation part of vmangos HandleAreaTriggerOpcode
    /// (areatrigger_involvedrelation): every related quest the player is on becomes explored.
    /// Returns the number of related quests that were updated.
    /// </summary>
    public int AreaTriggerReached(Player player, uint triggerId)
    {
        if (Ready(player) is not { } state || triggerId == 0 || !player.IsAlive)
        {
            return 0;
        }

        int updated = 0;
        foreach (QuestAreaTrigger relation in Options.AreaTriggerQuests)
        {
            if (relation.TriggerId == triggerId && state.Quests.Get(relation.QuestId) is { Status: QuestStatus.Incomplete }
                && Quests.Get(relation.QuestId) is { } quest && AcceptableQuest(quest))
            {
                AreaExploredOrEventHappens(player, relation.QuestId);
                updated++;
            }
        }

        return updated;
    }

    /// <summary>The area triggers configured for <paramref name="questId"/>.</summary>
    public bool HasAreaTrigger(uint questId) => Options.AreaTriggerQuests.Any(r => r.QuestId == questId && r.TriggerId != 0);

    /// <summary>
    /// Login reconciliation of delivery counters with the loaded inventory (vmangos AddQuest's
    /// AdjustQuestReqItemCount). Inventory and quest rows are saved by separate queues; the
    /// inventory is authoritative for "items in bags", so a relog can never keep a stale count.
    /// </summary>
    public void ReconcileItemCounts(Player player)
    {
        if (Ready(player) is not { } state || !player.Inventory.IsLoaded)
        {
            return;
        }

        foreach ((Quest quest, QuestStatusData data, int slot) in LoggedQuests(state))
        {
            if (Pending(quest, data) && quest.HasSpecialFlag(QuestSpecialFlags.Deliver))
            {
                AdjustRequiredItemCounts(state, quest, data);
                RefreshCompletion(state, quest, data, slot);
            }
        }

        Flush(state);
    }

    /// <summary>An accepted journal entry not yet turned in (a repeatable may carry an older reward).</summary>
    private static bool Pending(Quest quest, QuestStatusData data)
        => data.Status is QuestStatus.Incomplete or QuestStatus.Complete && (!data.Rewarded || quest.IsRepeatable);

    /// <summary>Items in the player's bags (not the bank); <see cref="IItemService"/> wins when present.</summary>
    private uint InventoryCount(Player player, uint entry, bool inBankAlso = false)
        => Deps.Items?.GetItemCount(player, entry, inBankAlso)
            ?? (player.Inventory.IsLoaded ? player.Inventory.GetItemCount(entry, inBankAlso) : 0);

    private static uint SourceItemCount(Quest quest) => quest.Template.SrcItemCount == 0 ? 1u : quest.Template.SrcItemCount;

    /// <summary>vmangos CanAddQuest: the source item must fit, otherwise the client receives the equip error.</summary>
    private static bool CanReceiveSourceItem(Player player, Quest quest)
    {
        uint entry = quest.Template.SrcItemId;
        if (entry == 0)
        {
            return true;
        }

        if (!player.Inventory.IsLoaded || player.Inventory.GuidAllocator is null)
        {
            return false;
        }

        ItemInventoryResult result = player.Inventory.CanStoreNewItem(entry, SourceItemCount(quest), [], out _);
        if (result != ItemInventoryResult.Ok)
        {
            player.Inventory.SendEquipError(result, null, null, entry: entry);
            return false;
        }

        return true;
    }

    /// <summary>vmangos GiveQuestSourceItemIfNeed (CanAddQuest already proved the space).</summary>
    private static void GiveSourceItem(Player player, Quest quest)
    {
        if (quest.Template.SrcItemId != 0)
        {
            ItemInventoryResult result = player.Inventory.AddItem(quest.Template.SrcItemId, SourceItemCount(quest), out _,
                received: true, created: false, showInChat: true);
            if (result != ItemInventoryResult.Ok)
            {
                player.Inventory.SendEquipError(result, null, null, entry: quest.Template.SrcItemId);
            }
        }
    }

    /// <summary>
    /// vmangos TakeQuestSourceItem on abandon: destroy the source item (bank included) unless
    /// the quest also requires it, in which case the player keeps what they gathered.
    /// </summary>
    private static void TakeSourceItem(Player player, Quest quest)
    {
        uint entry = quest.Template.SrcItemId;
        if (entry != 0 && player.Inventory.IsLoaded && !quest.ReqItemId.Contains(entry))
        {
            player.Inventory.DestroyItemCount(entry, SourceItemCount(quest), includeBank: true);
        }
    }

    /// <summary>vmangos AdjustQuestReqItemCount: delivery counters start from the items already owned (bank included).</summary>
    private void AdjustRequiredItemCounts(PlayerNpcState state, Quest quest, QuestStatusData data)
    {
        if (!quest.HasSpecialFlag(QuestSpecialFlags.Deliver))
        {
            return;
        }

        Player player = state.Quests.Player;
        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            if (quest.ReqItemId[i] == 0 || quest.ReqItemCount[i] == 0)
            {
                continue;
            }

            uint value = Math.Min(InventoryCount(player, quest.ReqItemId[i], inBankAlso: true), quest.ReqItemCount[i]);
            if (value != data.ItemCount[i])
            {
                data.ItemCount[i] = value;
                state.Quests.MarkChanged(quest.Id);
            }
        }
    }
}

/// <summary>One areatrigger_involvedrelation row (configuration "Quests:AreaTriggerQuests").</summary>
public sealed class QuestAreaTrigger
{
    public uint TriggerId { get; set; }

    public uint QuestId { get; set; }
}
