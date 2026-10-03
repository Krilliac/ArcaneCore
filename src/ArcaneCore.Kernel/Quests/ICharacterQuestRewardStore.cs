using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Kernel.Quests;

/// <summary>
/// One ordinary quest turn-in. Before and After carry complete inventories.
/// ExpectedQuest is the completed, unrewarded row; RewardedQuest preserves its counters,
/// remains complete, clears its timer and records the chosen item entry in RewardChoice.
/// <see cref="LearnedSpells"/> are spells the reward teaches (inserted into the character's
/// spell rows when missing) and <see cref="ReputationAfter"/> are the complete faction rows the
/// reward leaves behind (inserted or overwritten, last writer wins like the reputation queue);
/// both default to none and commit in the same transaction as everything else.
/// <see cref="InsertIfMissing"/> is the autocomplete turn-in (vmangos QUEST_METHOD_AUTOCOMPLETE, Player.cpp:12682-12700):
/// such a quest is rewarded without ever having been in the journal, so ExpectedQuest then is the virtual complete
/// row (status COMPLETE, no counters, the rewarded history the live quest data carries) and the store inserts the
/// rewarded row when no durable row exists, or claims the present row (status NONE or COMPLETE) when it equals that
/// virtual row apart from its status. A durable row already rewarded refuses a nonrepeatable quest.
/// The store detaches mutable snapshot lists before asynchronous work begins.
/// </summary>
public sealed record CharacterQuestRewardRequest(
    CharacterState Before,
    CharacterState After,
    CharacterQuestStatus ExpectedQuest,
    CharacterQuestStatus RewardedQuest,
    IReadOnlyList<uint>? LearnedSpells = null,
    IReadOnlyList<CharacterReputationRow>? ReputationAfter = null,
    bool InsertIfMissing = false);

public enum QuestRewardCommitResult
{
    Committed,
    AlreadyRewarded,
    Conflict,
    CharacterMissing,
}

/// <summary>
/// Atomically persists quest history, money, inventory, learned spells and faction standings
/// for one nonrepeatable reward.
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
