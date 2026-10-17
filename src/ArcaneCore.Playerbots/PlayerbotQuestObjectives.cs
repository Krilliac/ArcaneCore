using ArcaneCore.Game.Quests;

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
}
