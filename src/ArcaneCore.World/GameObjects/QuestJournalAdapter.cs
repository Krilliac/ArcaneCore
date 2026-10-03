using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// <see cref="ILootQuestJournal"/> over the quest feature's journals (vmangos
/// Player::HasQuestForItem, GetQuestStatus, ItemAddedQuestCheck, CastedCreatureOrGO). Without
/// the quest feature every answer is "no" and events are dropped. Money and item changes also
/// queue a character save. World thread.
/// </summary>
internal sealed class QuestJournalAdapter(IServiceProvider services, WorldRuntime world) : ILootQuestJournal
{
    private QuestNpcServices? Quests => services.GetService<QuestNpcFeature>()?.Services;

    public bool NeedsQuestItem(Player player, uint itemId)
    {
        if (Quests is not { } quests || quests.StateOf(player) is not { Loaded: true } state)
        {
            return false;
        }

        foreach ((uint questId, QuestStatusData data) in state.Quests.Statuses)
        {
            if (data.Status != QuestStatus.Incomplete || quests.Quests.Get(questId) is not { } quest)
            {
                continue;
            }

            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                if (quest.ReqItemId[i] == itemId && quest.ReqItemCount[i] > player.Inventory.GetItemCount(itemId, inBankAlso: true))
                {
                    return true;
                }

                if (quest.ReqSourceId[i] == itemId && quest.ReqSourceCount[i] > player.Inventory.GetItemCount(itemId, inBankAlso: true))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool IsQuestIncomplete(Player player, uint questId)
        => Quests?.StateOf(player) is { Loaded: true } state && state.Quests.GetStatus(questId) == QuestStatus.Incomplete;

    public void ItemLooted(Player player, uint itemId, uint count)
    {
        // The inventory's ItemCountChanged is not routed to quests yet; loot reports its own
        // additions (vmangos StoreNewItem → ItemAddedQuestCheck).
        Quests?.ItemAdded(player, itemId, count);
        world.SavePlayer(player);
    }

    public void MoneyLooted(Player player)
    {
        Quests?.MoneyChanged(player);
        world.SavePlayer(player);
    }

    public void GameObjectUsed(Player player, uint entry, ObjectGuid guid)
        => Quests?.CastedCreatureOrGo(player, entry, guid, isCreature: false, spellId: 0);
}
