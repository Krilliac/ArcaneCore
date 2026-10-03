using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// A unit that acts for a player: a pet, guardian, totem or charmed unit (vmangos <c>Unit::GetAffectingPlayer</c>,
/// Unit.cpp:4824-4833, and the <c>GetOwnerGuid</c> clauses in the duel rules, Unit.cpp:762-779). The pets and charm areas
/// implement it on their unit class; nothing on this base does, so the duel clauses that read it are inert until then.
/// A <see cref="Player"/> is its own controlling player and does not need to implement it.
/// </summary>
public interface IPlayerControlledUnit
{
    /// <summary>The player this unit acts for, or null when it currently has none.</summary>
    Player? ControllingPlayer { get; }
}
