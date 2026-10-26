using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// Drives one managed bot while the battlegrounds coordinator holds it (<see cref="PlayerbotBattlegroundCoordinator"/>): the walk to a
/// battlemaster before the queue, and the match itself, instead of <see cref="PlayerbotBrain"/>. World thread only; one per bot.
/// <para>
/// In a match the bot waits behind the gates until the match is in progress, then fights the enemy players near it (the class rotation of
/// <see cref="PlayerbotCombatSpells"/> and the melee swing, as the group AI fights creatures) and plays the objective of its role
/// (<see cref="PlayerbotBattlegroundRoles"/>):
/// </para>
/// <list type="bullet">
/// <item><b>Warsong Gulch</b> (vmangos BattleBotWaypoints.cpp:76-140, <c>WSG_AtAllianceFlag</c> / <c>WSG_AtHordeFlag</c>; BattleGroundWS.cpp:354-480 and
/// 539-548 for the server side): every think the coordinator gives each bot an order (<see cref="PlayerbotWarsongTactics.Plan"/>, after
/// mangosbot's <c>BGTactics::selectObjective</c>): the carrier walks home into its own flag room trigger (3646 Alliance, 3647 Horde), which
/// its motion reports with CMSG_AREATRIGGER and the match turns into a capture; the nearest bot to a dropped flag clicks it (CMSG_GAMEOBJ_USE:
/// its own returned, the enemy's picked up); defenders hold the flag room and fight intruders; hunters chase and kill the enemy carrier of
/// the own flag; escorts follow the own carrier (vmangos <c>BattleBotAI::SelectFollowTarget</c>, BattleBotAI.cpp:379-421); the rest take the
/// enemy flag from its stand (waiting there while it respawns) or hold the middle of the field.</item>
/// <item><b>Arathi Basin</b>: the team plan of <see cref="PlayerbotArathiBasinTactics"/>, carried out in PlayerbotBattlegroundAI.ArathiBasin.cs
/// (assault, defend, retreat, buffs).</item>
/// <item><b>Alterac Valley</b> (<c>PlayerbotBattlegroundAI.AlteracValley.cs</c>, <see cref="PlayerbotAlteracValleyTactics"/>): the push on the
/// enemy general through the enemy captain, towers and graveyards along the bot's lane, the own assaulted nodes clicked back and a limited
/// count of guards.</item>
/// </list>
/// <para>
/// A dead bot releases through <see cref="PlayerbotRecovery"/>, which on a battleground map waits beside the graveyard's spirit guide for its
/// area resurrection wave (CMSG_AREA_SPIRIT_HEALER_QUEUE; cmangos battlegrounds.cpp npc_spirit_guideAI) instead of running to its body.
/// </para>
/// <para>
/// A ranged bot with nothing to cast for <see cref="PlayerbotBrain.RangedIdleLimitMs"/>, or below 10% mana, closes to melee and swings
/// (vmangos CombatBotBaseAI::BeginChasing keeps the 25-yard caster distance only above 10% mana or with a ranged weapon).
/// </para>
/// </summary>
internal sealed partial class PlayerbotBattlegroundAI
{
    /// <summary>vmangos INTERACTION_DISTANCE (5 yards), less a margin: a flag or a battlemaster is used from this close.</summary>
    internal const float InteractYards = 4f;

    /// <summary>An enemy player this close is fought (an enemy attacking the bot always is).</summary>
    internal const float EngageYards = 30f;

    /// <summary>The enemy carrying the bot's own flag is chased from this far (Warsong Gulch).</summary>
    internal const float ChaseCarrierYards = 80f;

    /// <summary>A bot returns its own dropped flag from this far (a defender from anywhere on the map).</summary>
    internal const float ReturnFlagYards = 60f;

    /// <summary>A defender stays within this distance of what it defends.</summary>
    internal const float GuardYards = 8f;

    /// <summary>The Arathi Basin and Alterac Valley banner spell (Spell.dbc 21651 "Opening": OPEN_LOCK misc 17, 10 s, 0-5 yards).</summary>
    internal const uint BannerSpell = 21651;

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotCombatSpells _spells;
    private readonly PlayerbotRecovery _recovery;
    private readonly PlayerbotBattlegroundBuffs _buffs = new();
    private readonly float _followAngle;
    private readonly float _followDistance;
    private PlayerbotRoute? _routeSlot;

    /// <summary>The route this driver walks. Letting go of one (null or another route) marks it released
    /// (<see cref="PlayerbotRoute.Released"/>): <see cref="PlayerbotMotion.SettleAfterDriver"/> stops a walk nobody holds.</summary>
    private PlayerbotRoute? _route
    {
        get => _routeSlot;
        set
        {
            if (ReferenceEquals(_routeSlot, value)) return;
            if (_routeSlot is { } old) old.Released = true;
            _routeSlot = value;
        }
    }

    private Vector3 _routeGoal;
    private readonly PlayerbotPvpFight _pvp;
    private readonly Func<Vector3, uint, bool, bool> _chase;
    private readonly Action _dropRoute;
    private Player? _fighter;
    private uint _thinkElapsed;
    private bool _deathRetired;

    internal PlayerbotBattlegroundAI(WorldSession session, PlayerbotOptions options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _spells = new PlayerbotCombatSpells(session);
        _pvp = new PlayerbotPvpFight(session, _spells);
        _chase = (goal, interval, chase) => WalkTo(_fighter!, goal, interval, toward: false, chase: chase);
        _dropRoute = () => _route = null;
        _recovery = new PlayerbotRecovery(session, options);
        var random = new Random(session.AccountId);
        _followAngle = (float)(random.NextDouble() * MathF.Tau);
        _followDistance = 3f + ((float)random.NextDouble() * 2f);
    }

    /// <summary>What the bot is doing (reported and persisted as its goal).</summary>
    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Battleground;

    /// <summary>The objective or enemy the bot is after: a game object or creature entry, 0 for a player or nothing.</summary>
    internal uint TargetEntry { get; private set; }

    /// <summary>The enemy the bot fights (inspection and tests).</summary>
    internal Unit? Target => _pvp.Target;

    /// <summary>
    /// The enemy this think chose and engaged, kept even when the engagement was given up within the same think (the chase was refused) and the bot
    /// went on to its objectives; null when the think engaged no enemy (inspection and tests).
    /// </summary>
    internal Unit? EngagedThisThink { get; private set; }

    /// <summary>A short word for the status line: what the bot does in the match now.</summary>
    internal string Activity { get; private set; } = "idle";

    /// <summary>The banner casts the bot has submitted and the server took (Arathi Basin; inspection and tests).</summary>
    internal int BannerCasts { get; private set; }

    /// <summary>The bot's corpse run (inspection and tests).</summary>
    internal PlayerbotRecovery Recovery => _recovery;

    /// <summary>The bot's use of the battleground buff objects (inspection and tests).</summary>
    internal PlayerbotBattlegroundBuffs Buffs => _buffs;

    /// <summary>True when the walk to a battlemaster refused a route (or stood at the spot for 5s with no living battlemaster).</summary>
    internal bool TravelRefused { get; private set; }

    /// <summary>The Warsong Gulch order of the last think (inspection and tests).</summary>
    internal PlayerbotWsgOrder? Order { get; private set; }

    /// <summary>How long the bot has stood still at its decisions in the match, under its current Warsong Gulch duty (inspection and tests).</summary>
    internal uint StillMs { get; private set; }

    /// <summary>The bot was idle at its last think (<see cref="PlayerbotWarsongTactics.IsIdle"/>; inspection and tests).</summary>
    internal bool IdleNow { get; private set; }

    /// <summary>The thinks at which the bot was idle in a match (inspection and tests).</summary>
    internal int IdleThinks { get; private set; }

    /// <summary>A bot holds the middle of the field within this distance of it.</summary>
    internal const float MidfieldYards = 15f;

    private uint _atMasterMs;
    private uint _inMatchIdleMs;
    private Vector3? _lastThinkAt;
    private PlayerbotWsgDuty? _stillDuty;
    private bool _reportedObjectiveUnreachable;
    private readonly PlayerbotObjectiveReports _walkRefusals = new();
    private bool _reportedIdleInMatch;

    private long Now => (long)_session.World.Uptime.TotalMilliseconds;

    /// <summary>Drop every route, target and cast intention (the coordinator lets the bot go, or it changes its phase).</summary>
    internal void Release(Player player)
    {
        _fighter = null;
        if (_pvp.Attacking || player.Combat.Victim is not null) StopAttacking(player);
        PlayerbotMovementControl.Stop(_session, player);
        _route = null;
        _pvp.Target = null;
        _pvp.Attacking = false;
        _pvp.Forget();
        TravelRefused = false;
        _atMasterMs = 0;
        _inMatchIdleMs = 0;
        _pvp.RangedIdleMs = 0;
        _lastThinkAt = null;
        _stillDuty = null;
        StillMs = 0;
        IdleNow = false;
        Order = null;
        _reportedObjectiveUnreachable = false;
        _walkRefusals.Clear();
        _reportedIdleInMatch = false;
        _spells.GroupRole = null;
        _spells.Reset();
        _recovery.Reset();
        _buffs.Reset(player.Guid);
        EndRest(player);
        Activity = "idle";
    }

    // --- before the queue ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One think of the walk to a battlemaster: towards <paramref name="at"/>, stopping in interaction range. Out of combat only; a fight on
    /// the way is answered (the bot is attacked) before it walks on. True while it walks or fights.
    /// </summary>
    internal bool Travel(Player player, Vector3 at, uint elapsedMs)
    {
        if (!Think(player, elapsedMs, out uint interval)) return true;
        TravelRefused = false;
        if (!player.IsAlive) return false;
        Goal = PlayerbotGoalKind.Battleground;
        Activity = "to-battlemaster";
        if (FirstAttacker(player) is { } attacker)
        {
            Fight(player, attacker, interval);
            return true;
        }

        if (_pvp.Target is not null) ClearTarget(player);
        if (Vector3.Distance(Position(player), at) <= InteractYards - 1f)
        {
            Hold(player);
            _atMasterMs = unchecked(_atMasterMs + interval);
            if (_atMasterMs >= 5_000)
            {
                TravelRefused = true;
                ReportObjectiveUnreachable(player, $"no living battlemaster at {at}");
            }
            return false;
        }

        _atMasterMs = 0;
        bool walking = WalkTo(player, at, interval, toward: true, what: "battlemaster");
        TravelRefused = !walking;
        return walking;
    }

    // --- in the match ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// One tick of a participant (world thread, within the shared action budget like the brain). <paramref name="order"/> is the bot's
    /// Warsong Gulch order of this tick (<see cref="PlayerbotWarsongTactics.Plan"/>, computed once per match and team by the coordinator).
    /// </summary>
    internal void Play(Player player, Battleground match, Team team, PlayerbotBattlegroundRole role, uint elapsedMs, PlayerbotWsgOrder? order = null)
    {
        EngagedThisThink = null;
        if (!player.IsInWorld)
        {
            PlayerbotMovementControl.Update(_session, player);
            return;
        }

        Goal = PlayerbotGoalKind.Battleground;
        _spells.GroupRole = null;
        bool dead = !player.IsAlive;
        if (dead && !_deathRetired)
        {
            _pvp.Target = null;
            _pvp.Attacking = false;
            _route = null;
            _spells.Reset();
            _buffs.Reset(player.Guid);
            _restUntilMs = -1;
            _deathRetired = true;
        }

        if (!Think(player, elapsedMs, out uint interval)) return;
        if (dead)
        {
            Goal = PlayerbotGoalKind.Recover;
            Activity = "dead";
            _inMatchIdleMs = 0;
            StillMs = 0;
            _lastThinkAt = null;
            _stillDuty = null;
            IdleNow = false;
            // Not before the gates open, nor after the end: the server refuses a reclaim outside a running match (vmangos
            // HandleReclaimCorpseOpcode, the BattleGround status check) and resurrects everyone at the end.
            if (match.Status == BattlegroundStatus.InProgress) _recovery.Update(player, interval);
            Activity = _recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.AwaitAreaResurrection ? "spirit-guide" : "corpse-run";
            return;
        }

        _deathRetired = false;
        _recovery.Reset();
        PlayerbotNavigation.Guard(player, null);
        if (player.MapId != match.MapId || player.Map?.InstanceId != match.InstanceId)
        {
            // Bound to the match but not on its map (on the way in or out, or put elsewhere): its objectives are coordinates of the
            // battleground map and mean nothing here; never route to them on another map.
            if (_pvp.Target is not null) ClearTarget(player);
            Activity = "off-map";
            Order = null;
            _inMatchIdleMs = 0;
            StillMs = 0;
            IdleNow = false;
            Hold(player);
            return;
        }

        if (match.Status != BattlegroundStatus.InProgress)
        {
            // Behind the gates before the start, frozen at the end (BattleGround.cpp:290-464, 651-769).
            if (_pvp.Target is not null) ClearTarget(player);
            Activity = match.Status == BattlegroundStatus.WaitLeave ? "match-over" : "waiting-start";
            _inMatchIdleMs = 0;
            StillMs = 0;
            IdleNow = false;
            Hold(player);
            return;
        }

        if (Casting(player))
        {
            Activity = "casting";
            _inMatchIdleMs = 0;
            StillMs = 0;
            IdleNow = false;
            return;
        }

        if (match is ArathiBasin arathi)
        {
            // Arathi Basin plays its own team plan (PlayerbotBattlegroundAI.ArathiBasin.cs): retreat, defend, assault, buffs.
            PlayArathiBasin(player, arathi, team, role, interval);
            ObserveMatchOutcome(player, match, interval);
            return;
        }

        bool carrier = match is WarsongGulch carrying && carrying.FlagPicker(BattlegroundConstants.OtherTeam(team)) == player.Guid;
        Order = match is WarsongGulch ? order : null;
        Unit? enemy = carrier ? FirstAttackerIfStill(player) : OrderedEnemy(player, match, team) ?? SelectEnemy(player, match, team, chaseThief: Order is null);
        if (_buffs.Update(_session, player, match, team, enemy, carrier, Now) is { } buff)
        {
            // A speed, restoration or berserking buff worth the detour: walk onto it (the trap casts it; PlayerbotBattlegroundBuffs).
            EndRest(player);
            Activity = "taking-buff";
            _inMatchIdleMs = 0;
            StillMs = 0;
            IdleNow = false;
            TargetEntry = buff.Entry;
            if (!WalkTo(player, buff.Position, interval, toward: false, what: "buff")) _buffs.GiveUp(player.Guid, Now);
            return;
        }

        if (enemy is not null)
        {
            EndRest(player);
            Activity = Order is { Duty: PlayerbotWsgDuty.HuntCarrier } ? "hunting-carrier" : "fighting";
            _inMatchIdleMs = 0;
            EngagedThisThink = enemy;
            Fight(player, enemy, interval, mustPursue: MustPursue(match, team, enemy));
            if (_pvp.Target is not null)
            {
                ObserveStill(player, match, enemy, interval);
                return;
            }
        }

        if (_pvp.Target is not null) ClearTarget(player);
        TargetEntry = 0;
        // vmangos BattleBotAI::UpdateAI: DrinkAndEat (:852) before the out-of-combat spells (:870).
        if (RestBetweenFights(player, carrier))
        {
            _inMatchIdleMs = 0;
            StillMs = 0;
            IdleNow = false;
            return;
        }

        if (!player.Combat.IsInCombat && _spells.UpdateOutOfCombat(player, interval))
        {
            _inMatchIdleMs = 0;
            StillMs = 0;
            IdleNow = false;
            return;
        }

        switch (match)
        {
            case WarsongGulch wsg:
                PlayWarsong(player, wsg, team, Order, interval);
                break;
            case AlteracValley av:
                PlayAlteracValley(player, av, team, role, interval);
                break;
            default:
                Activity = "unsupported";
                Hold(player);
                break;
        }

        ObserveMatchOutcome(player, match, interval);
        ObserveStill(player, match, null, interval);
    }

    /// <summary>
    /// The enemy the bot's order names: the enemy carrier for a hunter (wherever it is: mangosbot sets the objective to its position), an
    /// intruder of the flag room for a defender. Null when the order names none or it is no attackable enemy now.
    /// </summary>
    private Player? OrderedEnemy(Player player, Battleground match, Team team)
    {
        if (Order is not { Target.IsEmpty: false } order || order.Duty is not (PlayerbotWsgDuty.HuntCarrier or PlayerbotWsgDuty.DefendFlagRoom)) return null;
        // The carrier is never set aside; an intruder the bot found no path to is (and is picked again after UnreachableMs).
        return Online(order.Target) is { } enemy && IsEnemy(player, enemy, match, BattlegroundConstants.OtherTeam(team))
            && (IsFlagCarrier(match, team, enemy) || !_pvp.IsSkipped(enemy)) ? enemy : null;
    }

    /// <summary>The enemy carries the bot's own flag (Warsong Gulch): the one target a bot never gives up on, whatever the path.</summary>
    private static bool IsFlagCarrier(Battleground match, Team team, Unit unit)
        => match is WarsongGulch wsg && unit is Player enemy && wsg.FlagState(team) == WsgFlagState.OnPlayer && wsg.FlagPicker(team) == enemy.Guid;

    /// <summary>The target is the carrier of the own flag (Warsong Gulch) or an Alterac Valley captain or general: a refused path never sets it aside.</summary>
    private bool MustPursue(Battleground match, Team team, Unit unit)
        => IsFlagCarrier(match, team, unit) || (unit is Game.Creatures.Creature creature && IsAlteracBoss(match, creature));

    /// <summary>
    /// The idle check of one think (<see cref="PlayerbotWarsongTactics.IsIdle"/>): a living bot out of combat that stood still for
    /// <see cref="PlayerbotWarsongTactics.IdleLimitMs"/> although its order sends it somewhere, or an enemy is beside it, is idle; reported
    /// once per match as <c>bot-bg/idle-in-match</c>. Reads only the bot and the units its order names (no scan).
    /// </summary>
    private void ObserveStill(Player player, Battleground match, Unit? fighting, uint interval)
    {
        Vector3 here = Position(player);
        bool still = _lastThinkAt is { } last && Vector3.Distance(last, here) < PlayerbotWarsongTactics.StillYards;
        _lastThinkAt = here;
        // The still clock is the duty's: a defender that held its room for 15 s and is then sent to the middle (a teammate came nearer
        // the room and took the defence) stood at its old goal, not at its new one; it is idle only if it stays put under the new duty.
        bool sameDuty = Order?.Duty == _stillDuty;
        _stillDuty = Order?.Duty;
        if (Order is null) StillMs = 0;
        if (Order is not { } order)
        {
            IdleNow = false;
            return;
        }

        Player? named = order.Target.IsEmpty ? null : Online(order.Target);
        if (named is not null && !ReferenceEquals(named.Map, player.Map)) named = null;
        float goal = order.Duty is PlayerbotWsgDuty.EscortCarrier or PlayerbotWsgDuty.HuntCarrier && named is not null
            ? Distance(player, named)
            : Vector3.Distance(here, order.Goal);
        float arrived = order.Duty switch
        {
            PlayerbotWsgDuty.Midfield => MidfieldYards + 2f,
            PlayerbotWsgDuty.EscortCarrier => 10f,
            PlayerbotWsgDuty.HuntCarrier => EngageYards,
            PlayerbotWsgDuty.ReturnOwnFlag or PlayerbotWsgDuty.TakeDroppedEnemyFlag => InteractYards + 1f,
            _ => GuardYards + 2f,
        };
        // A bot that swings at its enemy in reach fights (the swing puts it in combat a moment later); one that only stands beside it does not.
        bool engaged = player.Combat.IsInCombat || (fighting is not null && (_pvp.Attacking || _pvp.Target is not null))
            || _route is not null || PlayerbotMotion.IsActive(player) || Casting(player);
        float enemyYards = fighting is not null ? Distance(player, fighting)
            : named is not null && order.Duty is PlayerbotWsgDuty.HuntCarrier or PlayerbotWsgDuty.DefendFlagRoom or PlayerbotWsgDuty.Midfield
                ? Distance(player, named) : float.MaxValue;
        bool idleStep = PlayerbotWarsongTactics.IsIdle(player.IsAlive, engaged, PlayerbotWarsongTactics.IdleLimitMs, goal, arrived, enemyYards);
        uint stepMs = Math.Min(Math.Max(1u, interval), 5_000u);
        StillMs = still && sameDuty && idleStep ? unchecked(StillMs + stepMs) : 0;
        IdleNow = PlayerbotWarsongTactics.IsIdle(player.IsAlive, engaged, StillMs, goal, arrived, enemyYards);
        if (!IdleNow) return;
        IdleThinks++;
        string enemyText = enemyYards == float.MaxValue ? "none" : enemyYards.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " yd";
        ReportIdleInMatch(player, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"bot idle in {match.Type} match {match.InstanceId}: {order.Duty} still for {StillMs} ms out of combat, goal {goal:F0} yd away (arrived at {arrived:F0}), enemy {enemyText} (activity={Activity})"));
    }

    private void ObserveMatchOutcome(Player player, Battleground match, uint interval)
    {
        if (Activity is "no-flag" or "no-banners" or "no-banner-spell")
            ReportObjectiveUnreachable(player, $"battleground {match.Type} objective unavailable ({Activity})");

        bool unproductive = Activity is "idle" or "unsupported" or "no-orders" or "no-flag" or "no-banners" or "no-banner-spell";
        if (unproductive)
        {
            uint stepMs = Math.Min(Math.Max(1u, interval), 5_000u);
            _inMatchIdleMs = unchecked(_inMatchIdleMs + stepMs);
            if (Activity == "unsupported" || _inMatchIdleMs >= 15_000)
                ReportIdleInMatch(player, $"bot idle in {match.Type} match {match.InstanceId} (activity={Activity}, idleMs={_inMatchIdleMs})");
        }
        else
        {
            _inMatchIdleMs = 0;
        }
    }

    private void ReportObjectiveUnreachable(Player player, string detail)
    {
        if (_reportedObjectiveUnreachable || _session.IsInChaosProbe) return;
        _reportedObjectiveUnreachable = true;
        PlayerbotAnomalies.Report(player, "bg", "objective-unreachable", detail);
    }

    /// <summary>The refused walk to <paramref name="goal"/> as <c>objective-unreachable</c>, once per (goal, origin) cell pair until <see cref="Release"/>.</summary>
    private void ReportWalkRefused(Player player, Vector3 goal, string detail)
    {
        if (_session.IsInChaosProbe || !_walkRefusals.TryAdd(goal, Position(player))) return;
        PlayerbotAnomalies.Report(player, "bg", "objective-unreachable", detail);
    }

    /// <summary>
    /// The last point of the navigation mesh on the straight line from the bot to <paramref name="goal"/>, or null when the mesh holds the
    /// whole line (the goal itself is no better), has no answer, or the point is within 2 yards of the goal.
    /// </summary>
    private Vector3? SnappedGoal(Player player, Vector3 goal)
    {
        if (player.Map is null || !goal.IsFinite()) return null;
        IPathfinder pathfinder = WorldCollision.Of(_session.World).PathfinderFor(player.MapId);
        Vector3 snapped = Groups.PlayerbotGroupCoordinator.SnapToMesh(pathfinder, player.MapId, Position(player), goal);
        return snapped.IsFinite() && Vector3.Distance(snapped, goal) > 2f && Vector3.Distance(snapped, Position(player)) > 1.5f ? snapped : null;
    }

    private void ReportIdleInMatch(Player player, string detail)
    {
        if (_reportedIdleInMatch || _session.IsInChaosProbe) return;
        _reportedIdleInMatch = true;
        PlayerbotAnomalies.Report(player, "bg", "idle-in-match", detail);
    }

    // --- Warsong Gulch ---------------------------------------------------------------------------------------------------

    /// <summary>The bot's Warsong Gulch order of this think (<see cref="PlayerbotWarsongTactics.Plan"/>), carried out.</summary>
    private void PlayWarsong(Player player, WarsongGulch wsg, Team team, PlayerbotWsgOrder? order, uint interval)
    {
        Team enemy = BattlegroundConstants.OtherTeam(team);
        Map map = player.Map!;
        if (order is not { } o)
        {
            Activity = "no-orders";
            Hold(player);
            return;
        }

        switch (o.Duty)
        {
            case PlayerbotWsgDuty.CarryHome:
                // Inside its own flag room it waits for its own flag to come back (ForceFlagAreaTrigger captures then).
                Activity = "carrying";
                WalkTo(player, o.Goal, interval, toward: true);
                return;

            case PlayerbotWsgDuty.ReturnOwnFlag:
                // BattleGroundWS.cpp:396-415: a click on the own dropped flag returns it.
                Activity = "returning-flag";
                if (map.FindObject(wsg.DroppedFlagGuid(team)) is GameObject dropped) UseObject(player, dropped, interval);
                else WalkTo(player, o.Goal, interval, toward: false);
                return;

            case PlayerbotWsgDuty.TakeDroppedEnemyFlag:
                Activity = "taking-flag";
                if (map.FindObject(wsg.DroppedFlagGuid(enemy)) is GameObject loose) UseObject(player, loose, interval);
                else WalkTo(player, o.Goal, interval, toward: false);
                return;

            case PlayerbotWsgDuty.HuntCarrier:
                // Not attackable from here yet (out of sight or reach): towards it.
                Activity = "hunting-carrier";
                if (Online(o.Target) is { } thief && ReferenceEquals(thief.Map, map))
                {
                    if (!WalkTo(player, Position(thief), interval, toward: true, what: "carrier", chase: true))
                        WalkTo(player, o.Goal, interval, toward: true, what: "carrier", chase: true);
                }
                else
                {
                    WalkTo(player, o.Goal, interval, toward: true);
                }

                return;

            case PlayerbotWsgDuty.EscortCarrier:
                Activity = "escorting";
                if (Online(o.Target) is { } carrier && carrier.Guid != player.Guid && ReferenceEquals(carrier.Map, map)) Follow(player, carrier, interval);
                else Guard(player, o.Goal, interval);
                return;

            case PlayerbotWsgDuty.DefendFlagRoom:
                Activity = "defending";
                Guard(player, o.Goal, interval);
                return;

            case PlayerbotWsgDuty.TakeEnemyFlag:
            {
                bool ready = wsg.FlagState(enemy) == WsgFlagState.OnBase
                    && wsg.IsActiveEvent(enemy == Team.Alliance ? WarsongGulch.EventFlagAlliance : WarsongGulch.EventFlagHorde, 0);
                uint enemyStandEntry = enemy == Team.Alliance ? WarsongGulch.AllianceFlagBaseEntry : WarsongGulch.HordeFlagBaseEntry;
                GameObject? stand = !ready ? null
                    : !o.Target.IsEmpty && map.FindObject(o.Target) is GameObject { IsSpawned: true } found ? found
                    : map.FindUpdater<GameObjectMapSystem>()?.GameObjects.FirstOrDefault(go => go.Entry == enemyStandEntry && go.IsSpawned);
                // The stand object exists only once its grid is loaded, so a flag the match calls ready often has nothing to click
                // until the bot gets there. Going there is waiting-flag. no-flag (idle) is only once the bot has arrived and the
                // stand is still missing: content that failed to load, not the walk across the field (bot QA #819).
                Activity = PlayerbotWarsongTactics.EnemyStandActivity(ready, stand is not null,
                    Vector3.Distance(Position(player), o.Goal), GuardYards);
                if (stand is not null)
                {
                    UseObject(player, stand, interval);
                    return;
                }

                Guard(player, o.Goal, interval);
                return;
            }

            default:
                Activity = "midfield";
                Guard(player, o.Goal, interval, MidfieldYards);
                return;
        }
    }

    /// <summary>The centre of the team's own flag room trigger (3646 Alliance, 3647 Horde; BattleBotWaypoints.cpp:70-74).</summary>
    private Vector3? OwnFlagRoom(Team team)
    {
        uint id = team == Team.Alliance ? WarsongGulch.AreaTriggerAllianceFlagSpawn : WarsongGulch.AreaTriggerHordeFlagSpawn;
        return Game.Maps.Templates.WorldMaps.Of(_session.World).FindAreaTrigger(id) is { } trigger ? new Vector3(trigger.X, trigger.Y, trigger.Z) : null;
    }

    /// <summary>
    /// The node banners spawned on the bot's map now, each with its node: the objects whose battleground event (the
    /// <c>gameobject_battleground</c> row, event1 = node) names a node, that the banner spell opens (a lock id) and whose opening the match
    /// turns into the node click (<see cref="MatchRuntime.RoutesBannerUse"/>).
    /// </summary>
    private List<(int Node, GameObject Banner)> Banners(Player player, int nodeCount = ArathiBasin.NodeCount)
    {
        var banners = new List<(int, GameObject)>();
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects
            || _session.Services.GetService<BattlegroundFeature>() is not { } feature) return banners;
        foreach (GameObject go in objects.GameObjects)
        {
            // A banner the match does not route to the node click (a goober whose spell is no banner trigger) is never cast at: the cast would claim nothing.
            if (!go.IsSpawned || go.Spawn is not { } spawn || GameObjectLocks.LockIdOf(go.Template) == 0 || !MatchRuntime.RoutesBannerUse(go.Template)) continue;
            foreach ((byte e1, byte _) in feature.Events.ObjectEvents(spawn.Guid))
                if (e1 < nodeCount) banners.Add((e1, go));
        }

        return banners;
    }

    /// <summary>vmangos <c>AtFlag</c> (BattleBotWaypoints.cpp:181-189): a friend within interaction range already casting the banner spell there.</summary>
    private bool TeammateCasting(Player player, GameObject banner)
    {
        if (_session.Services.GetService<SpellFeature>() is not { } feature || player.Map is not { } map) return false;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (guid == player.Guid || map.FindObject(guid) is not Player friend || friend.Team != player.Team || !friend.IsAlive) continue;
            if (Distance(friend, banner) <= InteractYards + 1f
                && feature.System.GetState(friend.Guid)?.CurrentCast is { Spell.Id: BannerSpell, State: SpellCastState.Preparing or SpellCastState.Casting })
                return true;
        }

        return false;
    }

    // --- fighting --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The enemy player or Alterac Valley NPC to fight now: the carrier of the bot's own flag (Warsong Gulch) within <see cref="ChaseCarrierYards"/>, then one
    /// attacking the bot, then the one it fights already, then the nearest within <see cref="EngageYards"/>. Teammates never: the match's
    /// teams decide, not the faction templates (a player may always attack a player as far as the combat hooks go).
    /// </summary>
    /// <param name="chaseThief">Chase the carrier of the own flag from <see cref="ChaseCarrierYards"/> (without a Warsong Gulch order; with one,
    /// the hunters the plan names chase it and the others keep their duty).</param>
    private Unit? SelectEnemy(Player player, Battleground match, Team team, bool chaseThief = true)
    {
        if (player.Map is not { } map) return null;
        Team enemyTeam = BattlegroundConstants.OtherTeam(team);
        if (chaseThief && match is WarsongGulch wsg && wsg.FlagState(team) == WsgFlagState.OnPlayer && Online(wsg.FlagPicker(team)) is { } thief
            && IsEnemy(player, thief, match, enemyTeam) && Distance(player, thief) <= ChaseCarrierYards)
            return thief;

        // A target the bot found no path to (PlayerbotPvpFight.Unreachable) is skipped for 30 s, here and in the scan below; the carrier
        // of the own flag was returned above and is never skipped.
        foreach (Unit unit in player.Combat.Attackers)
        {
            if (unit is Player attacker && IsEnemy(player, attacker, match, enemyTeam) && !_pvp.IsSkipped(attacker)) return attacker;
            if (match is AlteracValley && unit is Game.Creatures.Creature npcAttacker && IsEnemyCreature(player, npcAttacker, requireHostile: false)
                && (IsAlteracBoss(match, npcAttacker) || !_pvp.IsSkipped(npcAttacker))) return npcAttacker;
        }

        if (_pvp.Target is Player current && IsEnemy(player, current, match, enemyTeam) && Distance(player, current) <= EngageYards * 1.5f
            && !_pvp.IsSkipped(current)) return current;
        if (match is AlteracValley && _pvp.Target is Game.Creatures.Creature currentNpc && IsAlteracBoss(match, currentNpc) is var currentBoss && IsEnemyCreature(player, currentNpc, deliberate: currentBoss)
            && Distance(player, currentNpc) <= EngageYards * 1.5f && (currentBoss || !_pvp.IsSkipped(currentNpc))) return currentNpc;

        // The nearest within reach; of those, one on the bot's own level first (walls, cliffs, the floors of an AV base).
        Unit? nearest = null;
        float best = EngageYards;
        bool bestOtherLevel = true;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            WorldObject? obj = map.FindObject(guid);
            if (obj is Player other && IsEnemy(player, other, match, enemyTeam) && !_pvp.IsSkipped(other))
            {
                float distance = Distance(player, other);
                bool otherLevel = PlayerbotPvpFight.OnOtherLevel(player, other);
                if (distance <= EngageYards && (nearest is not Player || PlayerbotPvpFight.IsBetterTarget(otherLevel, distance, bestOtherLevel, best)))
                {
                    best = distance;
                    bestOtherLevel = otherLevel;
                    nearest = other;
                }
            }
            else if (match is AlteracValley && nearest is not Player && obj is Game.Creatures.Creature npc && IsAlteracBoss(match, npc) is var boss && IsEnemyCreature(player, npc, deliberate: boss) && (boss || !_pvp.IsSkipped(npc)))
            {
                float distance = Distance(player, npc);
                bool otherLevel = PlayerbotPvpFight.OnOtherLevel(player, npc);
                if (distance <= EngageYards && (nearest is null || PlayerbotPvpFight.IsBetterTarget(otherLevel, distance, bestOtherLevel, best)))
                {
                    best = distance;
                    bestOtherLevel = otherLevel;
                    nearest = npc;
                }
            }
        }

        return nearest;
    }

    /// <summary>A living, attackable participant of the other team on the bot's map.</summary>
    private static bool IsEnemy(Player player, Player other, Battleground match, Team enemyTeam)
        => match.PlayerTeam(other.Guid) == enemyTeam && PlayerbotHostility.IsHostilePlayer(player, other);

    /// <summary>
    /// A living, attackable and hostile Alterac Valley NPC guard, captain, commander or general on the bot's map: the neutral wildlife of the
    /// valley (the rams and wolves) is left alone unless it attacks the bot (<paramref name="requireHostile"/> false).
    /// </summary>
    /// <param name="deliberate">The script targets this creature on purpose (the enemy captain or general): it is never filtered as a civilian.</param>
    private static bool IsEnemyCreature(Player player, Game.Creatures.Creature creature, bool requireHostile = true, bool deliberate = false)
        => creature.IsInWorld && creature.IsAlive && ReferenceEquals(creature.Map, player.Map)
            && (creature.NpcFlags & (uint)Game.Npc.NpcFlags.SpiritGuide) == 0 && player.Map is { } map
            && map.Combat.Hooks.CanAttack(player, creature) && (!requireHostile || map.Combat.Hooks.IsHostileTo(player, creature))
            // A civilian (a villager, a vendor) is not picked from a scan; one that attacks the bot (requireHostile false) is fought back.
            && (deliberate || !requireHostile || !PlayerbotTargetFilter.IsCivilian(creature));

    /// <summary>A unit attacking the bot that it may hit back (the walk to a battlemaster, a carrier that is blocked).</summary>
    private static Unit? FirstAttacker(Player player)
        => player.Combat.Attackers.FirstOrDefault(u => u.IsAlive && u.IsInWorld && ReferenceEquals(u.Map, player.Map)
            && player.Map is { } map && map.Combat.Hooks.CanAttack(player, u));

    /// <summary>A carrier does not stop to fight; it only keeps hitting a target it already fights in melee reach.</summary>
    private Unit? FirstAttackerIfStill(Player player)
        => _pvp.Target is { IsAlive: true } current && player.Combat.Attackers.Contains(current) && MapCombat.CanReachWithMeleeAutoAttack(player, current)
            ? current : null;

    /// <param name="mustPursue">The target is the carrier of the own flag or an objective of the script: a refused path never sets it aside.</param>
    private void Fight(Player player, Unit target, uint interval, bool mustPursue = false)
    {
        TargetEntry = target is Game.Creatures.Creature creature ? creature.Entry : 0;
        _fighter = player;
        _pvp.Fight(player, target, interval, _chase, _dropRoute, mustPursue);
    }

    private void ClearTarget(Player player)
    {
        if (_pvp.Attacking || player.Combat.Victim is not null) StopAttacking(player);
        _pvp.Target = null;
        _pvp.Attacking = false;
        _route = null;
    }

    private void StopAttacking(Player player)
    {
        _pvp.Stop(player);
        _route = null;
        TargetEntry = 0;
    }

    // --- objects, following, walking --------------------------------------------------------------------------------------

    /// <summary>Walk to <paramref name="go"/> and use it (CMSG_GAMEOBJ_USE) once in interaction range: a flag stand or a dropped flag.</summary>
    private void UseObject(Player player, GameObject go, uint interval)
    {
        TargetEntry = go.Entry;
        if (Distance(player, go) > InteractYards)
        {
            WalkTo(player, new Vector3(go.X, go.Y, go.Z), interval, toward: false);
            return;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        _route = null;
        Act(WorldOpcode.CmsgGameobjUse, PlayerbotNavigation.GuidPayload(go.Guid.Value));
    }

    /// <summary>The nearest spawned object on the bot's map that <paramref name="match"/> accepts.</summary>
    private static GameObject? FindObject(Player player, Func<GameObject, bool> match)
    {
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects) return null;
        GameObject? best = null;
        float nearest = float.MaxValue;
        foreach (GameObject go in objects.GameObjects)
        {
            if (!go.IsSpawned || !match(go)) continue;
            float distance = Distance(player, go);
            if (distance < nearest)
            {
                nearest = distance;
                best = go;
            }
        }

        return best;
    }

    private void Follow(Player player, Player leader, uint interval)
    {
        if (!ReferenceEquals(leader.Map, player.Map) || Distance(player, leader) <= 5f)
        {
            Hold(player);
            return;
        }

        Vector3 point = Party.PlayerbotParty.FollowPoint(Position(leader), leader.Orientation, _followAngle, _followDistance);
        if (!WalkTo(player, point, interval, toward: Distance(player, leader) > 60f, what: "follow", chase: true)
            && !WalkTo(player, Position(leader), interval, toward: true, what: "follow", chase: true)
            && OwnFlagRoom(player.Team) is { } ownRoom)
        {
            WalkTo(player, ownRoom, interval, toward: true, what: "follow");
        }
    }

    /// <summary>Stay within <paramref name="radius"/> (<see cref="GuardYards"/> by default) of <paramref name="at"/>.</summary>
    private void Guard(Player player, Vector3 at, uint interval, float radius = GuardYards)
    {
        if (Vector3.Distance(Position(player), at) <= radius) Hold(player);
        else WalkTo(player, at, interval, toward: true);
    }

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) of this bot's plans.</summary>
    private TileWait _tileWait;

    private void Hold(Player player)
    {
        _route = null;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
    }

    /// <summary>Walk towards <paramref name="goal"/>, planning again when it moved more than 2 yards since the last plan.</summary>
    /// <param name="toward">A far goal: a partial route towards it is fine (<see cref="PlayerbotNavigation.TryPlanToward"/>).</param>
    /// <param name="what">What the goal is, for the refusal's report (the walk to a battlemaster is no battleground objective).</param>
    /// <param name="chase">The goal is an enemy the bot closes on (<see cref="PlayerbotRoute.Chase"/>), planned as the brain plans its chase:
    /// the loop watch does not count its ends. Without it a fight in the bot's own flag room between two walks to its objective (the enemy
    /// stand, the middle) read as a back-and-forth (A, B, A) and the loop watch refused the objectives for a minute: three of four bots stood
    /// beside the Alliance flag room for 60 s with every route to the enemy flag and the middle refused.</param>
    private bool WalkTo(Player player, Vector3 goal, uint interval, bool toward, string what = "battleground objective", bool chase = false)
    {
        if (Vector3.Distance(Position(player), goal) <= 1.5f)
        {
            Hold(player);
            return true;
        }

        if (_route is null || _route.Complete || Vector3.Distance(_routeGoal, goal) > 2f)
        {
            PlanOutcome planned = toward
                ? PlayerbotNavigation.PlanToward(player, goal, _options, ref _tileWait, _session.World.NowMs, out PlayerbotRoute? plannedRoute,
                    watchPolicy: PlayerbotRoutePlanWatch.Policy.Duty)
                : PlayerbotNavigation.PlanExact(player, goal, _options, ref _tileWait, _session.World.NowMs, out plannedRoute, chase: chase);
            _route = plannedRoute;
            // A refused goal off the mesh (a flag room's corner, the middle's stump) is tried again at the last point of the mesh on the
            // straight line to it (Groups.PlayerbotGroupCoordinator.SnapToMesh) before the bot stands still.
            if (planned == PlanOutcome.Refused && !chase && SnappedGoal(player, goal) is { } snapped)
            {
                planned = toward
                    ? PlayerbotNavigation.PlanToward(player, snapped, _options, ref _tileWait, _session.World.NowMs, out plannedRoute,
                        watchPolicy: PlayerbotRoutePlanWatch.Policy.Duty)
                    : PlayerbotNavigation.PlanExact(player, snapped, _options, ref _tileWait, _session.World.NowMs, out plannedRoute, chase: chase);
                _route = plannedRoute;
            }

            if (planned != PlanOutcome.Route || _route is null)
            {
                _route = null;
                PlayerbotMovementControl.Stop(_session, player);
                // A tile of the way is still being read: the goal is kept and planned to again next think, not reported unreachable.
                // Once per (goal, origin) cell pair, not once per match: a refusal that goes on from the same spot adds nothing.
                if (planned != PlanOutcome.Pending && !chase && what is not ("buff" or "battlemaster" or "follow" or "carrier") && Activity != "taking-buff")
                    ReportWalkRefused(player, goal, $"route to {what} at {goal} was refused");
                return planned == PlanOutcome.Pending;
            }

            _routeGoal = goal;
        }

        if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, Math.Max(1u, interval), _session.World.NowMs))
        {
            _route = null;
            PlanOutcome replanned = toward
                ? PlayerbotNavigation.PlanToward(player, goal, _options, ref _tileWait, _session.World.NowMs, out PlayerbotRoute? replannedRoute,
                    watchPolicy: PlayerbotRoutePlanWatch.Policy.Duty)
                : PlayerbotNavigation.PlanExact(player, goal, _options, ref _tileWait, _session.World.NowMs, out replannedRoute, chase: chase);
            _route = replannedRoute;
            if (replanned != PlanOutcome.Route || _route is null)
            {
                _route = null;
                return replanned == PlanOutcome.Pending;
            }

            _routeGoal = goal;
            if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, Math.Max(1u, interval), _session.World.NowMs))
            {
                _route = null;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The movement acknowledgements first (every tick), then one decision every <see cref="PlayerbotOptions.ThinkIntervalMs"/>
    /// (the brain's pace). False when this tick makes no decision.
    /// </summary>
    private bool Think(Player player, uint elapsedMs, out uint interval)
    {
        interval = 0;
        if (PlayerbotMovementControl.Update(_session, player)) return false;
        _thinkElapsed = unchecked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs) return false;
        interval = _thinkElapsed;
        _thinkElapsed = 0;
        if (PlayerbotMotion.ConsumeLoop(player)) _route = null;
        return true;
    }

    private bool Casting(Player player)
        => _session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting };

    private Player? Online(ObjectGuid guid) => guid.IsEmpty ? null : _session.World.FindOnlinePlayer(guid);

    /// <summary>
    /// Run one client opcode through its world handler. <paramref name="budgeted"/> actions draw on the shared per-tick budget like the
    /// brain's; the others are a client's own answers and never wait.
    /// </summary>
    private bool Act(WorldOpcode opcode, byte[] payload, bool budgeted = true)
        => Groups.PlayerbotGroupCoordinator.Act(_session, opcode, payload, budgeted);

    private static Vector3 Position(WorldObject o) => PlayerbotGeometry.MotionPosition(o);

    private static float Distance(WorldObject a, WorldObject b) => PlayerbotGeometry.MotionDistance(a, b);
}
