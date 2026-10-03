using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Progression;

/// <summary>
/// The experience owner as seen by quest settlement: a side-effect-free preview lets the reward
/// transaction persist the resulting level before the live XP is published. World thread.
/// </summary>
public interface IQuestExperience : IPlayerExperience
{
    /// <summary>MaxPlayerLevel: quest XP turns into RewMoneyMaxLevel money from here on.</summary>
    byte MaxPlayerLevel { get; }

    /// <summary>The XP currently shown (PLAYER_XP).</summary>
    uint GetCurrentXp(Player player);

    /// <summary>The level and XP non-kill experience would produce, without side effects.</summary>
    (byte Level, uint Xp) Preview(byte level, uint currentXp, uint xp);
}

/// <summary>
/// Reward side effects that may only run after the settlement released the character: reward
/// spells (vmangos RewardQuest RewSpellCast/RewSpell) and the optional reputation owner hook.
/// World thread.
/// </summary>
public interface IQuestRewardEffects
{
    /// <summary>Whether this reward spell can be cast (unknown spells keep the quest unsupported).</summary>
    bool CanCastRewardSpell(uint spellId);

    /// <summary>The reward was durably committed and published; <paramref name="questGiver"/> may have despawned.</summary>
    void QuestRewarded(Player player, Quest quest, ObjectGuid questGiver);
}

/// <summary>
/// Optional reputation owner hook (feat/reputation): vmangos RewardQuest → RewardReputation.
/// This branch stores no reputation; implementations are discovered from DI when present.
/// </summary>
public interface IQuestReputationRewards
{
    void RewardQuestReputation(Player player, Quest quest);
}
