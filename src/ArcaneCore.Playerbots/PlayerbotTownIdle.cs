using ArcaneCore.World.Playerbots.Population;

namespace ArcaneCore.World.Playerbots;

/// <summary>What an idle bot with a town home does (<see cref="PlayerbotTownIdle.Decide"/>).</summary>
internal enum TownIdleAction : byte
{
    /// <summary>Explore as any bot (look for creatures to hunt).</summary>
    Explore,

    /// <summary>Walk to the town centre (the innkeeper).</summary>
    WalkToTown,

    /// <summary>Stand at the town centre.</summary>
    Linger,
}

/// <summary>
/// The idle rule of a bot with a town home (<see cref="PopulationTownProgression"/>): a bot that has nothing to do goes back to the
/// town it stays in and lingers at its inn instead of wandering the wilderness. At the capital it always does (the stay is for errands
/// and rest); at the level-band town it lingers <see cref="LingerMs"/> and may explore <see cref="ExploreMs"/>, in turn, so a bot with no
/// quest left still finds creatures to hunt and none is stuck in town for the whole stay.
/// <para>Thread affinity: one instance per brain, the world thread.</para>
/// </summary>
internal sealed partial class PlayerbotTownIdle
{
    /// <summary>A bot this close to the town centre (yards; the innkeeper's spawn) is at the inn: inside the NPC interaction distance (5), so it can bind there.</summary>
    internal const float LingerYards = 4f;

    /// <summary>A hearthstone bind this near the town centre (yards) is the town's: nothing to bind.</summary>
    internal const float BindYards = 100f;

    /// <summary>
    /// Whether the bot's home bind should be moved to this town: the bind is on another map or farther than <see cref="BindYards"/> from the
    /// town centre. Every bind and hearth teleport of a bot sent a graduate back to its start valley (live: every bot bound in its start
    /// zone); a bot that stays in a town binds at its innkeeper (CMSG_BINDER_ACTIVATE, the server's real bind path).
    /// </summary>
    internal static bool NeedsBind(uint bindMap, float bindX, float bindY, uint townMap, float townX, float townY)
    {
        if (bindMap != townMap) return true;
        float dx = bindX - townX, dy = bindY - townY;
        return !float.IsFinite(dx) || !float.IsFinite(dy) || (dx * dx) + (dy * dy) > BindYards * BindYards;
    }

    /// <summary>
    /// A <see cref="TownIdleAction.Linger"/> this near the town centre (yards) is a bot standing at its inn, or at the spot it cannot get
    /// closer to (a counter, an upper floor): the brain holds it there and the stall watch does not count the wait (<see cref="IsHeld"/>).
    /// Farther out the linger is only a walk given up for <see cref="StallDeferMs"/> (<see cref="WalkStalled"/>), and the brain explores
    /// meanwhile as any bot does. Before, that bot stood in the open for the whole give-up and the stall watch reported it (live
    /// 2026-10-17, bot QA "stall|goal=Explore": every stall of phase explore, 1 390 of 3 396, was a town-homed bot, 724 of them over
    /// 150 yards from the town centre).
    /// </summary>
    internal const float HoldYards = 30f;

    /// <summary>Whether a lingering bot <paramref name="distance"/> yards from the town centre is held where it stands (<see cref="HoldYards"/>).</summary>
    internal static bool IsHeld(float distance) => distance <= HoldYards;

    internal const uint LingerMs = 120_000;

    internal const uint ExploreMs = 240_000;

    /// <summary>A walk to the inn that brings the bot no closer than a yard in this long (ms) is stalled: the spot is unreachable (a counter, an upper floor, a wall).</summary>
    internal const uint WalkStallMs = 20_000;

    /// <summary>A stalled walk is not tried again for this long (ms): the bot stays where it stands, as at the inn, so it never re-plans every think.</summary>
    internal const uint StallDeferMs = 180_000;

    private bool _exploring;

    /// <summary>Whether the last decision put a secondary town's bot in its explore window (it leaves the town for now).</summary>
    internal bool Exploring => _exploring;
    private uint _until;
    private TownIdleAction _last = TownIdleAction.Explore;

    /// <summary>
    /// A walk to the inn is one run of consecutive <see cref="TownIdleAction.WalkToTown"/> decisions, with no wall-clock limit between them
    /// (a think gap is often 10 to 16 seconds on a busy server, and thinks without plan budget skip the decision). The run ends when the
    /// brain says it did something else in between (a quest, combat, an errand, a goal change, an explore route: the brain calls this
    /// from the one place it learns of it, <c>PlayerbotBrain.Update</c> and <c>Explore</c>), or when the last decision was not a walk.
    /// The progress window then restarts: the walk is judged by the progress it makes from where it resumes.
    /// </summary>
    internal void Interrupted()
    {
        _last = TownIdleAction.Explore;
        _bestDistance = float.MaxValue;
    }

    /// <summary>
    /// The town centre this idle is measured from has changed (a festival spot became the centre, or the bot gave it up and the inn is again):
    /// the walk state belongs to one centre, so it is cleared. The best distance and a give-up of the old centre mean nothing against the
    /// new one, and a walk given up toward the old centre must not hold the bot back from the new one. The linger and explore window of a
    /// secondary town is a clock, not walk state, and is kept.
    /// </summary>
    internal void Recentre()
    {
        _last = TownIdleAction.Explore;
        _bestDistance = float.MaxValue;
        _deferred = false;
    }

    /// <summary>The bot has another home: the linger and explore window of the old one is dropped and the walk state with it.</summary>
    internal void Rehomed()
    {
        (_started, _exploring) = (false, false);
        Recentre();
    }

    private bool _started;
    private float _bestDistance = float.MaxValue;
    private uint _bestAt;
    private bool _deferred;
    private uint _deferUntil;

    /// <summary>The action for the bot's idle think, <paramref name="distance"/> yards from the town centre (it has arrived within <paramref name="arriveYards"/>).</summary>
    internal TownIdleAction Decide(TownStay stay, float distance, uint nowMs, float arriveYards = LingerYards)
    {
        TownIdleAction action = Choose(stay, distance, nowMs, arriveYards);
        _last = action;
        return action;
    }

    private TownIdleAction Choose(TownStay stay, float distance, uint nowMs, float arriveYards)
    {
        if (stay != TownStay.Secondary) _exploring = false; // an explore window belongs to a secondary town's stay only
        if (stay == TownStay.None) return TownIdleAction.Explore;
        if (stay == TownStay.Secondary) Advance(nowMs);
        else _started = false; // the capital: always town; the secondary's window starts afresh on return
        if (stay == TownStay.Secondary && _exploring) return TownIdleAction.Explore;
        if (distance <= arriveYards)
        {
            _bestDistance = float.MaxValue;
            return TownIdleAction.Linger;
        }

        // Not a continuation of a walk (the last decision was another action, or the brain reported an interruption): start afresh here.
        if (!_deferred && _last != TownIdleAction.WalkToTown) _bestDistance = float.MaxValue;
        return WalkStalled(distance, nowMs) ? TownIdleAction.Linger : TownIdleAction.WalkToTown;
    }

    /// <summary>
    /// Whether the walk to the inn is given up for now: it has not got a yard closer in <see cref="WalkStallMs"/>. The give-up lasts
    /// <see cref="StallDeferMs"/>, then the walk is tried afresh. Retries are bounded by those two clocks, not by the think rate.
    /// </summary>
    private bool WalkStalled(float distance, uint nowMs)
    {
        if (_deferred)
        {
            if (unchecked((int)(nowMs - _deferUntil)) < 0) return true;
            _deferred = false;
            _bestDistance = float.MaxValue;
        }

        if (distance < _bestDistance - 1f)
        {
            (_bestDistance, _bestAt) = (distance, nowMs);
            return false;
        }

        if (unchecked(nowMs - _bestAt) < WalkStallMs) return false;
        (_deferred, _deferUntil) = (true, unchecked(nowMs + StallDeferMs));
        return true;
    }

    private void Advance(uint nowMs)
    {
        if (!_started)
        {
            (_started, _exploring, _until) = (true, false, unchecked(nowMs + LingerMs));
            return;
        }

        if (unchecked((int)(nowMs - _until)) < 0) return;
        _exploring = !_exploring;
        _until = unchecked(nowMs + (_exploring ? ExploreMs : LingerMs));
    }
}
