using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Quest reputation rewards as part of the reward transaction (vmangos Player::RewardReputation(Quest const*)).
/// <see cref="TryStage"/> computes the whole result on a detached copy before anything is held, so the
/// reward store can persist the exact standing and flag rows with the quest journal; <see cref="Publish"/>
/// then replays the same frozen gains on the live player inside the settlement's publication step.
/// World thread.
/// </summary>
public interface IQuestReputationSettlement
{
    /// <summary>
    /// Freeze the gains (the dither roll is taken once) and apply them to a copy of the player's standings.
    /// Pairs whose faction or value is zero, and unknown or reputation-less factions, are skipped exactly as
    /// <see cref="IQuestReputationRewards.RewardQuest"/> skips them. False (nothing staged) when the player's
    /// standings are not loaded.
    /// </summary>
    /// <param name="questLevel">quest_template.QuestLevel; zero or negative means the player's level.</param>
    bool TryStage(Player player, int questLevel, IReadOnlyList<QuestReputationReward> rewards, out QuestReputationStage stage);

    /// <summary>
    /// Apply the staged gains to the live player and send the client updates, without queueing a write (the
    /// settlement transaction already persisted the rows). True when the live rows equal the staged rows; on a
    /// mismatch the live rows are queued so that storage converges to the live state, and false is returned.
    /// </summary>
    bool Publish(Player player, QuestReputationStage stage);
}

/// <summary>
/// The frozen result of <see cref="IQuestReputationSettlement.TryStage"/>: the faction rows the reward
/// transaction writes, and the gains the publication step replays.
/// </summary>
public sealed class QuestReputationStage
{
    internal QuestReputationStage(IReadOnlyList<(uint Faction, int Gain)> gains, IReadOnlyList<CharacterReputationRow> after)
    {
        Gains = gains;
        After = after;
    }

    /// <summary>No reputation reward.</summary>
    public static QuestReputationStage Empty { get; } = new([], []);

    /// <summary>The faction rows (standing relative to the base, and flags) after the reward.</summary>
    public IReadOnlyList<CharacterReputationRow> After { get; }

    internal IReadOnlyList<(uint Faction, int Gain)> Gains { get; }
}
