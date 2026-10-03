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

    /// <summary>
    /// vmangos Player::CanGiveQuestSourceItemIfNeed (Player.cpp:13643-13676): what the player already owns
    /// (bank included) counts towards SrcItemCount, only the missing part must fit, and a refusal tells the
    /// client why: QUEST_FAILED inventory-full / duplicate-item, otherwise the item's equip error.
    /// </summary>
    private bool CanReceiveSourceItem(Player player, Quest quest)
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

        uint owned = player.Inventory.GetItemCount(entry, inBankAlso: true);
        uint wanted = SourceItemCount(quest);
        if (owned >= wanted)
        {
            return true;
        }

        ItemInventoryResult result = player.Inventory.CanStoreNewItem(entry, wanted - owned, [], out _);
        switch (result)
        {
            case ItemInventoryResult.Ok:
                return true;
            case ItemInventoryResult.InventoryFull:
                Send(player, WorldOpcode.SmsgQuestgiverQuestFailed, QuestPackets.QuestFailed(quest.Id, QuestInvalidReason.InventoryFull));
                return false;
            case ItemInventoryResult.CantCarryMoreOfThis:
                Send(player, WorldOpcode.SmsgQuestgiverQuestFailed, QuestPackets.QuestFailed(quest.Id, QuestInvalidReason.DuplicateItem));
                return false;
            default:
                player.Inventory.SendEquipError(result, null, null, entry: entry);
                return false;
        }
    }

    /// <summary>vmangos GiveQuestSourceItemIfNeed: the part of SrcItemCount the player does not already own (CanAddQuest proved the space).</summary>
    private static void GiveSourceItem(Player player, Quest quest)
    {
        uint entry = quest.Template.SrcItemId;
        if (entry == 0)
        {
            return;
        }

        uint owned = player.Inventory.GetItemCount(entry, inBankAlso: true);
        uint wanted = SourceItemCount(quest);
        if (owned >= wanted)
        {
            return;
        }

        ItemInventoryResult result = player.Inventory.AddItem(entry, wanted - owned, out _,
            received: true, created: false, showInChat: true);
        if (result != ItemInventoryResult.Ok)
        {
            player.Inventory.SendEquipError(result, null, null, entry: entry);
        }
    }

    /// <summary>
    /// vmangos Player::TakeOrReplaceQuestStartItems(quest, msg: true, giveQuestStartItem: true) as RemoveQuestAtSlot
    /// calls it (Player.cpp:13696-13759): false (and the client told why) when the source item cannot be taken
    /// off; a source item that is the quest's own start item stays; otherwise SrcItemCount is destroyed when
    /// owned (bank included) and replaced by the quest's starting item, if the quest has one.
    /// </summary>
    private bool TakeOrReplaceQuestStartItems(Player player, Quest quest)
    {
        uint entry = quest.Template.SrcItemId;
        if (entry == 0 || !player.Inventory.IsLoaded || player.Inventory.Templates.Find(entry) is not { } item)
        {
            return true;
        }

        uint count = SourceItemCount(quest);
        if (quest.Id == item.StartQuest)
        {
            return true;   // Quest-starting item and item given at quest start identical: leave it.
        }

        ItemInventoryResult unequip = player.Inventory.CanUnequipItems(entry, count);
        if (unequip != ItemInventoryResult.Ok)
        {
            player.Inventory.SendEquipError(unequip, null, null, entry: entry);
            return false;
        }

        // Additional check to prevent possible unlimited gold.
        if (player.Inventory.GetItemCount(entry, inBankAlso: true) < count)
        {
            return true;
        }

        player.Inventory.DestroyItemCount(entry, count, includeBank: true);
        uint replacement = player.Inventory.Templates.QuestStartingItem(quest.Id);
        if (replacement != 0)
        {
            player.Inventory.AddItem(replacement, count, out _);
        }

        return true;
    }

    /// <summary>
    /// vmangos RemoveQuestAtSlot, abandon since 1.12.1 (Player.cpp:13046-13062): quest-bound required items
    /// (BIND_QUEST_ITEM, BIND_QUEST_ITEM1) are destroyed, bank included.
    /// </summary>
    private static void DestroyQuestBoundRequiredItems(Player player, Quest quest)
    {
        if (!player.Inventory.IsLoaded)
        {
            return;
        }

        foreach (uint entry in quest.ReqItemId)
        {
            if (entry != 0 && player.Inventory.Templates.Find(entry) is { } item
                && (item.Bonding == (uint)ItemBonding.QuestItem || item.Bonding == QuestItem1Bonding))
            {
                uint owned = player.Inventory.GetItemCount(entry, inBankAlso: true);
                if (owned > 0)
                {
                    player.Inventory.DestroyItemCount(entry, owned, includeBank: true);
                }
            }
        }
    }

    /// <summary>BIND_QUEST_ITEM1 (not used in game, vmangos ItemPrototype.h:56).</summary>
    private const uint QuestItem1Bonding = 5;

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
