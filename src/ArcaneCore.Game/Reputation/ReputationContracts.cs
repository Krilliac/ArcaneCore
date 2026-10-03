using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

/// <summary>One quest reputation reward slot (quest_template RewRepFaction1..5 / RewRepValue1..5).</summary>
public readonly record struct QuestReputationReward(uint FactionId, int Value);

/// <summary>
/// The quest-settlement seam (vmangos Player::RewardReputation(Quest const*)). The quest owner
/// calls it on the world thread after its reward has committed and published, passing the
/// quest's reputation slots. The reputation owner persists through its own ordered queue.
/// </summary>
public interface IQuestReputationRewards
{
    /// <param name="questLevel">quest_template.QuestLevel; zero or negative means the player's level (GetQuestLevelForPlayer).</param>
    void RewardQuest(Player player, int questLevel, IReadOnlyList<QuestReputationReward> rewards);
}

/// <summary>NPC reaction toward a player from faction templates plus reputation. World thread.</summary>
public interface INpcReactionSource
{
    /// <summary>The NPC's reaction toward the player; false when it cannot be resolved (fail closed).</summary>
    bool TryGetNpcReaction(Player player, FactionTemplateRecord npc, FactionTemplateRecord playerTemplate, out ReputationRank reaction);
}

/// <summary>Where reputation changes go (the world daemon's reputation write queue). World thread.</summary>
public interface IReputationSink
{
    void FactionsChanged(Player player, IReadOnlyList<CharacterReputationRow> rows);

    void WatchedFactionChanged(Player player, int watchedFaction);
}

/// <summary>
/// Reputation for other features (quests, vendors, trainers, combat, gossip conditions).
/// All members run on the world thread against players whose reputation has loaded; an
/// untracked player or an unknown faction reads as no reputation and cannot change.
/// </summary>
public interface IReputationService : IQuestReputationRewards, INpcReactionSource
{
    FactionCatalog Factions { get; }

    /// <summary>The player's loaded state, or null before login completes or without Faction.dbc data.</summary>
    PlayerReputation? For(Player player);

    /// <summary>ReputationMgr::GetReputation(factionId): base + standing; 0 when unknown.</summary>
    int GetReputation(Player player, uint factionId);

    /// <summary>Player::GetReputationRank; Neutral-equivalent 0 reputation when unknown.</summary>
    ReputationRank GetRank(Player player, uint factionId);

    bool IsAtWar(Player player, uint factionId);

    /// <summary>ReputationMgr::ModifyReputation: add <paramref name="delta"/> and tell the client.</summary>
    bool ModifyReputation(Player player, uint factionId, int delta);

    /// <summary>ReputationMgr::SetReputation: set the effective reputation and tell the client.</summary>
    bool SetReputation(Player player, uint factionId, int reputation);

    /// <summary>Player::RewardReputation(Unit*): creature_onkill_reputation for a direct kill.</summary>
    void RewardKill(Player player, Creature victim, float rate = 1f);

    /// <summary>The player's reaction toward an NPC template (PvC); false when unresolvable.</summary>
    bool TryGetPlayerReaction(Player player, FactionTemplateRecord npc, FactionTemplateRecord playerTemplate, out ReputationRank reaction);
}
