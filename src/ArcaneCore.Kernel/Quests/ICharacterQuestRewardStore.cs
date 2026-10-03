using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Kernel.Quests;

/// <summary>
/// One ordinary, nonrepeatable quest turn-in. Before and After carry complete inventories.
/// ExpectedQuest is the completed, unrewarded row; RewardedQuest preserves its counters,
/// remains complete, clears its timer and records the chosen item entry in RewardChoice.
/// The store detaches mutable snapshot lists before asynchronous work begins.
/// </summary>
public sealed record CharacterQuestRewardRequest(
    CharacterState Before,
    CharacterState After,
    CharacterQuestStatus ExpectedQuest,
    CharacterQuestStatus RewardedQuest);

public enum QuestRewardCommitResult
{
    Committed,
    AlreadyRewarded,
    Conflict,
    CharacterMissing,
}

/// <summary>
/// Atomically persists quest history, money and inventory for one nonrepeatable reward.
/// The caller drains character and quest saves and prevents older snapshots from being
/// queued across the commit. A successful commit is durable before live rewards are applied.
/// Database failures propagate after rollback; retrying observes the rewarded history.
/// </summary>
public interface ICharacterQuestRewardStore
{
    Task<QuestRewardCommitResult> CommitAsync(
        CharacterQuestRewardRequest request,
        CancellationToken cancellationToken = default);
}
