using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>
/// Drops the gray quests of a freshly boosted character. A boost jumps levels, so the log a character started with (the starting
/// zone's quests) is suddenly far below it and pays almost nothing; a managed playerbot would still follow those quests across the
/// map. A quest is gray when its level is at or below <see cref="ExperienceFormulas.GrayLevel"/> of the new level (the same table
/// the experience rules use, mangos-classic <c>MaNGOS::XP::GetGrayLevel</c>, Formulas.h), and only an unfinished one (incomplete or
/// failed) is dropped: a completed quest is kept so its reward can still be turned in. A quest of level 0 stands for the player's own
/// level (vmangos Player::GetQuestLevelForPlayer, Player.h:1114) and is never gray. Abandoning goes through
/// <see cref="QuestNpcServices.AbandonQuest"/>, the path of CMSG_QUESTLOG_REMOVE_QUEST, so the source items are taken back and the
/// change is saved; a refused abandon leaves the quest in the log and is not counted. Runs only from <c>.character boost</c>; a level-up
/// by play never reaches it. World thread only.
/// </summary>
internal static class BoostQuestPruner
{
    /// <summary>Whether a quest of <paramref name="questLevel"/> is gray for a character of <paramref name="playerLevel"/>.</summary>
    public static bool IsGray(uint playerLevel, int questLevel)
        => questLevel > 0 && (uint)questLevel <= ExperienceFormulas.GrayLevel(playerLevel);

    /// <summary>Abandons every unfinished gray quest of <paramref name="player"/> at <paramref name="level"/>; returns how many left the log.</summary>
    public static int DropGrayQuests(Player player, QuestNpcServices quests, byte level)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(quests);
        if (quests.StateOf(player)?.Quests is not { } log)
        {
            return 0;
        }

        int dropped = 0;
        for (byte slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            uint id = log.SlotQuestId(slot);
            if (id == 0 || quests.Quests.Get(id) is not { } quest || !IsGray(level, quest.QuestLevel))
            {
                continue;
            }

            if (log.GetStatus(id) is QuestStatus.Incomplete or QuestStatus.Failed && quests.AbandonQuest(player, slot))
            {
                dropped++;
            }
        }

        return dropped;
    }
}
