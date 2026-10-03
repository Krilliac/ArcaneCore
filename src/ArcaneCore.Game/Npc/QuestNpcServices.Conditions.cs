using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Npc;

/// <summary>The quest-log facts the conditions table reads (cmangos Conditions.cpp:220-227, 295-298, 309-313).</summary>
public sealed partial class QuestNpcServices : IConditionQuests
{
    /// <summary>cmangos Player::GetQuestRewardStatus (Player.cpp:13304-13318): unknown quests are false.</summary>
    public bool? IsRewarded(Player player, uint questId)
    {
        if (LoadedState(player) is not { } state)
        {
            return null;
        }

        return Quests.Get(questId) is { } quest && state.Quests.RewardStatus(quest);
    }

    /// <summary>cmangos Player::IsCurrentQuest(questId, mode) (Player.cpp:12191-12208).</summary>
    public bool? IsCurrent(Player player, uint questId, byte mode)
    {
        if (LoadedState(player) is not { } state)
        {
            return null;
        }

        if (state.Quests.Get(questId) is not { } data)
        {
            return false;
        }

        return mode switch
        {
            1 => data.Status == QuestStatus.Incomplete,
            2 => data.Status == QuestStatus.Complete && !data.Rewarded,
            _ => data.Status == QuestStatus.Incomplete || (data.Status == QuestStatus.Complete && !data.Rewarded),
        };
    }

    /// <summary>cmangos Player::CanTakeQuest(quest, false): an unknown quest cannot be taken.</summary>
    public bool? CanTakeQuest(Player player, uint questId)
    {
        if (LoadedState(player) is not { } state)
        {
            return null;
        }

        return Quests.Get(questId) is { } quest && CanTakeQuest(state, quest, []);
    }

    private PlayerNpcState? LoadedState(Player player)
        => StateOf(player) is { Loaded: true } state && ReferenceEquals(state.Quests.Player, player) ? state : null;
}
