using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// How a duel ended (vmangos Player.h:582-587 <c>DuelCompleteType</c>). The numeric values are the
/// vmangos ones; <see cref="Interrupted"/> never announces a winner.
/// </summary>
public enum DuelCompleteType : byte
{
    /// <summary>Cancelled before the start, a player left or died to a third party (DUEL_INTERRUPTED).</summary>
    Interrupted = 0,

    /// <summary>One side lost at 1 hp or forfeited with /forfeit (DUEL_WON).</summary>
    Won = 1,

    /// <summary>One side left the area, the map or the flag object vanished (DUEL_FLED).</summary>
    Fled = 2,
}

/// <summary>
/// One player's half of a duel (vmangos Player.h:235-244 <c>DuelInfo</c>). vmangos creates two instances per
/// challenge (SpellEffects.cpp:4739-4757): the challenger's has <c>opponent = target</c>, the target's has
/// <c>opponent = challenger</c>; both name the challenger as <see cref="Initiator"/>. The transport guid vmangos
/// keeps is not modelled (no transport system on this base; see docs/areas/duels.md). All times are whole Unix
/// seconds (vmangos <c>time_t</c>); 0 means "unset". World thread only.
/// </summary>
public sealed class DuelInfo(Player initiator, Player opponent)
{
    public Player Initiator { get; } = initiator ?? throw new ArgumentNullException(nameof(initiator));

    public Player Opponent { get; } = opponent ?? throw new ArgumentNullException(nameof(opponent));

    /// <summary>vmangos <c>startTimer</c>: when the opponent accepted; 0 until then and again once the duel started.</summary>
    public long StartTimerSeconds { get; internal set; }

    /// <summary>vmangos <c>startTime</c>: when the flag turned on (accept + 3 s); 0 while the duel is only requested or counting down.</summary>
    public long StartTimeSeconds { get; internal set; }

    /// <summary>vmangos <c>outOfBound</c>: when this player first left the duel area; 0 while inside.</summary>
    public long OutOfBoundSeconds { get; internal set; }

    /// <summary>vmangos <c>finished</c>: the duel is over; the object is dropped on the owner's next update (Player.cpp:1132-1137).</summary>
    public bool Finished { get; internal set; }
}
