using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Npc;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// What a quest in a bot's log still asks for (creature kills or casts, game objects, items), read from the quest and the bot's progress alone.
/// Used when a creature entry is set aside as unreachable (<see cref="PlayerbotWorldDestinations"/>): the quest follows it only when no other
/// open objective of the quest can still be served. Pure; no allocation but the delegates' own.
/// </summary>
internal static class PlayerbotQuestObjectives
{
    /// <summary>
    /// Whether <paramref name="entry"/> serves an open objective of the quest: it is a creature objective not yet done, or it drops an item
    /// the bot still lacks. <paramref name="droppers"/> gives the creatures that drop an item (null: unknown).
    /// </summary>
    internal static bool Serves(Quest quest, QuestStatusData status, uint entry, Func<uint, uint> itemsHeld, Func<uint, IReadOnlyList<uint>?> droppers)
    {
        for (int index = 0; index < quest.ReqCreatureOrGOId.Count && index < status.CreatureOrGOCount.Length; index++)
        {
            int objective = quest.ReqCreatureOrGOId[index];
            if (objective > 0 && (uint)objective == entry && status.CreatureOrGOCount[index] < quest.ReqCreatureOrGOCount[index]) return true;
        }

        for (int index = 0; index < quest.ReqItemId.Count; index++)
        {
            uint item = quest.ReqItemId[index], needed = quest.ReqItemCount[index];
            if (item == 0 || needed == 0 || itemsHeld(item) >= needed) continue;
            if (droppers(item) is { } list && list.Contains(entry)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the quest has an open objective that <paramref name="skipped"/> does not serve alone and that can still be served: a creature
    /// objective of another entry that is not set aside (<paramref name="entrySuspended"/>), any game object objective, or an item the bot
    /// lacks that has no known source, or one dropper other than <paramref name="skipped"/> that is not set aside.
    /// </summary>
    internal static bool HasOtherOpenObjective(Quest quest, QuestStatusData status, uint skipped, Func<uint, bool> entrySuspended,
        Func<uint, uint> itemsHeld, Func<uint, IReadOnlyList<uint>?> droppers)
    {
        for (int index = 0; index < quest.ReqCreatureOrGOId.Count && index < status.CreatureOrGOCount.Length; index++)
        {
            int objective = quest.ReqCreatureOrGOId[index];
            if (objective == 0 || status.CreatureOrGOCount[index] >= quest.ReqCreatureOrGOCount[index]) continue;
            if (objective < 0) return true; // a game object: not judged here, still open
            if ((uint)objective != skipped && !entrySuspended((uint)objective)) return true;
        }

        for (int index = 0; index < quest.ReqItemId.Count; index++)
        {
            uint item = quest.ReqItemId[index], needed = quest.ReqItemCount[index];
            if (item == 0 || needed == 0 || itemsHeld(item) >= needed) continue;
            if (droppers(item) is not { Count: > 0 } list) return true; // no known creature source (a game object, a vendor): still open
            foreach (uint dropper in list)
                if (dropper != skipped && !entrySuspended(dropper)) return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when the bot's inventory can store at least one unit of <paramref name="itemEntry"/>
    /// (including stacking onto an existing partial stack when bags have no empty slots).
    /// </summary>
    internal static bool CanStoreObjectiveItem(Player player, uint itemEntry)
    {
        if (itemEntry == 0 || !player.Inventory.IsLoaded)
            return true;

        return player.Inventory.CanStoreNewItem(itemEntry, 1, [], out _) == ArcaneCore.Game.Items.InventoryResult.Ok;
    }

    /// <summary>
    /// Sums all kill/interaction counters and required quest item counts for a single quest status so
    /// item-use objectives detect progress regardless of which counter or item the spell/script credits.
    /// </summary>
    internal static uint GetQuestTotalProgress(Player player, Quest quest, QuestStatusData status)
    {
        uint total = 0;
        for (int i = 0; i < quest.ReqCreatureOrGOId.Count && i < status.CreatureOrGOCount.Length; i++)
        {
            if (quest.ReqCreatureOrGOCount[i] > 0)
                total += status.CreatureOrGOCount[i];
        }

        for (int i = 0; i < quest.ReqItemId.Count; i++)
        {
            uint item = quest.ReqItemId[i];
            if (item != 0 && quest.ReqItemCount[i] > 0)
                total += player.Inventory.GetItemCount(item);
        }

        return total;
    }

    /// <summary>
    /// Sums progress across all incomplete quest objectives (kill credits and dropped quest items)
    /// that <paramref name="creatureEntry"/> can advance, so multi-dropper or shared-creature
    /// objectives reset contention timers when any associated objective progresses.
    /// </summary>
    internal static uint GetTotalProgressForCreature(
        Player player,
        QuestNpcServices services,
        PlayerNpcState state,
        LootContent? loot,
        uint creatureEntry)
    {
        if (creatureEntry == 0)
            return 0;

        uint total = 0;
        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses)
        {
            if (status.Status != QuestStatus.Incomplete
                || services.Quests.Get(questId) is not { IsActive: true } quest)
                continue;

            for (int i = 0; i < quest.ReqCreatureOrGOId.Count && i < status.CreatureOrGOCount.Length; i++)
            {
                int req = quest.ReqCreatureOrGOId[i];
                if (req > 0 && (uint)req == creatureEntry && quest.ReqCreatureOrGOCount[i] > 0)
                    total += status.CreatureOrGOCount[i];
            }

            if (loot is null)
                continue;

            for (int i = 0; i < quest.ReqItemId.Count; i++)
            {
                uint item = quest.ReqItemId[i];
                if (item == 0 || quest.ReqItemCount[i] == 0)
                    continue;

                IReadOnlyList<uint> droppers = PlayerbotDropIndex.DroppersOf(loot, item);
                for (int d = 0; d < droppers.Count; d++)
                {
                    if (droppers[d] == creatureEntry)
                    {
                        total += player.Inventory.GetItemCount(item);
                        break;
                    }
                }
            }
        }

        return total;
    }
}
