using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Npc;

/// <summary>Reputation objectives (vmangos Player::ReputationChanged, Player.cpp:14239-14264).</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>
    /// A faction's standing was set: every logged quest whose reputation objective names it is re-evaluated, so an Incomplete
    /// quest completes when the standing reaches RepObjectiveValue (and all other objectives are met, CanCompleteQuest) and a
    /// Complete one reverts to Incomplete when the standing falls below it.
    /// </summary>
    public void ReputationChanged(Player player, uint factionId)
    {
        if (factionId == 0 || Ready(player) is not { } state)
        {
            return;
        }

        foreach ((Quest quest, QuestStatusData data, int slot) in LoggedQuests(state))
        {
            if (quest.Template.RepObjectiveFaction == factionId)
            {
                RefreshCompletion(state, quest, data, slot);
            }
        }

        Flush(state);
    }
}
