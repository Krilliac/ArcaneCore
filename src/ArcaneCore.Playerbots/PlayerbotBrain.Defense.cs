using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Combat;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Defense calls from the responder's side (<c>World:Playerbots:Pvp:Defense</c>, design war-mode-defense sections 1.4 and 1.5; the call board is
/// <see cref="PlayerbotDefenseBoard"/>, the choosing lane is <c>PlayerbotDefenseLane</c>). The lane assigns a free bot with <see cref="RequestDefense"/>; the bot's own
/// think then answers: <see cref="TryRespond"/> walks it to the call's centre with a private walk-only return (<see cref="PlayerbotHomeReturn.WalkOnly"/>: no
/// <c>Judge</c>, teleport, taxi, tram or lift), takes on the nearest attacker in sight within <see cref="DefenseEngageYards"/> of the centre
/// (<see cref="PlayerbotEngageReason.Defense"/>) and, with nobody left, lingers <see cref="DefenseLingerMs"/> and goes back to its own affairs.
/// <para>
/// The bot is "responding" by call id only; its goal stays <see cref="PlayerbotGoalKind.Home"/> (no new goal kind: the goal list is a frozen schema). It drops the call
/// when the call closes, defense is switched off, it changes map, the call's centre is more than <see cref="DefenseDropYards"/> away, the travel limit passes, the
/// walk is blocked (then it also stays out of defense for <see cref="DefenseBackoffMs"/>), it stalls, dies (a <see cref="DefenseDeathCooldownMs"/> cooldown) or stops.
/// Reinforcements ignore the initiate rule's outnumbered check while they answer a call (a deliberate design choice: the call is the reason to fight).
/// </para>
/// World thread, except <see cref="ReleaseDefenseCall"/> which <see cref="Stop"/> posts to it. Memory only.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>A responder takes on a player within this many yards of the call's centre.</summary>
    internal const float DefenseEngageYards = 40f;

    /// <summary>The walk to a call ends inside this radius around the centre.</summary>
    internal const float DefenseArrivalYards = 30f;

    /// <summary>A responder at the centre with nobody to fight waits this long, then drops the call (milliseconds).</summary>
    internal const uint DefenseLingerMs = 15_000;

    /// <summary>A responder that has not arrived this long after it was assigned drops the call (milliseconds; the design's grace plus 180 s of travel slack).</summary>
    internal const uint DefenseTravelLimitMs = 180_000;

    /// <summary>A responder whose walk is blocked stays out of defense for this long (milliseconds, about 5 minutes).</summary>
    internal const uint DefenseBackoffMs = 300_000;

    /// <summary>A bot that died is not reselected for this long (milliseconds).</summary>
    internal const uint DefenseDeathCooldownMs = 120_000;

    /// <summary>A responder farther than this from the call's centre drops it (yards).</summary>
    internal const float DefenseDropYards = 350f;

    /// <summary>A call whose centre moved more than this makes the responder plan its walk again (yards).</summary>
    internal const float DefenseRetargetYards = 15f;

    private int _defenseCallId;
    private ulong _defenseGuid;
    private DefenseSide _defenseSide;
    private uint _defenseSince;
    private bool _defenseArrived;
    private uint _defenseArrivedAt;
    private bool _defenseWalking;
    private bool _defenseHeld;
    private uint _defenseHoldUntil;
    private PlayerbotHomeArea? _defenseArea;
    private readonly PlayerbotHomeReturn _defenseWalk = new() { WalkOnly = true };

    /// <summary>True while the bot answers a defense call (the help lane's <c>IsFree</c> and <see cref="RequestDefense"/> read it).</summary>
    internal bool Responding => _defenseCallId != 0;

    /// <summary>The id of the call being answered, or 0.</summary>
    internal int DefenseCallId => _defenseCallId;

    /// <summary>True once the responder stands inside <see cref="DefenseArrivalYards"/> of the call's centre (the lane's joiner cap counts only defenders that have arrived).</summary>
    internal bool DefenseArrived => _defenseCallId != 0 && _defenseArrived;

    /// <summary>
    /// The lane asks the bot to answer call <paramref name="callId"/> as <paramref name="side"/>. False when it already answers one, has a help request pending
    /// (help and defense are exclusive), is stopped, sits out a backoff or a death cooldown, or has no player in the world. The lane has judged the bot free and eligible;
    /// the think checks the facts again.
    /// </summary>
    internal bool RequestDefense(int callId, DefenseSide side)
    {
        if (callId <= 0 || _defenseCallId != 0 || _helpKind != PlayerbotHelpKind.None || Volatile.Read(ref _stopped) != 0) return false;
        if (_session.Player is not { IsAlive: true } player) return false;
        uint now = _session.World.NowMs;
        if (_defenseHeld && unchecked((int)(_defenseHoldUntil - now)) > 0) return false;
        _defenseHeld = false;
        _defenseCallId = callId;
        _defenseGuid = player.Guid.Value;
        _defenseSide = side;
        _defenseSince = now;
        _defenseArrived = false;
        _defenseWalking = false;
        _defenseArea = null;
        _defenseWalk.Reset();
        return true;
    }

    /// <summary>The lane could not record the assignment on the board (a side filled meanwhile): the bot forgets the request. No effect for another call id.</summary>
    internal void CancelDefense(int callId)
    {
        if (_defenseCallId != callId) return;
        _defenseCallId = 0;
        _defenseArrived = false;
        _defenseWalking = false;
        _defenseArea = null;
        _defenseWalk.Reset();
    }

    /// <summary>
    /// <see cref="Stop"/>'s release (world thread, posted when Stop runs elsewhere): the bot leaves the call's responders on the board so a logout never keeps a
    /// slot. Touches no world object: the guid was kept at the request.
    /// </summary>
    private void ReleaseDefenseCall()
    {
        int id = _defenseCallId;
        if (id == 0) return;
        DefenseCalls?.Release(id, _defenseGuid);
        _defenseCallId = 0;
        _defenseArrived = false;
        _defenseWalking = false;
        _defenseArea = null;
        _defenseWalk.Reset();
    }

    /// <summary>The bot died: it leaves its call and is not reselected for <see cref="DefenseDeathCooldownMs"/>.</summary>
    private void NoteDefenseDeath()
    {
        ReleaseDefenseCall();
        _defenseHeld = true;
        _defenseHoldUntil = unchecked(_session.World.NowMs + DefenseDeathCooldownMs);
        // A dead bot's own pull toward a fight ends with it: the engagement of a defense is no ghost's business.
        if (_engagedReason == PlayerbotEngageReason.Defense) _engaged = null;
    }

    /// <summary>
    /// The bot gives its call up: released on the board, the walker and the route forgotten, the bot stopped when it was walking to the call. With
    /// <paramref name="holdMs"/> it also sits out defense for that long (a blocked walk).
    /// </summary>
    private void DropDefense(Player? player, uint holdMs)
    {
        bool walking = _defenseWalking;
        ReleaseDefenseCall();
        if (holdMs != 0)
        {
            _defenseHeld = true;
            _defenseHoldUntil = unchecked(_session.World.NowMs + holdMs);
        }

        if (walking && _engaged is null)
        {
            AbandonRoute();
            if (player is { IsAlive: true } && (player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
        }
    }

    /// <summary>
    /// One think of a responder; true when it owns the think. Order: validity (the call is open, defense is on, same map, not too far, not past the travel limit),
    /// a creature that attacks the bot is fought back, the nearest fair attacker is taken on, otherwise the bot walks to the centre, and at the centre with nobody
    /// to fight it lingers and then drops the call.
    /// </summary>
    internal bool TryRespond(Player player, uint interval)
    {
        if (_defenseCallId == 0) return false;
        PlayerbotThinkProbe.Phase("respond");
        uint now = _session.World.NowMs;
        if (!ValidCall(player, now, out DefenseCall call)) return false;

        // A creature on the bot is fought first (a player's attack was TryFightPlayer's, earlier in the think).
        if (player.Combat.IsInCombat)
        {
            if (FindDefensiveAttacker(player) is not { } foe) return false;
            _stall.Reset();
            FightUnit(player, foe, interval);
            return true;
        }

        if (PickDefenseTarget(player, call) is { } target)
        {
            _defenseWalking = false;
            Engage(target, PlayerbotEngageReason.Defense);
            FightUnit(player, target, interval);
            return true;
        }

        PlayerbotHomeArea area = DefenseAreaOf(call);
        PlayerbotHomeState state = _defenseWalk.Evaluate(player, area, now);
        switch (state)
        {
            case PlayerbotHomeState.Inside:
                if (!_defenseArrived)
                {
                    _defenseArrived = true;
                    _defenseArrivedAt = now;
                }

                if (unchecked(now - _defenseArrivedAt) >= DefenseLingerMs)
                {
                    DropDefense(player, 0);
                    return false;
                }

                // Hold at the centre: rest, scan again next think.
                _stall.Reset();
                _goal = PlayerbotGoalKind.Home;
                TargetEntry = 0;
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                return true;

            case PlayerbotHomeState.Returning:
                _defenseArrived = false;
                if (!_defenseWalking)
                {
                    DropIntent();
                    _defenseWalking = true;
                }

                if (!_defenseWalk.Update(_session, player, area, _options, interval, now)) return false;
                _goal = PlayerbotGoalKind.Home;
                return true;

            default:
                // Blocked (the walk found no way) or off the call's map: the call is dropped and the bot stays out of defense for a while.
                DropDefense(player, DefenseBackoffMs);
                return false;
        }
    }

    /// <summary>
    /// The call the bot answers, or false after dropping it: the call is gone or closed (an absorbed call is followed to its survivor), defense is off, the bot changed
    /// map, the centre is more than <see cref="DefenseDropYards"/> away, or the bot has not arrived within <see cref="DefenseTravelLimitMs"/>.
    /// </summary>
    private bool ValidCall(Player player, uint now, out DefenseCall call)
    {
        call = null!;
        if (DefenseCalls is not { } board || !_options.Pvp.Defense.Enabled || player.Map is not { } map)
        {
            DropDefense(player, 0);
            return false;
        }

        DefenseCall? found = board.Find(_defenseCallId);
        if (found is null || !found.IsOpen(board.NowMs) || player.MapId != found.MapId || map.InstanceId != found.InstanceId
            || Vector2.Distance(new Vector2(player.X, player.Y), new Vector2(found.Centre.X, found.Centre.Y)) > DefenseDropYards
            || (!_defenseArrived && unchecked(now - _defenseSince) > DefenseTravelLimitMs))
        {
            DropDefense(player, 0);
            return false;
        }

        _defenseCallId = found.Id; // an absorbed call's responders follow the survivor
        call = found;
        return true;
    }

    /// <summary>The walk's target circle (cached; rebuilt, with the walker reset, when the centre moved more than <see cref="DefenseRetargetYards"/>).</summary>
    private PlayerbotHomeArea DefenseAreaOf(DefenseCall call)
    {
        // The cache is by position only: a call this one was merged into has another id, and a centre within the retarget distance needs no new walk.
        if (_defenseArea is { } area
            && Vector2.Distance(new Vector2(area.Centre.X, area.Centre.Y), new Vector2(call.Centre.X, call.Centre.Y)) <= DefenseRetargetYards) return area;
        if (_defenseArea is not null)
        {
            _defenseWalk.Reset();
            AbandonRoute();
        }

        return _defenseArea = new PlayerbotHomeArea(call.MapId, call.Centre, DefenseArrivalYards, "defense");
    }

    /// <summary>
    /// The nearest living player in sight (visible to the bot, so a stealthed one it cannot see is never picked), in line of sight, within
    /// <see cref="DefenseEngageYards"/> of the call's centre and <see cref="PvpPursuitYards"/> of the bot that this responder's side fights
    /// (<see cref="DefenseFoe"/>) and <see cref="PlayerbotHostility.MayEngage"/> allows for <see cref="PlayerbotEngageReason.Defense"/> (never into an exempt area).
    /// </summary>
    private Player? PickDefenseTarget(Player player, DefenseCall call)
    {
        if (player.Map is not { } map || player.VisibleObjects.Count == 0) return null;
        var war = _options.Social.WarMode;
        Vector2 centre = new(call.Centre.X, call.Centre.Y);
        Player? best = null;
        float bestYards = PvpPursuitYards;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (!guid.IsPlayer || guid == player.Guid) continue;
            if (_session.World.FindOnlinePlayer(guid) is not { IsAlive: true } other || !ReferenceEquals(other.Map, map)) continue;
            if (Vector2.Distance(new Vector2(other.X, other.Y), centre) > DefenseEngageYards) continue;
            float yards = Distance(player, other);
            if (yards > bestYards || (best is not null && yards >= bestYards)) continue;
            if (_pvpFight?.IsSkipped(other) == true || !DefenseFoe(player, other, call, map, war.Enabled)) continue;
            if (!PlayerbotHostility.MayEngage(player, other, PlayerbotEngageReason.Defense, null, war.SharedSwitch())) continue;
            if (!player.IsWithinLineOfSight(other)) continue;
            best = other;
            bestYards = yards;
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="other"/> is a foe of this responder's side. A defender fights an enemy-team player the call names or that attacks a caller-team player,
    /// and with War Mode off only one that struck first (<see cref="MapCombat.StruckFirst"/>, the help lane's rule: an enemy who only fights back is never chased).
    /// A joiner fights a caller-team player who is a caller or who attacks an attacker-team player.
    /// </summary>
    private bool DefenseFoe(Player player, Player other, DefenseCall call, Map map, bool warMode)
    {
        Player? victim = other.Combat.Victim as Player;
        if (_defenseSide == DefenseSide.Joiner)
        {
            if (other.Team != call.CallerTeam) return false;
            return call.Callers.ContainsKey(other.Guid.Value) || (victim is not null && victim.Team != call.CallerTeam);
        }

        if (other.Team == player.Team) return false;
        bool hitsCallerTeam = victim is not null && victim.Team == call.CallerTeam;
        if (!call.Attackers.Contains(other.Guid.Value) && !hitsCallerTeam) return false;
        if (warMode) return true;
        if (hitsCallerTeam && map.Combat.StruckFirst(other, victim!)) return true;
        foreach (ulong callerGuid in call.Callers.Keys)
        {
            if (_session.World.FindOnlinePlayer(new ObjectGuid(callerGuid)) is { } caller && ReferenceEquals(caller.Map, map) && map.Combat.StruckFirst(other, caller)) return true;
        }

        return false;
    }
}
