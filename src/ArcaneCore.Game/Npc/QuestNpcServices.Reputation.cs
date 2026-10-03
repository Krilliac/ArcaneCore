using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Npc;

public sealed partial class QuestNpcServices
{
    /// <summary>
    /// vmangos Player::ReputationChanged (Player.cpp:14239-14264): a logged quest whose reputation objective names
    /// <paramref name="factionId"/> completes when the standing reaches RepObjectiveValue (CanCompleteQuest, which also
    /// re-checks every other objective) and reverts to incomplete when it falls below it. Quests that are not in the
    /// log, are failed, or name another faction are untouched.
    /// </summary>
    public void ReputationChanged(Player player, uint factionId)
    {
        if (factionId == 0 || Ready(player) is not { } state)
        {
            return;
        }

        foreach ((Quest quest, QuestStatusData data, int slot) in LoggedQuests(state))
        {
            if (quest.Template.RepObjectiveFaction == factionId && data.Status is QuestStatus.Incomplete or QuestStatus.Complete)
            {
                RefreshCompletion(state, quest, data, slot);
            }
        }

        Flush(state);
    }
}
