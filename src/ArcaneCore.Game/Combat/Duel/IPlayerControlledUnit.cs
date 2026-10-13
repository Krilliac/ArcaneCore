using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// A stand-in for a unit that acts for a player but does not publish <c>UNIT_FIELD_SUMMONEDBY</c> or <c>UNIT_FIELD_CHARMEDBY</c>.
/// Production pets, guardians, totems and charmed units are read from those fields (<see cref="DuelRules.ControllingPlayer"/>,
/// vmangos <c>Unit::GetAffectingPlayer</c>). A <see cref="Player"/> is its own controlling player and does not need to implement it.
/// </summary>
public interface IPlayerControlledUnit
{
    /// <summary>The player this unit acts for, or null when it currently has none.</summary>
    Player? ControllingPlayer { get; }
}
