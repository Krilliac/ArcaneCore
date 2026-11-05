using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.Zones;
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
        Player? provoker = PlayerbotHostility.FirstProvokingPlayer(player, _pvpFight);
        Player? opponent = provoker;
        Player? engaged = EngagedOpponent(player);
        opponent ??= engaged;
        // A fight the bot started (help, grudge, initiate) is not chased into an exempt area unless the target keeps attacking the bot from there (self-defense); a player who attacks the bot is fought back as ever.
        bool anyOpponent = opponent is not null;
        if (opponent is not null && StartedAndExempt(player, opponent)) opponent = null;
        if (opponent is null && _pvpFight is { Target: Player current } && PlayerbotHostility.IsHostilePlayer(player, current)
            && !_pvpFight.IsSkipped(current) && !StartedAndExempt(player, current)
            && Vector3.Distance(PlayerbotMotion.CurrentPosition(player), PlayerbotMotion.CurrentPosition(current)) <= PvpPursuitYards)
            opponent = current;

        if (opponent is null)
        {
            if (!anyOpponent) _startedOn = null; // nobody fights the bot any more: the next attacker is a fresh one
            if (_pvpFight is { Target: not null } ended)
            {
                ended.Stop(player);
                AbandonRoute();
            }

            return false;
        }

        // A crowd at a festival spot that draws a player's attack disperses: the town's festival is set aside for the team for a while.
        _eventGoals?.Attacked();
        // Only a player that really attacks the bot makes it call for help (not a target it chose itself): the call names that provoker.
        if (provoker is not null) TryReportDefense(player, provoker, out _);
        FightUnit(player, opponent, interval);
        return true;
    }

    /// <summary>The call board of defense calls (<see cref="ManagedPlayerbotFeature.DefenseCalls"/>); null without one (a brain built by a test or a feature that has none).</summary>
    internal PlayerbotDefenseBoard? DefenseCalls { get; set; }

    /// <summary>A bot reports at most once per this many milliseconds (design: war-mode-defense, section 1.2).</summary>
    internal const int DefenseReportIntervalMs = 3000;

    private uint _defenseReportAtMs;
    private bool _defenseReported;

    /// <summary>
    /// Why a bot that is attacked does not report to the call board, from facts only (design section 1.2): defense is off
    /// (<c>Pvp:Defense:Enabled</c>), the map is not open world, the bot or its provoker stands where War Mode protects (<see cref="PvpAreaState.IsWarModeProtected"/>),
    /// or the bot is in a duel or holds a defend post or a stay (those have rules of their own).
    /// </summary>
    internal static DefenseReportRefusal DefenseRefusal(bool enabled, bool openWorld, bool selfProtected, bool provokerProtected, bool inDuel, bool onPostOrStay)
        => !enabled ? DefenseReportRefusal.Off
            : !openWorld ? DefenseReportRefusal.NotOpenWorld
            : selfProtected || provokerProtected ? DefenseReportRefusal.WarModeProtected
            : inDuel || onPostOrStay ? DefenseReportRefusal.Busy
            : DefenseReportRefusal.None;

    /// <summary>
    /// Report to the call board that <paramref name="provoker"/> attacks the bot: its position, zone, area, team and level, once per
    /// <see cref="DefenseReportIntervalMs"/> and only when <see cref="DefenseRefusal"/> finds no reason against it. World thread (a think). The report
    /// carries no world object (the board keeps guids and positions), and the zone and area come from the terrain already in memory, never a file read.
    /// </summary>
    /// <returns><see cref="DefenseReportRefusal.None"/> when the board was asked (<paramref name="report"/> is its answer, which may be a refusal of its own).</returns>
    internal DefenseReportRefusal TryReportDefense(Player player, Player provoker, out DefenseReport report)
    {
        report = default;
        if (DefenseCalls is not { } board) return DefenseReportRefusal.Off;
        var map = player.Map;
        DefenseReportRefusal refusal = DefenseRefusal(_options.Pvp.Defense.Enabled, map is not null && map.Template is not { Instanceable: true },
            PvpAreaState.IsWarModeProtected(player), PvpAreaState.IsWarModeProtected(provoker), player.Duel is not null, _defend is not null || _stay is not null);
        if (refusal != DefenseReportRefusal.None || map is null) return refusal;

        uint now = _session.World.NowMs;
        if (_defenseReported && unchecked((int)(now - _defenseReportAtMs)) < DefenseReportIntervalMs) return DefenseReportRefusal.Throttled;
        _defenseReported = true;
        _defenseReportAtMs = now;

        Vector3 position = PlayerbotMotion.CurrentPosition(player);
        uint zone = player.ZoneId, area = 0;
        if (map.TryGetZoneAndAreaIdWithoutLoading(position.X, position.Y, position.Z, out (uint ZoneId, uint AreaId) there))
        {
            area = there.AreaId;
            if (zone == 0) zone = there.ZoneId;
        }

        report = board.Report(player.Guid.Value, provoker.Guid.Value, player.Team, player.MapId, map.InstanceId, zone, area, position, player.Level);
        return DefenseReportRefusal.None;
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

/// <summary>Why <see cref="PlayerbotBrain.TryReportDefense"/> did not ask the call board.</summary>
internal enum DefenseReportRefusal : byte
{
    /// <summary>Nothing against it: the board was asked.</summary>
    None,

    /// <summary>Defense is off (<c>Pvp:Defense:Enabled</c>), or the brain has no board.</summary>
    Off,

    /// <summary>The bot is on an instance or a battleground map (or has no map).</summary>
    NotOpenWorld,

    /// <summary>The bot or its provoker stands where War Mode protects (an exempt area, or not placed yet).</summary>
    WarModeProtected,

    /// <summary>The bot is in a duel, or holds a defend post or a stay.</summary>
    Busy,

    /// <summary>The bot reported less than <see cref="PlayerbotBrain.DefenseReportIntervalMs"/> ago.</summary>
    Throttled,
}
