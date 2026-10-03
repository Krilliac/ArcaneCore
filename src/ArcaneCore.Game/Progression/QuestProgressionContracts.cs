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
/// The reward spell of an ordinary quest (vmangos RewardQuest RewSpellCast/RewSpell) as seen by
/// quest settlement. Permanent grants (LearnSpell, CreateItem) are persisted atomically with the
/// journal; transient effects are cast once after the commit. World thread.
/// </summary>
public interface IQuestRewardEffects
{
    /// <summary>
    /// The static gate: whether this reward spell has a shape settlement can honour (unknown spells and
    /// everything that is neither a pure grant nor a transient, preflightable spell keep the quest unsupported).
    /// </summary>
    bool CanCastRewardSpell(uint spellId);

    /// <summary>
    /// The player-aware gate, before anything is held or mutated: resolves the grants, freezes created-item
    /// counts and preflights teleport destinations and summon owners. False keeps the quest unrewarded.
    /// </summary>
    bool TryPrepareRewardSpell(Player player, ObjectGuid questGiver, uint spellId, out QuestRewardSpellGrant grant);

    /// <summary>SMSG_LEARNED_SPELL for the committed learned spells, inside the settlement's publication step.</summary>
    void AnnounceLearnedSpells(Player player, QuestRewardSpellGrant grant);

    /// <summary>
    /// After the character was released: passive self-casts of the learned spells and the transient reward cast.
    /// The transient effect was never durable; a failure now is logged and lost. <paramref name="questGiver"/> may have despawned.
    /// </summary>
    void PublishRewardSpell(Player player, Quest quest, ObjectGuid questGiver, QuestRewardSpellGrant grant);
}
