using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// The pure duel predicates the combat hooks, the damage path and the spell checks share. They read only
/// <see cref="Player.Duel"/> and who a unit acts for, so they work without a duel service. World thread.
/// </summary>
public static class DuelRules
{
    /// <summary>
    /// vmangos <c>Player::IsInDuelWith</c> (Player.h:2210): the duel with <paramref name="other"/> has started. It does NOT
    /// test <see cref="DuelInfo.Finished"/>, so it stays true for the rest of the tick that ended the duel; PvP pulses
    /// (Unit.cpp:5973, 6047) and <c>IsValidAttackTarget</c> (Object.cpp:3797-3800) use it.
    /// </summary>
    public static bool IsInDuelWith(Player player, Player other)
        => player.Duel is { } duel && ReferenceEquals(duel.Opponent, other) && duel.StartTimeSeconds != 0;

    /// <summary>
    /// vmangos <c>WorldObject::GetReactionTo</c> duel clause (Object.cpp:3650-3652): a started, unfinished duel makes the two
    /// players' units always hostile to each other, before any faction or group rule. A pet, totem or charmed unit counts
    /// through its controlling player (<see cref="ControllingPlayer"/>).
    /// </summary>
    public static bool IsOpponentHostile(Unit a, Unit b)
    {
        if (ControllingPlayer(a) is not { Duel: { } duel } owner || ControllingPlayer(b) is not { } other)
        {
            return false;
        }

        return !ReferenceEquals(owner, other) && ReferenceEquals(duel.Opponent, other) && duel.StartTimeSeconds != 0 && !duel.Finished;
    }

    /// <summary>
    /// The player a unit acts for (vmangos <c>Unit::GetAffectingPlayer</c>, Unit.cpp:4824-4833). A player with no charmer is
    /// itself; a pet, guardian, totem or charmed unit is the player behind <c>UNIT_FIELD_SUMMONEDBY</c> / <c>UNIT_FIELD_CHARMEDBY</c>.
    /// <see cref="IPlayerControlledUnit"/> is only the stand-in for a unit that does not publish those fields.
    /// </summary>
    public static Player? ControllingPlayer(Unit unit)
        => unit.GetAffectingPlayer() ?? (unit as IPlayerControlledUnit)?.ControllingPlayer;
}
