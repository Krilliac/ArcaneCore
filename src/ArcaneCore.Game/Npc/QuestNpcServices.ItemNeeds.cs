using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Npc;

/// <summary>Which quest items a player still wants (loot and quest-item visibility).</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>
    /// vmangos Quest::IsAllowedInRaid (QuestDef.cpp:227-235): a raid quest (type 62 or the RAID flag 0x40), or any quest
    /// while <c>Quests:IgnoreRaid</c> (vmangos Quests.IgnoreRaid, default off, mangosd.conf.dist.in:1168-1172) is set.
    /// </summary>
    public bool IsAllowedInRaid(Quest quest) => quest.Template.Type == QuestTypeRaid || quest.HasFlag(QuestFlags.Raid) || Options.IgnoreRaid;

    /// <summary>
    /// vmangos Player::HasQuestForItem (Player.cpp:14267-14320): an incomplete quest in the log still wants
    /// <paramref name="itemId"/>. A raid group member does not see quests that are not allowed in raids. A required item
    /// is wanted while the quest's own counter is below the requirement (the counter, not the bags). A source item
    /// (<c>ReqSource</c>) is wanted while the player (bank included) holds fewer than the item's max count (unique
    /// items), then fewer than <c>ReqSourceCount</c> when the quest sets one, else fewer than one stack.
    /// </summary>
    public bool HasQuestForItem(Player player, uint itemId, bool inRaidGroup)
    {
        if (StateOf(player) is not { Loaded: true } state)
        {
            return false;
        }

        foreach ((Quest quest, QuestStatusData data, _) in LoggedQuests(state))
        {
            if (data.Status != QuestStatus.Incomplete || (inRaidGroup && !IsAllowedInRaid(quest)))
            {
                continue;
            }

            // "There should be no mixed ReqItem/ReqSource drop": the required items first.
            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                if (itemId == quest.ReqItemId[i] && data.ItemCount[i] < quest.ReqItemCount[i])
                {
                    return true;
                }
            }

            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                if (quest.ReqSourceId[i] != itemId || player.Inventory.Templates.Find(itemId) is not { } proto)
                {
                    continue;
                }

                uint owned = InventoryCount(player, itemId, inBankAlso: true);
                if (proto.MaxCount != 0 && owned < proto.MaxCount)
                {
                    return true;
                }

                if (quest.ReqSourceCount[i] != 0 ? owned < quest.ReqSourceCount[i] : owned < proto.MaxStackSize())
                {
                    return true;
                }
            }
        }

        return false;
    }
}
