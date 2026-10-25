using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.Combat;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The brain's answer to a player who attacks the bot: the bot fights back with the PvP helper (<see cref="PlayerbotPvpFight"/>) under the one
/// hostility rule (<see cref="PlayerbotHostility"/>). It never starts on a player by itself: only a player in its attackers provokes it, and it
/// keeps that fight until the player is dead, gone or out of <see cref="PvpPursuitYards"/>. Creature fights stay in <c>_target</c>.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>A player the bot fights back is pursued from this far at most; past it the bot lets go.</summary>
    internal const float PvpPursuitYards = 45f;

    private Func<Vector3, uint, bool, bool>? _pvpWalk;
    private Action? _pvpDropRoute;

    /// <summary>
    /// A player attacking the bot (or the one it already fights back, still hostile and within <see cref="PvpPursuitYards"/>) owns this think: the
    /// route and creature intent are dropped once when the opponent changes, the goal is Combat, and the bot fights. False when no player fights it.
    /// </summary>
    private bool TryFightPlayer(Player player, uint interval)
    {
        // A player the walk found no path to is set aside for PlayerbotPvpFight.UnreachableMs (a caster on a cliff: the bot would plan a chase it cannot walk at every think).
        Player? opponent = PlayerbotHostility.FirstProvokingPlayer(player, _pvpFight);
        if (opponent is null && _pvpFight is { Target: Player current } && PlayerbotHostility.IsHostilePlayer(player, current)
            && !_pvpFight.IsSkipped(current)
            && Vector3.Distance(PlayerbotMotion.CurrentPosition(player), PlayerbotMotion.CurrentPosition(current)) <= PvpPursuitYards)
            opponent = current;

        if (opponent is null)
        {
            if (_pvpFight is { Target: not null } ended)
            {
                ended.Stop(player);
                AbandonRoute();
            }

            return false;
        }

        // A crowd at a festival spot that draws a player's attack disperses: the town's festival is set aside for the team for a while.
        _eventGoals?.Attacked();
        FightUnit(player, opponent, interval);
        return true;
    }

    /// <summary>
    /// One think of the fight with <paramref name="target"/> through <see cref="PlayerbotPvpFight"/>: the route and creature intent are dropped once
    /// when the opponent changes, the goal is Combat.
    /// </summary>
    private void FightUnit(Player player, Unit target, uint interval)
    {
        if (!ReferenceEquals(Pvp.Target, target))
        {
            DropIntent();
            AbandonRoute();
        }

        _goal = PlayerbotGoalKind.Combat;
        TargetEntry = target is Creature creature ? creature.Entry : 0;
        _pvpWalk ??= PvpWalk;
        _pvpDropRoute ??= AbandonRoute;
        Pvp.Fight(player, target, interval, _pvpWalk, _pvpDropRoute);
    }

    /// <summary>The walk of <see cref="PlayerbotPvpFight"/>: a chase route planned as the brain plans its creature chase. False when the way is refused.</summary>
    private bool PvpWalk(Vector3 goal, uint interval, bool chase)
    {
        if (_session.Player is not { } player) return false;
        if (_route is null || ChaseRouteStale(player, _route, goal))
        {
            PlanOutcome planned = PlayerbotNavigation.PlanExact(player, goal, _options, ref _tileWait, _session.World.NowMs,
                out PlayerbotRoute? fresh, chase: chase);
            if (planned == PlanOutcome.Pending)
            {
                if (_route is { Complete: false } inHand && !PlayerbotNavigation.TryAdvance(_session, inHand, _options, interval, _session.World.NowMs))
                    AbandonRoute();
                return true;
            }

            if (planned == PlanOutcome.Refused)
            {
                AbandonRoute();
                PlayerbotMovementControl.Stop(_session, player);
                return false;
            }

            if (_route is not null && !ReferenceEquals(_route, fresh)) AbandonRoute();
            if (fresh is not null)
            {
                fresh.Chase = chase;
                _chaseGoal = goal;
                _chaseTruncated = false;
            }

            _route = fresh;
        }

        if (_route is null || !PlayerbotNavigation.TryAdvance(_session, _route, _options, interval, _session.World.NowMs)) AbandonRoute();
        return true;
    }
}
