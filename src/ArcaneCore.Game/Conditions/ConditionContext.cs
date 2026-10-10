using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Conditions;

/// <summary>
/// Quest state the quest conditions read; implemented by the quest service, which owns every
/// player's quest log. A member returns null when the player has no loaded quest log yet (the
/// condition is then unknown and fails closed).
/// </summary>
public interface IConditionQuests
{
    /// <summary>cmangos Player::GetQuestRewardStatus (Player.cpp:13304-13318); false for unknown or repeatable quests.</summary>
    bool? IsRewarded(Player player, uint questId);

    /// <summary>cmangos Player::IsCurrentQuest(questId, mode) (Player.cpp:12191-12208): 0 incomplete or complete-not-rewarded, 1 incomplete, 2 complete-not-rewarded.</summary>
    bool? IsCurrent(Player player, uint questId, byte mode);

    /// <summary>cmangos Player::CanTakeQuest(quest, false) for an existing quest; false for an unknown quest.</summary>
    bool? CanTakeQuest(Player player, uint questId);
}

/// <summary>
/// The facts the condition types read, each optional: a condition type whose collaborator is missing
/// cannot be decided and fails closed (hidden option) rather than guessing. Every member is a plain
/// function over the world thread's state.
/// </summary>
public sealed record ConditionContext
{
    /// <summary>cmangos Player::HasItemCount: the item count (item, inBankAlso).</summary>
    public Func<Player, uint, bool, uint>? ItemCount { get; init; }

    /// <summary>Player::HasSpell.</summary>
    public Func<Player, uint, bool>? HasSpell { get; init; }

    /// <summary>Player::GetSkillValueBase; 0 when the player does not have the skill.</summary>
    public Func<Player, uint, uint>? SkillValueBase { get; init; }

    /// <summary>Whether the faction exists (sFactionStore.LookupEntry); absent means every faction exists.</summary>
    public Func<uint, bool>? FactionExists { get; init; }

    /// <summary>ReputationMgr::GetRank as a ReputationRank value (0 hated .. 7 exalted).</summary>
    public Func<Player, uint, byte>? ReputationRank { get; init; }

    /// <summary>WorldObject::GetZoneAndAreaId for (mapId, x, y, z).</summary>
    public Func<uint, float, float, float, (uint ZoneId, uint AreaId)>? ZoneAndArea { get; init; }

    /// <summary>AreaTable flags of an area id; null when the area does not exist.</summary>
    public Func<uint, uint?>? AreaFlags { get; init; }

    /// <summary>Unit::HasAura(spellId, effectIndex).</summary>
    public Func<Player, uint, byte, bool>? HasAura { get; init; }

    /// <summary>Whether one of the AD commission auras is active (Conditions.cpp:228-235).</summary>
    public Func<Player, bool>? HasAdCommissionAura { get; init; }

    /// <summary>GetHonorRankInfo().rank.</summary>
    public Func<Player, byte>? HonorRank { get; init; }

    /// <summary>sGameEventMgr.IsActiveEvent.</summary>
    public Func<uint, bool>? IsGameEventActive { get; init; }

    /// <summary>sGameEventMgr.IsActiveHoliday.</summary>
    public Func<uint, bool>? IsHolidayActive { get; init; }

    /// <summary>The quest state source (resolved on every evaluation: the quest service can be rebuilt).</summary>
    public Func<IConditionQuests?>? Quests { get; init; }

    /// <summary>cmangos CONDITION_INSTANCE_SCRIPT: null when the player has no instance script.</summary>
    public Func<Player, uint, bool?>? InstanceScript { get; init; }

    /// <summary>DungeonEncounter ids whose instance completion bits are set (Conditions.cpp:393-411).</summary>
    public Func<Player, uint, uint, bool?>? CompletedEncounter { get; init; }

    /// <summary>The source creature's last reached waypoint, or null when it cannot be resolved.</summary>
    public Func<Player, NpcInfo, uint?>? LastWaypoint { get; init; }

    /// <summary>Modes 1 and 2 of DEAD_OR_AWAY: all group or instance players dead or away.</summary>
    public Func<Player, NpcInfo?, uint, uint, bool?>? DeadOrAwayGroup { get; init; }

    /// <summary>Whether a live creature of the entry is within range of the player.</summary>
    public Func<Player, uint, uint, bool?>? CreatureInRange { get; init; }

    /// <summary>Number of currently spawned creatures of the entry in the player's map.</summary>
    public Func<Player, uint, uint?>? SpawnCount { get; init; }

    /// <summary>Global world script condition (war effort, invasion or transport state).</summary>
    public Func<uint, uint, bool?>? WorldScript { get; init; }

    /// <summary>Signed map variable for CONDITION_WORLDSTATE; absent variable is zero.</summary>
    public Func<Player, uint, int?>? WorldState { get; init; }

    /// <summary>The same variable when a spawn group evaluates its condition with a map but no player.</summary>
    public Func<Map, uint, int?>? MapWorldState { get; init; }
}
