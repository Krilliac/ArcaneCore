using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>Quest chains: the next quest offered when a quest is turned in.</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>
    /// After a turn-in at <paramref name="giver"/>: when the finished quest names a NextQuestInChain that this
    /// same giver starts (creature quest relations; vmangos Player::GetNextQuest, Player.cpp:12514-12541),
    /// its details window opens (QuestHandler.cpp:275-277, SendQuestGiverQuestDetails with ActivateAccept).
    /// vmangos does not check CanTakeQuest here: accepting is where a refusal happens. True when sent.
    /// </summary>
    public bool OfferNextQuest(Player player, ObjectGuid giver, Quest completed)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(completed);
        uint nextId = completed.NextQuestInChain;
        if (nextId == 0 || FindNpc(player, giver) is not { } npc || !StartersOf(npc).Contains(nextId)
            || Quests.Get(nextId) is not { } next)
        {
            return false;
        }

        Send(player, WorldOpcode.SmsgQuestgiverQuestDetails, QuestPackets.Details(giver, next, Options.RateDropMoney, RewardDisplayOf(player)));
        return true;
    }
}
