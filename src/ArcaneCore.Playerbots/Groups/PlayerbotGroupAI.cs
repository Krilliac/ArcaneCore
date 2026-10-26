using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// Drives one managed bot while it is a member of a bot-led group (<see cref="PlayerbotGroupCoordinator"/>), instead of
/// <see cref="PlayerbotBrain"/>. World thread only; one per member.
/// <para>
/// The leader takes the group where its goal is: it holds until the members have gathered, walks to the meeting point (the
/// objective's spawn, or the instance entrance) without leaving anyone more than <see cref="WaitForMembersYards"/> behind, walks
/// into the entrance (the members follow it through the same trigger, into the group's instance) and clears towards the objective:
/// whatever hostile creature stands within <see cref="ClearYards"/> of its way is pulled first. The members follow it at 2-5 yards
/// (the party bots' follow, vmangos PartyBotAI MoveFollow). While travelling, engaging or regrouping, one farther than the follow
/// teleport distance is brought to the leader (vmangos' 100-yard catch-up); a dungeon run that fights through still only walks.
/// They fight by role: the tank takes whatever attacks someone else (its
/// rotation taunts what turned away from it), the healer heals (its rotation's group heals) and fights when nobody needs healing, the
/// damage dealers assist the leader's target. Out of combat a member eats and drinks (the leader waits for it), resurrects a dead
/// member when its class can, and loots the corpses whose round-robin turn is its own. A dead member waits for a resurrection while a
/// living member could give one, then releases and runs back (<see cref="PlayerbotRecovery"/>, the ghost through the entrance when it
/// died inside). A wiping group retreats (<see cref="PlayerbotRetreat"/>) and regroups.
/// </para>
/// </summary>
internal sealed class PlayerbotGroupAI
{
    /// <summary>The leader waits while a living member on its map is farther than this.</summary>
    internal const float WaitForMembersYards = 30f;

    /// <summary>A hostile creature this close to the leader's way is cleared before the leader walks on.</summary>
    internal const float ClearYards = 20f;

    /// <summary>A member assists another member's attacker within this distance (vmangos PartyBotAI SelectPartyAttackTarget).</summary>
    internal const float AssistYards = 50f;

    /// <summary>A healer walks to a dead member to resurrect it from this far (the resurrection spells' 30 yards, less a margin).</summary>
    internal const float ResurrectYards = 25f;

    /// <summary>Round-robin loot the bot holds is taken from corpses within this distance (the group reward distance, vmangos 74).</summary>
    internal const float LootYards = 40f;

    /// <summary>Health or mana below this percentage: eat or drink between fights; the leader does not pull meanwhile.</summary>
    internal const uint RestBelowPct = 50;

    /// <summary>The player resurrection spells (vmangos IsResurrectionSpell; classic names, any rank).</summary>
    internal static readonly string[] ResurrectionSpells = ["Resurrection", "Redemption", "Ancestral Spirit", "Rebirth"];

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotCombatSpells _spells;
    private readonly PlayerbotKite _kite;
    private readonly PlayerbotEquipment _equipment;
    private readonly PlayerbotRecovery _recovery;
    private readonly PlayerbotRisk _risk;
    private readonly Random _random;
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
    private Unit? _target;
    private bool _attacking;
    private uint _thinkElapsed;
    private uint _restUntilMs;
    private PlayerbotConsumableKind _restKind;
    private long _deadSinceMs = -1;
    private bool _deathRetired;
    private ObjectGuid _lootOpened;
    private long _lootOpenedMs;
    private readonly Queue<(WorldOpcode Opcode, byte[] Payload)> _lootActions = new();
    private readonly Dictionary<ObjectGuid, int> _lootTries = [];
    private readonly float _followAngle;
    private readonly float _followDistance;
    private bool _retreatStarted;
    /// <summary>
    /// Consecutive thinks the instance-exit walk found no path (or stood on an exit that did not fire); past <see cref="WipeExitGiveUp"/> a wiped
    /// bot falls back to the retreat, and a bot in the retry regroup falls back to the regroup's own think (it fights back where it stands, or
    /// with no target follows its leader), the count kept for the rest of that regroup.
    /// It restarts at a death (the bot is revived somewhere else) and whenever the group's phase changes (<see cref="_wipeExitPhase"/>), so a
    /// give-up holds for the wipe or the regroup it was reached in and no other.
    /// </summary>
    private int _wipeExitFails;

    /// <summary>
    /// The group state, and whether it was the retry regroup (<see cref="PlayerbotRunStep.Retry"/> while Gathering), at this bot's last live think;
    /// null before the first. A change restarts <see cref="_wipeExitFails"/>, even when the bot thought in none of the states in between (dead, or
    /// a regroup that went on before its next think).
    /// </summary>
    private (PlayerbotGroupState State, bool Retry)? _wipeExitPhase;
    internal const int WipeExitGiveUp = 5;

    /// <summary>The current <see cref="_wipeExitFails"/> count (tests and traces).</summary>
    internal int WipeExitFails => _wipeExitFails;
    private long _resurrectAtMs;
    private PlayerbotRoute? _runRoute;
    private long _runRouteAtMs;
    private long _stuckSinceMs, _stuckLastMs;

    /// <summary>The targets this bot could plan no way to, each for <see cref="UnreachableMemoryMs"/>: it does not choose them again meanwhile.</summary>
    private readonly PlayerbotUnreachableTargets _unreachable = new();

    /// <summary>This bot's group fight and whether it goes anywhere (<see cref="FightStallShunMs"/>).</summary>
    private readonly PlayerbotFightWatch _fightWatch = new();

    /// <summary>When a leader began to wait for a lagging member without a break (world ms; 0: it does not), and whether that wait was waived.</summary>
    private uint _lagSinceMs;

    private bool _lagWaived;

    /// <summary>The leader's walk to the approach point: the nearest it has been (yards), when it last got nearer, and when it last tried (world ms).</summary>
    private float _approachNearestYards;

    private uint _approachProgressMs, _approachTriedMs;

    /// <summary>The leader has started the loop watch afresh for the retreat it is walking (once per retreat, see <see cref="LeadSiege"/>).</summary>
    private bool _retreatFresh;

    /// <summary>When the leader last retried the walk of a retreat that found no path.</summary>
    private uint _retreatRetryMs;

    /// <summary>How often a leader whose retreat found no path tries the walk again.</summary>
    private const uint RetreatRetryMs = 3_000;
    private uint _rangedIdleMs;

    internal PlayerbotGroupAI(WorldSession session, PlayerbotOptions options, PlayerbotEquipment? equipment = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _spells = new PlayerbotCombatSpells(session);
        _kite = new PlayerbotKite(session, options, _spells);
        _equipment = equipment ?? new PlayerbotEquipment(session);
        _recovery = new PlayerbotRecovery(session, options);
        _risk = new PlayerbotRisk(session, options, _spells) { AvoidsStrongCreatures = false };
        _recovery.Hazards = _risk.HazardThreats;
        _random = new Random(session.AccountId);
        _followAngle = (float)(_random.NextDouble() * MathF.Tau);
        _followDistance = 2f + ((float)_random.NextDouble() * 2.5f);
    }

    /// <summary>What the bot is doing (persisted and reported as its goal).</summary>
    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Group;

    internal uint TargetEntry { get; private set; }

    /// <summary>
    /// The last unit the bot fought, kept when a fight is dropped because no way to the target could be planned (<see cref="Fight"/> clears
    /// <see cref="TargetUnit"/> then), so the group's stall watch can still give that target up. Empty once the bot has no target for any other
    /// reason (the target died or was given up), or was released or re-targeted.
    /// </summary>
    internal ObjectGuid LastTargetGuid { get; private set; }

    /// <summary>The entry of the creature <see cref="LastTargetGuid"/> names (0: a player, or none).</summary>
    internal uint LastTargetEntry { get; private set; }

    /// <summary>The creature the bot fights (inspection and tests); null while it fights a player.</summary>
    internal Creature? Target => _target as Creature;

    /// <summary>The unit the bot fights, a creature or a player (inspection and tests).</summary>
    internal Unit? TargetUnit => _target;

    /// <summary>What the bot did at its last think (inspection, the group stall line): <c>lead:hold-not-ready</c>, <c>fight:114:hold</c>, ...</summary>
    internal string Step { get; private set; } = "none";

    /// <summary>This AI's own risk (inspection, tests): it keeps no hazards of creatures beyond a solo bot's strength.</summary>
    internal PlayerbotRisk Risk => _risk;

    /// <summary>The bot's retreat (a wiping group).</summary>
    internal PlayerbotRetreat Retreat => _risk.Retreat;

    /// <summary>The bot's spells (its group role is set here).</summary>
    internal PlayerbotCombatSpells Spells => _spells;

    /// <summary>
    /// Share <paramref name="questId"/> with the group: CMSG_PUSHQUESTTOPARTY, the client's "Share Quest" (vmangos
    /// <c>HandlePushQuestToParty</c>: every member within 14 yards who can take it is offered it). True when the server took it.
    /// </summary>
    internal bool PushQuest(uint questId) => Act(WorldOpcode.CmsgPushquesttoparty, BitConverter.GetBytes(questId), budgeted: false);

    /// <summary>The bot's corpse run (inspection and tests).</summary>
    internal PlayerbotRecovery Recovery => _recovery;

    /// <summary>Resting (eating or drinking) now.</summary>
    internal bool Resting => _restUntilMs != 0;

    /// <summary>Loot packets queued and not yet sent (a pushing siege leader off its waypoint sends none).</summary>
    internal int QueuedLootActions => _lootActions.Count;

    /// <summary>Test seam: queues a loot packet as a looted corpse would (the world update thread).</summary>
    internal void QueueLootAction(WorldOpcode opcode, byte[] payload) => _lootActions.Enqueue((opcode, payload));

    /// <summary>Walking to a corpse, or opening and emptying its loot (a run waits for it after the last boss).</summary>
    internal bool Looting => _lootActions.Count > 0 || !_lootOpened.IsEmpty || Goal == PlayerbotGoalKind.Loot;

    /// <summary>The last resurrection cast (inspection and tests: the dead member's name).</summary>
    internal string? LastResurrection { get; private set; }

    /// <summary>Whether that cast was aimed at the member's corpse (it had released) rather than at its unreleased body.</summary>
    internal bool LastResurrectionAtCorpse { get; private set; }

    /// <summary>How long a dead member waits for a resurrection before it releases (a test seam, never configuration-bound).</summary>
    internal long DeadWaitMs { get; set; } = PlayerbotGroupCoordinator.DeadWaitMs;

    private long Now => (long)_session.World.Uptime.TotalMilliseconds;

    /// <summary>Drop every route, target and rest (the group let the bot go, or it changed its state).</summary>
    internal void Release(Player player)
    {
        if (_attacking || player.Combat.Victim is not null) StopAttacking(player);
        PlayerbotMovementControl.Stop(_session, player);
        if (_restUntilMs != 0) Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u), budgeted: false);
        _route = null;
        _target = null;
        _attacking = false;
        _restUntilMs = 0;
        _lootActions.Clear();
        _lootOpened = ObjectGuid.Empty;
        _spells.GroupRole = null;
        _spells.Reset();
        _kite.Reset(_session.Player);
        _recovery.Reset();
        ForgetFight();
        _unreachable.Clear();
        _lagSinceMs = 0;
        _lagWaived = false;
        _approachTriedMs = 0;
        _wipeExitFails = 0;
        _wipeExitPhase = null;
        if (_risk.Retreat.Active) _risk.Retreat.Stop("released");
    }

    /// <summary>
    /// The group's stall watch gave up the leader's target (<see cref="PlayerbotGroupCoordinator"/>, set aside for the group): stop
    /// fighting it and forget the way, so the next think chooses again.
    /// </summary>
    internal void Retarget(Player player)
    {
        if (_attacking || player.Combat.Victim is not null) StopAttacking(player);
        PlayerbotMovementControl.Stop(_session, player);
        _target = null;
        _attacking = false;
        _route = null;
        _runRoute = null;
        _rangedIdleMs = 0;
        _stuckSinceMs = _stuckLastMs = 0;
        ForgetFight();
    }

    /// <summary>
    /// Whether a fight ends the lift ride of a member walking to the stop (<see cref="ElevatorStage.ToStop"/>). It does, except for the members of a
    /// siege raid that pushes through its fight (<see cref="PlayerbotSiegeRaid.PushingThrough"/>): they ride on.
    /// </summary>
    internal static bool CombatEndsRide(PlayerbotSiegeRaid? raid) => raid is not { PushingThrough: true };

    /// <summary>One tick of a member (world thread, inside the shared action budget like the brain).</summary>
    internal void Update(Player player, PlayerbotGroupCoordinator.BotGroup group, PlayerbotGroupCoordinator.Member me, uint elapsedMs)
    {
        if (!player.IsInWorld)
        {
            PlayerbotMovementControl.Update(_session, player);
            return;
        }

        _spells.GroupRole = PlayerbotGroupContent.CombatRole(me.Role, me.TalentRole);
        bool dead = !player.IsAlive;
        if (dead) _fightWatch.Reset();
        if (dead && !_deathRetired)
        {
            _risk.OnDeath(player);
            _target = null;
            _attacking = false;
            _route = null;
            _restUntilMs = 0;
            _lootActions.Clear();
            _spells.Reset();
            _kite.Reset(_session.Player);
            ForgetFight();
            _wipeExitFails = 0; // revived, it stands somewhere else (the landing): a give-up on the exit from where it died does not hold there
            _deathRetired = true;
        }

        if (PlayerbotMovementControl.Update(_session, player)) return;
        _thinkElapsed = unchecked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs) return;
        uint interval = _thinkElapsed;
        _thinkElapsed = 0;
        if (PlayerbotMotion.ConsumeLoop(player)) _route = null;

        if (dead)
        {
            Step = "dead";
            UpdateDead(player, group, interval);
            return;
        }

        _deathRetired = false;
        _deadSinceMs = -1;
        _recovery.Reset();

        // A lift ride (PlayerbotElevator) owns the bot until it stands on the other level, as it owns a brain's: a fight on the way to the
        // landing ends it. A failed ride is not fatal: the walk below plans again, and the lift's backoff keeps it off that lift for a while.
        // A raid pushing through its fight (PlayerbotSiegeRaid.PushingThrough) rides all the same: its leader walks on in combat, and the cancel
        // would end the ride at the landing every think, the next walk beginning it again, for as long as the raid is in combat.
        if (player.Combat.IsInCombat && CombatEndsRide(group.Siege) && PlayerbotElevator.TripOf(player) is { Stage: ElevatorStage.ToStop })
            PlayerbotElevator.Cancel(player);
        ElevatorStep lift = PlayerbotElevator.Drive(_session, player, _options, interval, out string? liftFailure);
        if (lift == ElevatorStep.Traveling)
        {
            Step = "lift";
            _target = null;
            _attacking = false;
            _route = null;
            return;
        }

        if (lift == ElevatorStep.Failed) Step = "lift-failed:" + liftFailure;
        _risk.Track(player);
        PlayerbotNavigation.Guard(player, null); // a group goes where its leader goes; the leader chose the way

        // A wiped group inside its dungeon leaves through the exit: instanceable maps have no soft leash (CreatureMapSystem.IsOutOfThreatArea is
        // false there, as in vmangos), so trash that is still alive keeps its target for good and a leash-based retreat only gets out if its
        // straight line happens to cross the exit trigger. Checked every think, so a member revived at the landing and pulled again leaves too.
        // The same holds once the run is on its Retry step and the group regroups (Gathering): the leader walks out to regroup at the entrance
        // (Lead), and a member revived at the landing that the surviving trash engages (it attacks the unit it hits, vmangos
        // Creature::OnEnterCombat -> AttackedBy) leaves through the exit too rather than fighting it at half health with no wipe check running.
        // Past WipeExitGiveUp it goes on as it would have without an exit: it fights back where it stands (SelectTarget), or follows its leader.
        bool retryRegroup = group.State == PlayerbotGroupState.Gathering && group.Run is { Step: PlayerbotRunStep.Retry };
        // A give-up holds for the phase it was reached in: one reached while wiped does not carry into the retry regroup (a survivor that found
        // no way out then may well have one after its retreat), and the next wipe starts afresh.
        if (_wipeExitPhase != (group.State, retryRegroup))
        {
            _wipeExitPhase = (group.State, retryRegroup);
            _wipeExitFails = 0;
        }

        if ((group.State == PlayerbotGroupState.Wiped || retryRegroup) && player.Combat.IsInCombat && player.MapId == group.Goal.MapId
            && WorldMaps.Of(_session.World).Registry.Find(player.MapId) is { IsDungeon: true }
            && _wipeExitFails < WipeExitGiveUp
            && PlayerbotGroupCoordinator.ExitOf(_session.World, player.MapId) is { } wipeExit)
        {
            if (_attacking || player.Combat.Victim is not null) StopAttacking(player);
            _risk.Retreat.Stop("instance-exit");
            _target = null;
            _attacking = false;
            Goal = PlayerbotGoalKind.Retreat;
            var exitPoint = new Vector3(wipeExit.X, wipeExit.Y, wipeExit.Z);
            bool walking = WalkTo(player, exitPoint, interval, toward: true);
            // Standing on the exit point and still in the instance means its trigger did not fire: that counts as a failure too, or the hold would reset the give-up for good.
            bool atExit = Vector3.Distance(new Vector3(player.X, player.Y, player.Z), exitPoint) <= 1.5f;
            _wipeExitFails = walking && !atExit ? 0 : _wipeExitFails + 1;
            Step = retryRegroup ? walking ? "retry:run-exit" : "retry:no-path-exit" : walking ? "wipe:run-exit" : "wipe:no-path-exit";
            return;
        }

        // A wiping group gets out: back the way it came, past the creatures' leash (the solo retreat). In an instance with an exit the block
        // above takes the bot out instead; this is the open world's way (and an instance with no exit trigger).
        if (_risk.UpdateRetreat(player, interval))
        {
            Step = "retreat";
            Goal = PlayerbotGoalKind.Retreat;
            return;
        }

        if (group.State == PlayerbotGroupState.Wiped)
        {
            if (!_retreatStarted && player.Combat.IsInCombat && Enemies(player) is { Count: > 0 } enemies)
            {
                _retreatStarted = true;
                StopAttacking(player);
                _risk.StartRetreat(player, enemies, "group-wipe");
                Goal = PlayerbotGoalKind.Retreat;
                _risk.UpdateRetreat(player, interval);
                return;
            }
        }
        else
        {
            _retreatStarted = false;
        }

        Player? leader = group.LeaderPlayer(_session.World);
        bool isLeader = leader is not null && leader.Guid == player.Guid;
        // A pushing leader away from its waypoint walks: it does not rest, eat, loot or buff on the way (the push has its own no-progress bound, and a rest that is not ended stands the leader still until the push is withdrawn).
        bool pushWalks = isLeader && group.Siege is { PushingThrough: true, AtWaypoint: false };
        if (!pushWalks && _lootActions.TryPeek(out var next))
        {
            Step = "loot";
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            if (Act(next.Opcode, next.Payload)) _lootActions.Dequeue();
            return;
        }

        if (_restUntilMs != 0 && ContinueRest(player, pushWalks)) { Step = "rest"; return; }

        if (isLeader && group.Siege is { Order: PlayerbotSiegeOrder.Rally, RetreatNoPath: true } stood) RetryRetreatWalk(player, stood, interval);
        if (isLeader && group.Siege is null) TrackLag(player, group);
        else ClearLag();
        if (!isLeader && SitOutMark(player, group, interval)) return;
        Unit? target = group.State is PlayerbotGroupState.Wiped or PlayerbotGroupState.Forming ? null
            : SelectTarget(player, group, me, leader, isLeader);
        if (target is not null)
        {
            if (isLeader && group.Run is { } run && target is Creature boss) NoteBossFight(group, run, boss);
            PlayerbotSiegeRaid? waiting = isLeader && group.Siege is { PushingThrough: true, AtWaypoint: true } w ? w : null;
            if (waiting is not null)
            {
                waiting.AnswerRequestedMs = Math.Max(1u, _session.World.NowMs);
                waiting.AnswerRequestedLeader = player.Guid;
                // The chase out and the walk back are an A, B, A pattern to the loop watch; the waypoint's own refusal is lifted as LeadSiege does (only that place).
                if (PlayerbotMotion.IsBlacklisted(player, waiting.MoveTo)) PlayerbotMotion.Unblacklist(player, waiting.MoveTo);
            }
            // A rallying leader holds its retreat point: it fights back what reaches it there and chases nothing farther than RallyYards from
            // the point (the commander's own "at the rally point"), or the chase takes it off the point and the raid flaps back into a retreat.
            // A rally where the leader stands only because it found no path (RetreatNoPath) has no point to hold.
            PlayerbotSiegeRaid? rallying = isLeader && group.Siege is { Order: PlayerbotSiegeOrder.Rally, RetreatNoPath: false } rally ? rally : null;
            if (rallying is not null) Fight(player, target, interval, rallying.MoveTo, PlayerbotSiegeCommander.RallyYards, leader);
            else Fight(player, target, interval, waiting?.MoveTo, waiting is { AnswerSpent: true } ? PlayerbotSiegeCommander.WaypointYards : PlayerbotSiegeCommander.SiegeAnswerLeashYards, leader);
            if (_noPathToTarget) GiveUpUnreachable(group, target, isLeader);
            else if (group.Siege is null && target is Creature fought) WatchFight(player, group, fought);
            return;
        }

        if (_target is not null)
        {
            if (_attacking) StopAttacking(player);
            _target = null;
            _attacking = false;
            _route = null;
            LastTargetGuid = ObjectGuid.Empty;
            LastTargetEntry = 0;
        }

        _fightWatch.Reset();
        TargetEntry = 0;
        if (!player.Combat.IsInCombat && !pushWalks)
        {
            if (FinishLoot(player)) { Step = "loot"; return; }
            if (Resurrect(player, group)) { Step = "resurrect"; return; }
            if (LootHeld(player, interval)) { Step = "loot"; return; }
            if (_spells.UpdateOutOfCombat(player, interval)) { Step = "buff"; Goal = PlayerbotGoalKind.Group; return; }
            if (Rest(player)) { Step = "rest"; return; }
            UpdateEquipment(player);
        }

        if (isLeader) Lead(player, group, interval);
        else Follow(player, group, leader, interval);
    }

    // --- leading --------------------------------------------------------------------------------------------------------

    private void Lead(Player player, PlayerbotGroupCoordinator.BotGroup group, uint interval)
    {
        Goal = PlayerbotGoalKind.Group;
        Step = "lead:" + group.State.ToString().ToLowerInvariant();
        if (group.State is not (PlayerbotGroupState.Travelling or PlayerbotGroupState.Engaging)) _stuckSinceMs = _stuckLastMs = 0;
        switch (group.State)
        {
            case PlayerbotGroupState.Gathering when PlayerbotGroupCoordinator.RetryExitOf(_session.World, group, player) is { } retryExit:
                // A retry regroups at the entrance: a leader revived inside walks out rather than holding beside live trash with a half-health party
                // (Gathered is false while it is inside, so the group does not go on from there on its own; only the GatherCatchUpMs fallback in
                // CatchUpGathering can, 60 s on, when the leader found no path out). Members follow it on foot, and through Follow's exit
                // branch once it is on another map.
                if (WalkTo(player, new Vector3(retryExit.X, retryExit.Y, retryExit.Z), interval, toward: true)) Step = "lead:walk-exit";
                else { Step = "lead:no-path-exit"; Hold(player); }
                return;
            case PlayerbotGroupState.Forming:
            case PlayerbotGroupState.Gathering:
            case PlayerbotGroupState.Regrouping:
            case PlayerbotGroupState.Wiped:
            case PlayerbotGroupState.Disbanding:
                Hold(player);
                return;
            case PlayerbotGroupState.Travelling:
                if (player.MapId != group.Goal.MeetingMap) { Step = "lead:hold-map"; HoldRun(player); return; }
                if (MembersBehind(player, group)) { Step = "lead:hold-members"; HoldRun(player); return; }
                if (group.AnyResting(_session.World)) { Step = "lead:hold-resting"; HoldRun(player); return; }
                // The leader keeps out of known hazards. Not on the way back after a world-boss wipe: the hazard at the spot the raid died on lies between the leader and the rally, and the raid is going back to that fight on purpose (a refusal there ended the raid as no-path-to-approach).
                PlayerbotNavigation.Guard(player, _options.Risk.Enabled && group.WorldBoss is not { Wipes: > 0 } ? _risk : null);
                Vector3 approach = TravelPoint(group);
                bool approaching = WalkTo(player, approach, interval, toward: true);
                Step = approaching ? "lead:walk-approach" : "lead:no-path-approach";
                NoteRunProgress(group, approaching);
                NoteApproachProgress(player, group, approach);
                return;
            case PlayerbotGroupState.Entering:
                if (player.MapId == group.Goal.MapId) { Step = "lead:hold-inside"; Hold(player); return; }
                if (MembersBehind(player, group)) { Step = "lead:hold-members"; Hold(player); return; }
                Step = WalkTo(player, group.Goal.Meeting, interval, toward: true) ? "lead:walk-entrance" : "lead:no-path-entrance";
                return;
            case PlayerbotGroupState.Engaging:
                if (group.Siege is { } siegeRaid) { LeadSiege(player, siegeRaid, interval); return; }
                if (group.Goal.InInstance && player.MapId == group.Goal.MeetingMap)
                {
                    Step = WalkTo(player, group.Goal.Meeting, interval, toward: true) ? "lead:walk-entrance" : "lead:no-path-entrance";
                    return;
                }
                if (group.Run is { CurrentBoss: null }) { Step = "lead:hold-loot"; HoldRun(player); return; } // every boss is dead: the coordinator lets the loot settle
                if (group.WorldBoss is { KilledAtMs: not 0 }) { Step = "lead:hold-loot"; HoldRun(player); return; } // the boss is down: the coordinator lets the loot settle
                if (group.WorldBoss is { PullHeld: true }) { Step = "lead:hold-worldboss"; HoldRun(player); return; } // the pull waits for the players, or for the raid
                if (MembersBehind(player, group)) { Step = "lead:hold-members"; HoldRun(player); return; }
                if (group.AnyResting(_session.World)) { Step = "lead:hold-resting"; HoldRun(player); return; }
                if (!Ready(player)) { Step = "lead:hold-not-ready"; HoldRun(player); return; }
                if (group.Run is null && group.Handoff?.Sweep is { } sweep) { LeadClear(player, group, sweep, interval); return; }
                bool walked = WalkTo(player, ObjectivePoint(player, group), interval, toward: true);
                Step = walked ? "lead:walk-objective" : "lead:no-path-objective";
                NoteRunProgress(group, walked);
                return;
            case PlayerbotGroupState.Leaving:
                // Out of the instance already: wait outside for the others (on the continent the entrance would take it back in).
                if (WorldMaps.Of(_session.World).Registry.Find(player.MapId) is { IsDungeon: true }
                    && PlayerbotGroupCoordinator.ExitOf(_session.World, player.MapId) is { } exit)
                    Step = WalkTo(player, new Vector3(exit.X, exit.Y, exit.Z), interval, toward: true) ? "lead:walk-exit" : "lead:no-path-exit";
                else
                {
                    Step = "lead:hold-outside";
                    Hold(player);
                }

                return;
        }
    }

    /// <summary>
    /// The raid rallies where it stands only because its leader found no path to the retreat point: the leader tries the walk again every
    /// <see cref="RetreatRetryMs"/> (a lift ride that failed, a place the loop watch refused). When it plans, the flag clears and the raid
    /// retreats again; the time at the rally point restarts, once per latch (<see cref="PlayerbotSiegeRaid.EndNoPathRally"/>). Members fight back meanwhile (<see cref="SelectTarget"/>).
    /// </summary>
    private void RetryRetreatWalk(Player player, PlayerbotSiegeRaid raid, uint interval)
    {
        uint now = _session.World.NowMs;
        if (unchecked(now - _retreatRetryMs) < RetreatRetryMs) return;
        // A leader that stands at the real retreat point has nothing to retry.
        float dx = player.X - raid.MoveTo.X, dy = player.Y - raid.MoveTo.Y;
        if (MathF.Sqrt((dx * dx) + (dy * dy)) <= PlayerbotSiegeCommander.RallyYards) return;
        _retreatRetryMs = Math.Max(1u, now);
        if (!WalkTo(player, raid.MoveTo, interval, toward: true)) return;
        raid.EndNoPathRally();
    }

    /// <summary>
    /// A siege raid's leader does what the commander ordered (<see cref="PlayerbotSiegeCommander"/>): walks to <see cref="PlayerbotSiegeRaid.MoveTo"/>
    /// to advance or retreat (the leader of a retreat has no target, see <see cref="SelectTarget"/>), otherwise stands: a rally holds the retreat
    /// point (the pull and the fight are <see cref="SelectTarget"/>'s).
    /// </summary>
    private void LeadSiege(Player player, PlayerbotSiegeRaid raid, uint interval)
    {
        // The retreat point is the waypoint the raid walked to, then broke off the walk to pull: the loop watch reads the walk back as an A, B, A
        // pattern and refuses the place, and the retreat would stand and be hit for the whole blacklist. A retreat is a new errand (as the walk of a
        // revived bot is, PlayerbotMotion.ForgetLoops): the watch starts afresh once per retreat, so a retreat that loops is still caught.
        if (raid.Order == PlayerbotSiegeOrder.Retreat)
        {
            if (!_retreatFresh) PlayerbotMotion.ForgetLoops(player);
            _retreatFresh = true;
        }
        else if (raid.Order != PlayerbotSiegeOrder.Rally) _retreatFresh = false;

        if (raid.Order is PlayerbotSiegeOrder.Advance or PlayerbotSiegeOrder.Retreat)
        {
            // A pushing leader that was drawn off its waypoint and walked back has made the loop watch refuse the waypoint (an A, B, A pattern), and the walk would report no path until the blacklist ends. The push has its
            // own no-progress bound (PushStallMs), so the waypoint's own entry is lifted. Only that one: the other refused places, the loop pattern and the rejection count stay, so the guard against a walk that is
            // refused again and again still trips (ForgetLoops on every think would reset it each time).
            if (raid.PushingThrough && PlayerbotMotion.IsBlacklisted(player, raid.MoveTo)) PlayerbotMotion.Unblacklist(player, raid.MoveTo);
            bool walked = WalkTo(player, raid.MoveTo, interval, toward: true);
            raid.RetreatNoPath = !walked && raid.Order == PlayerbotSiegeOrder.Retreat;
            Step = string.Create(CultureInfo.InvariantCulture,
                $"lead:siege-{raid.Order.ToString().ToLowerInvariant()}{(raid.PushingThrough ? ":push" : string.Empty)}{(walked ? string.Empty : ":no-path")}");
            return;
        }

        Step = "lead:siege-" + raid.Order.ToString().ToLowerInvariant();
        HoldRun(player);
    }

    /// <summary>
    /// A handed-over group without a catalogue route (<see cref="PlayerbotClearList"/>): walk towards the next creature on the clear list, the
    /// group behind; <see cref="SelectTarget"/> pulls it once it is within <see cref="PlayerbotLeaderHandoff.PullYards"/>. One the leader fails
    /// to plan a way to for <see cref="RunStuckMs"/> is given up (it leaves the list).
    /// </summary>
    private void LeadClear(Player player, PlayerbotGroupCoordinator.BotGroup group, PlayerbotClearList sweep, uint interval)
    {
        if (PlayerbotGroupCoordinator.SweepTarget(group, player, _session.World.NowMs, creature => PlayerbotBrain.IsSomeoneElses(player, creature, Groups())) is not { } next)
        {
            Step = "lead:hold-cleared";
            HoldRun(player);
            return;
        }

        bool walked = WalkTo(player, new Vector3(next.X, next.Y, next.Z), interval, toward: true);
        Step = string.Create(CultureInfo.InvariantCulture, $"lead:{(walked ? "walk" : "no-path")}-clear:{next.Entry}");
        if (!NoteStuck(ref _stuckSinceMs, ref _stuckLastMs, walked, Now)) return;
        sweep.GiveUp(next.Guid.Value);
        _stuckSinceMs = _stuckLastMs = 0;
    }

    /// <summary>The leader holds still while the run waits (members behind, a rest, a wait for loot): no attempt to walk was made, so no stretch of failed planning goes on.</summary>
    private void HoldRun(Player player)
    {
        _stuckSinceMs = _stuckLastMs = 0;
        Hold(player);
    }

    /// <summary>Where the leader walks to before the content: the objective's spawn (stopping in reach of it) or the instance entrance.</summary>
    private static Vector3 TravelPoint(PlayerbotGroupCoordinator.BotGroup group) => group.Approach;

    /// <summary>Where the leader walks while engaging: the nearest visible objective creature, else the objective's spawn.</summary>
    private Vector3 ObjectivePoint(Player player, PlayerbotGroupCoordinator.BotGroup group)
    {
        // A world-boss raid walks to its cached boss (one FindObject), else to where the boss was last known: never a scan.
        if (group.WorldBoss is { } bossRaid) return !bossRaid.BossGuid.IsEmpty && player.Map?.FindObject(bossRaid.BossGuid) is Creature cached ? new Vector3(cached.X, cached.Y, cached.Z) : group.ObjectivePosition;
        if (FindObjectiveCreature(player, group) is { } creature) return new Vector3(creature.X, creature.Y, creature.Z);

        // A run's boss out of sight (it patrols away from its spawn): walk to where it is.
        if (group.Run is not null && PlayerbotGroupCoordinator.FindLivingOnMap(player, group.ObjectiveEntry) is { } patrolling)
            return new Vector3(patrolling.X, patrolling.Y, patrolling.Z);
        return group.ObjectivePosition;
    }

    /// <summary>How long the leader may fail to plan a way before the run gives up (no path to the entrance or the boss).</summary>
    internal const long RunStuckMs = 30_000;

    /// <summary>
    /// A leader reports that it could (or could not) plan a way; a way that stays unplanned for <see cref="RunStuckMs"/> ends the group: a run
    /// with <c>no-path-to-boss</c> or <c>no-path-to-entrance</c>, a quest group with <c>no-path-to-objective</c> or <c>no-path-to-approach</c>
    /// (<see cref="PlayerbotGroupCoordinator.BotGroup.RunFault"/>, read by the coordinator). A handed-over player's group (no run) has rules of its own.
    /// </summary>
    private void NoteRunProgress(PlayerbotGroupCoordinator.BotGroup group, bool planned)
    {
        if (group.Run is null && (group.Handoff is not null || group.Members.Any(m => m.Real))) return;
        if (!NoteStuck(ref _stuckSinceMs, ref _stuckLastMs, planned, Now)) return;
        bool engaging = group.State == PlayerbotGroupState.Engaging;
        group.RunFault ??= group.Run is null
            ? (engaging ? "no-path-to-objective" : "no-path-to-approach")
            : (engaging ? "no-path-to-boss" : "no-path-to-entrance");
    }

    /// <summary>
    /// The leader's walk to the approach point: the point counts as reached (<see cref="PlayerbotGroupCoordinator.BotGroup.ApproachReached"/>) when
    /// the leader plans a way and walks it but gets no nearer than <see cref="ApproachProgressYards"/> beyond its nearest for
    /// <see cref="ApproachNoProgressMs"/> (a root, a wall the mesh does not show, a route that circles): the group goes on to the content instead of
    /// waiting at a point nobody reaches. A way that cannot be planned is <see cref="NoteRunProgress"/>'s. A pause of more than
    /// <see cref="RunStuckGapMs"/> between two attempts (a hold, a rest, a fight) starts a new stretch.
    /// </summary>
    private void NoteApproachProgress(Player player, PlayerbotGroupCoordinator.BotGroup group, Vector3 approach)
    {
        uint now = _session.World.NowMs;
        float yards = Vector3.Distance(new Vector3(player.X, player.Y, player.Z), approach);
        if (_approachTriedMs == 0 || unchecked(now - _approachTriedMs) > RunStuckGapMs)
        {
            _approachNearestYards = yards;
            _approachProgressMs = now;
        }

        _approachTriedMs = Math.Max(1u, now);
        if (yards < _approachNearestYards - ApproachProgressYards)
        {
            _approachNearestYards = yards;
            _approachProgressMs = now;
            return;
        }

        uint idle = unchecked(now - _approachProgressMs);
        if (idle > ApproachNoProgressMs && idle <= int.MaxValue) group.ApproachReached = true;
    }

    /// <summary>How long a leader walks to the approach point without getting nearer before the point counts as reached.</summary>
    internal const uint ApproachNoProgressMs = 45_000;

    /// <summary>The distance the leader must close, beyond the nearest it has been, to count as progress toward the approach point (yards).</summary>
    internal const float ApproachProgressYards = 2f;

    /// <summary>
    /// A planning failure that comes after a pause longer than this (a rest, a fight, a wait for the group: the leader did not try to walk
    /// meanwhile) starts a new stretch: only time spent failing to plan a way counts towards <see cref="RunStuckMs"/>.
    /// </summary>
    internal const long RunStuckGapMs = 5_000;

    /// <summary>
    /// Account one walking attempt. <paramref name="since"/> is when the current unbroken stretch of failed attempts began and
    /// <paramref name="last"/> when the latest failed one was made (0: none). Returns true when the stretch has lasted more than
    /// <see cref="RunStuckMs"/>. A planned way ends the stretch; so does a gap of more than <see cref="RunStuckGapMs"/> between two failures.
    /// </summary>
    internal static bool NoteStuck(ref long since, ref long last, bool planned, long now)
    {
        if (planned)
        {
            since = 0;
            last = 0;
            return false;
        }

        if (since == 0 || now - last > RunStuckGapMs) since = now;
        last = now;
        return now - since > RunStuckMs;
    }

    private Creature? FindObjectiveCreature(Player player, PlayerbotGroupCoordinator.BotGroup group)
    {
        if (player.Map is not { } map || group.ObjectiveEntry == 0) return null;
        Creature? best = null;
        bool bestOtherLevel = false;
        float nearest = float.MaxValue;
        var here = new Vector3(player.X, player.Y, player.Z);
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature { IsAlive: true } creature || creature.Entry != group.ObjectiveEntry) continue;
            if (!map.Combat.Hooks.CanAttack(player, creature) || group.IsShunned(creature.Guid, _session.World.NowMs)) continue;
            if (group.WorldBoss is not null && PlayerbotBrain.IsSomeoneElses(player, creature, Groups())) continue; // a tap that lands between two scans still stops the pull
            float distance = Distance(player, creature);
            // The objective on the leader's own level first (a unit on the floor below is near in 3D and unreachable from here), then the nearest.
            bool otherLevel = PlayerbotTargetFilter.OnOtherLevel(new Vector3(creature.X, creature.Y, creature.Z), here);
            if (Better(otherLevel, distance, best is not null, bestOtherLevel, nearest)) { nearest = distance; bestOtherLevel = otherLevel; best = creature; }
        }

        return best;
    }

    /// <summary>
    /// Whether a candidate objective beats the best so far: one on the leader's level beats one on another level, whatever the distances;
    /// between two on the same side of that, the nearer wins. With no best yet (<paramref name="haveBest"/> false) any candidate does.
    /// </summary>
    internal static bool Better(bool candidateOtherLevel, float candidateYards, bool haveBest, bool bestOtherLevel, float bestYards)
    {
        if (!haveBest) return true;
        if (candidateOtherLevel != bestOtherLevel) return !candidateOtherLevel;
        return candidateYards < bestYards;
    }

    /// <summary>The leader waits for a living member on its map more than <see cref="WaitForMembersYards"/> behind, unless that wait was waived (<see cref="TrackLag"/>).</summary>
    private bool MembersBehind(Player leader, PlayerbotGroupCoordinator.BotGroup group) => !_lagWaived && AnyMemberBehind(leader, group);

    /// <summary>A living member on the leader's map lags more than <see cref="WaitForMembersYards"/> behind.</summary>
    private bool AnyMemberBehind(Player leader, PlayerbotGroupCoordinator.BotGroup group)
    {
        foreach (PlayerbotGroupCoordinator.Member member in group.Members)
            if (IsBehind(leader, group, member, out _)) return true;
        return false;
    }

    private bool IsBehind(Player leader, PlayerbotGroupCoordinator.BotGroup group, PlayerbotGroupCoordinator.Member member, out Player? other)
    {
        other = null;
        if (member.Guid == leader.Guid || !member.Joined || _session.World.FindOnlinePlayer(member.Guid) is not { IsAlive: true } found) return false;
        if (member.Real && group.Handoff is not null) return false; // the player who handed the lead over is never waited for
        if (!ReferenceEquals(found.Map, leader.Map)) return false; // between maps or still outside: the state machine waits for them
        if (Distance(leader, found) <= WaitForMembersYards) return false;
        other = found;
        return true;
    }

    /// <summary>How long a leader waits for a lagging member before it catches the member up (or waives the wait).</summary>
    internal const uint LagCatchUpMs = 30_000;

    /// <summary>
    /// The bound of the leader's wait for a lagging member. While a member is more than <see cref="WaitForMembersYards"/> behind, in a state where the
    /// leader holds for it (travelling, entering or engaging, out of a fight), the clock runs; past <see cref="LagCatchUpMs"/> each laggard is
    /// brought to the leader with the catch-up teleport the follow already has (<see cref="CatchUpNow"/>, retried while the teleport is pending),
    /// and when one cannot be (a run that only walks, teleports off, a real player, a refusing service) the wait is waived until the lag clears.
    /// </summary>
    private void TrackLag(Player leader, PlayerbotGroupCoordinator.BotGroup group)
    {
        uint now = _session.World.NowMs;
        if (group.State is not (PlayerbotGroupState.Travelling or PlayerbotGroupState.Entering or PlayerbotGroupState.Engaging)
            || leader.Combat.IsInCombat || group.InFight(now) || !AnyMemberBehind(leader, group))
        {
            ClearLag();
            return;
        }

        if (_lagSinceMs == 0) _lagSinceMs = Math.Max(1u, now);
        uint waited = unchecked(now - _lagSinceMs);
        if (_lagWaived || waited <= LagCatchUpMs || waited > int.MaxValue) return;
        bool unable = false;
        foreach (PlayerbotGroupCoordinator.Member member in group.Members)
        {
            if (!IsBehind(leader, group, member, out Player? laggard) || laggard is null) continue;
            if ((member.AI?.CatchUpNow(laggard, leader, group) ?? PlayerbotCatchUpResult.Unable) == PlayerbotCatchUpResult.Unable) unable = true;
        }

        if (unable) _lagWaived = true;
    }

    private void ClearLag()
    {
        _lagSinceMs = 0;
        _lagWaived = false;
    }

    /// <summary>The leader is fit to pull: health and mana at <see cref="PlayerbotRiskOptions.RecoverHealthPct"/> or more (it rests otherwise).</summary>
    private bool Ready(Player player) => !_options.Risk.Enabled || _risk.Recovered(player) || !CanRest(player);

    // --- following ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a group member teleports to a living leader on its map instead of walking (vmangos PartyBotAI's
    /// <c>!IsWithinDistInMap(pLeader, 100)</c>, <see cref="Party.PlayerbotParty.DecideFollow"/>). Travelling, engaging and regrouping
    /// only: gathering walks, because the leader holds until the members arrive. A dungeon run that fights through
    /// (<see cref="PlayerbotGroupTravelPolicy.FightThrough"/>) never teleports a laggard.
    /// </summary>
    internal static bool ShouldCatchUp(in PlayerbotGroupCatchUpFacts facts)
    {
        if (facts.FightThroughRun || facts.State is not (PlayerbotGroupState.Travelling or PlayerbotGroupState.Engaging or PlayerbotGroupState.Regrouping))
            return false;
        return Party.PlayerbotParty.DecideFollow(new Party.PlayerbotFollowFacts(
            Party.PlayerbotPartyMode.Follow, MasterInWorld: true, SameMap: true, facts.Distance, facts.TeleportToLeader,
            MasterMapAllowed: true, MasterFlying: false, facts.InCombat, MasterInOtherInstance: false, facts.Mode, facts.TeleportDistance,
            facts.LeaderInCombat, facts.LeaderCombatRange, EntranceToMaster: false)) == Party.PlayerbotFollowAction.Teleport;
    }

    /// <summary>Land on the leader (the party follower's catch-up teleport). False when the teleport service refuses: the member walks.</summary>
    private bool TryCatchUp(Player player, Player leader) => Land(player, leader) == PlayerbotCatchUpResult.Done;

    /// <summary>
    /// Whether this bot may be teleported to its group's leader to end a wait for it: teleports to the leader are on and the bot is managed, and
    /// the group is not a dungeon run that fights through (which only walks, <see cref="ShouldCatchUp"/>).
    /// </summary>
    internal bool CanCatchUp(PlayerbotGroupCoordinator.BotGroup group)
        => _options.Party.TeleportToLeader && _session.IsManaged && !(group.Run is not null && !_options.Groups.DungeonRuns.TeleportToEntrance) && !WalksOnly(group);

    /// <summary>A world-boss raid walks to its rally unless <see cref="PlayerbotWorldBossOptions.CatchUpTeleport"/> lets the catch-up teleport a straggler.</summary>
    private bool WalksOnly(PlayerbotGroupCoordinator.BotGroup group) => group.WorldBoss is not null && !_options.Groups.WorldBosses.CatchUpTeleport;

    /// <summary>
    /// Bring this bot (<paramref name="player"/>) to the group's leader now, to end the wait for it (<see cref="TrackLag"/>, the gathering's bound):
    /// <see cref="PlayerbotCatchUpResult.Unable"/> when the group may not or the service refuses (the caller waives the wait),
    /// <see cref="PlayerbotCatchUpResult.Pending"/> when it is being teleported or the leader is in the air (try again next think).
    /// </summary>
    internal PlayerbotCatchUpResult CatchUpNow(Player player, Player leader, PlayerbotGroupCoordinator.BotGroup group)
        => CanCatchUp(group) ? Land(player, leader) : PlayerbotCatchUpResult.Unable;

    private PlayerbotCatchUpResult Land(Player player, Player leader)
    {
        TeleportService? teleports;
        try
        {
            teleports = _session.Services.GetService<TeleportFeature>()?.Teleports;
        }
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            return PlayerbotCatchUpResult.Unable;
        }

        if (teleports is null || !_session.IsManaged || player.Duel is not null) return PlayerbotCatchUpResult.Unable;
        if (teleports.IsBeingTeleported(player) || teleports.IsBeingTeleported(leader)
            || (leader.Movement.Flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) != 0) return PlayerbotCatchUpResult.Pending;
        PlayerbotMovementControl.Stop(_session, player);
        if (!teleports.TeleportTo(player, leader.MapId, leader.X, leader.Y, leader.Z, leader.Orientation)) return PlayerbotCatchUpResult.Unable;
        _route = null;
        return PlayerbotCatchUpResult.Done;
    }

    private void Follow(Player player, PlayerbotGroupCoordinator.BotGroup group, Player? leader, uint interval)
    {
        Goal = PlayerbotGoalKind.Follow;
        Step = "follow";
        // Into the instance after the leader: walk into the entrance it took (its trigger brings the member into the group's instance).
        if (group.Goal.InInstance && player.MapId == group.Goal.MeetingMap
            && (leader is null || leader.MapId == group.Goal.MapId || !ReferenceEquals(leader.Map, player.Map))
            && (group.State is PlayerbotGroupState.Entering or PlayerbotGroupState.Engaging or PlayerbotGroupState.Regrouping
                || (group.State == PlayerbotGroupState.Gathering && leader is not null && leader.MapId == group.Goal.MapId && group.Run is not { Step: PlayerbotRunStep.Retry })))
        {
            Goal = PlayerbotGoalKind.Group;
            Step = WalkTo(player, group.Goal.Meeting, interval, toward: true) ? "follow:walk-entrance" : "follow:no-path-entrance";
            return;
        }

        // Out after the leader: walk to the instance's exit. A run retry can leave a survivor inside while the resurrected leader
        // is outside; the gathering step cannot finish until that survivor takes the exit trigger too.
        if ((group.State == PlayerbotGroupState.Leaving || group.Run is { Step: PlayerbotRunStep.Retry }
                && group.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)
            && player.MapId == group.Goal.MapId && (leader is null || !ReferenceEquals(leader.Map, player.Map))
            && PlayerbotGroupCoordinator.ExitOf(_session.World, player.MapId) is { } exit)
        {
            Goal = PlayerbotGoalKind.Group;
            Step = WalkTo(player, new Vector3(exit.X, exit.Y, exit.Z), interval, toward: true) ? "follow:walk-exit" : "follow:no-path-exit";
            return;
        }

        // A dead leader is waited for where the group is (its ghost walks to the graveyard and back; nobody follows it there).
        if (leader is null || !leader.IsInWorld || !leader.IsAlive || !ReferenceEquals(leader.Map, player.Map))
        {
            Step = "follow:hold-leader";
            Hold(player);
            return;
        }

        float distance = Distance(player, leader);
        if (distance <= 5f)
        {
            Step = "follow:hold-near";
            Hold(player);
            return;
        }

        // vmangos PartyBotAI teleports a follower farther than TeleportDistanceYards. Group follow only walked, so the distance
        // check never ran: a member 428 yards from its leader stayed on step "follow" for the whole left-behind budget (bot QA #989).
        // Gathering still walks (the leader holds until the members arrive on foot). A fight-through run never teleports.
        if (ShouldCatchUp(new PlayerbotGroupCatchUpFacts(group.State, distance, player.Combat.IsInCombat, leader.Combat.IsInCombat,
                _options.Party.TeleportToLeader && _session.IsManaged, _options.Follow.CatchUpMode, _options.Follow.TeleportDistanceYards,
                _options.Follow.LeaderCombatRangeYards, group.Run is not null && !_options.Groups.DungeonRuns.TeleportToEntrance || WalksOnly(group)))
            && TryCatchUp(player, leader))
        {
            Step = "follow:catch-up";
            return;
        }

        Vector3 point = Party.PlayerbotParty.FollowPoint(new Vector3(leader.X, leader.Y, leader.Z), leader.Orientation, _followAngle, _followDistance);
        bool walked = WalkTo(player, point, interval, toward: distance > 60f) || WalkTo(player, new Vector3(leader.X, leader.Y, leader.Z), interval, toward: true);
        Step = walked ? "follow:walk" : "follow:no-path";
    }

    // --- fighting -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What this member fights now, by role. Everyone first answers a creature (or a player it may fight back) attacking a member, the tank
    /// especially one that attacks someone else (its rotation taunts it); in a fight the group's raid marks come first (the member's
    /// <see cref="PlayerbotGroupCoordinator.Member.FocusIcon"/>, then the skull; vmangos PartyBotAI.cpp:366-377), then it assists the leader's
    /// victim; the leader also pulls the objective and what stands in the way while engaging. A siege raid that retreats fights nothing while it
    /// disengages (<see cref="PlayerbotSiegeOrder.Retreat"/>: null, so <see cref="Update"/> clears the target and reaches Lead or Follow) and, standing
    /// at its retreat point (<see cref="PlayerbotSiegeOrder.Rally"/>), fights back only what attacks it. The raid that pushes through its fight (<see cref="PlayerbotSiegeRaid.PushingThrough"/>) follows no marks and picks no new targets: its leader fights nothing away from its waypoint (it walks, its attackers in tow) and fights back at it; every member answers any attacker of any raid member within <see cref="AssistYards"/>, its own attacker first, then the nearest, and with none follows the leader.
    /// </summary>
    private Unit? SelectTarget(Player player, PlayerbotGroupCoordinator.BotGroup group, PlayerbotGroupCoordinator.Member me,
        Player? leader, bool isLeader)
    {
        if (player.Map is not { } map) return null;
        // A retreating siege raid disengages: it fights nothing and casts nothing (every cast-time spell stops the mover first), so the leader walks
        // to the retreat point and the members follow it.
        PlayerbotSiegeOrder? siegeOrder = group.Siege?.Order;
        if (siegeOrder == PlayerbotSiegeOrder.Retreat) return null;
        GroupManager? groups = Groups();
        // A target the group gave up (its stall watch: no progress against it) is not kept; one that attacks a member comes back below.
        Unit? current = _target is not null && IsValidTarget(player, _target) && !group.IsShunned(_target.Guid, _session.World.NowMs) && !Unreachable(_target) ? _target : null;
        var loose = new List<(Unit Unit, float Distance, bool OnSelf)>();
        foreach (PlayerbotGroupCoordinator.Member member in group.Members)
        {
            Player? other = member.Guid == player.Guid ? player : _session.World.FindOnlinePlayer(member.Guid);
            if (other is null || !ReferenceEquals(other.Map, map)) continue;
            foreach (Unit unit in other.Combat.Attackers)
            {
                // A player attacker is fought only when it provoked the bot or a member (the one hostility rule).
                bool fightable = unit is Creature creature ? IsValidTarget(player, creature)
                    : unit is Player attacker && PlayerbotHostility.MayEngage(player, attacker, PlayerbotEngageReason.GroupProvoked, groups);
                if (fightable && !Unreachable(unit) && Distance(player, unit) <= AssistYards)
                    loose.Add((unit, Distance(player, unit), ReferenceEquals(other, player)));
            }
        }

        // At the retreat point (Rally) the raid picks no new targets and follows no marks: it only fights back what attacks the member (or, for a healer, the raid member it heals), the member itself first, then the nearest.
        if (siegeOrder == PlayerbotSiegeOrder.Rally)
        {
            bool heals = me.Role == PlayerbotGroupRole.Healer;
            return loose.Where(l => l.OnSelf || heals).OrderBy(l => l.OnSelf ? 0 : 1).ThenBy(l => l.Distance).Select(l => l.Unit).FirstOrDefault();
        }

        // While the raid pushes through its fight there are no marks and no new targets. The leader walks: away from its waypoint it fights nothing (Update never reaches Lead while it has a target), with its attackers in
        // tow; at the waypoint, waiting, it fights back. Every member answers any attacker of any raid member within AssistYards (loose), its own attacker first, then the nearest, and with none follows.
        if (group.Siege is { PushingThrough: true } pushing)
        {
            if (isLeader && !pushing.AtWaypoint) return null;
            // The leader waiting at its waypoint picks no fights: it takes what is at the waypoint (within WaypointYards, 3D, so a unit on the other level of a lift top is not) and an attacker that is on it, as long as that attacker
            // is within SiegeAnswerLeashYards of the waypoint (a caster, a flagged player, a long-reach melee unit). Beyond the leash (a kiting attacker) it is not followed: the leader walks back to its waypoint. Fight closes on
            // an attacker the leader cannot hit from where it stands, within that leash only.
            if (isLeader)
                return loose.Where(l => Vector3.Distance(new Vector3(l.Unit.X, l.Unit.Y, l.Unit.Z), pushing.MoveTo) <= (l.OnSelf && !pushing.AnswerSpent ? PlayerbotSiegeCommander.SiegeAnswerLeashYards : PlayerbotSiegeCommander.WaypointYards))
                    .OrderBy(l => l.OnSelf ? 0 : 1).ThenBy(l => l.Distance).Select(l => l.Unit).FirstOrDefault();
            return loose.OrderBy(l => l.OnSelf ? 0 : 1).ThenBy(l => l.Distance).Select(l => l.Unit).FirstOrDefault();
        }

        // A siege leader that marches (an Advance that is no push) fights only what attacks it: the generic tank and non-tank rules below (peel a member's attacker, follow a mark, finish the target) would turn it
        // back from its goal for any fight of the raid. The members answer attackers as ever.
        if (isLeader && group.Siege is { Order: PlayerbotSiegeOrder.Advance })
            return loose.Where(l => l.OnSelf).OrderBy(l => l.Distance).Select(l => l.Unit).FirstOrDefault();

        Unit? marked = null;
        if (player.Combat.IsInCombat || group.InFight(_session.World.NowMs) || leader is not null && leader.Combat.Victim is not null)
            marked = MarkedTarget(player, group, me, map, groups);

        if (me.Role == PlayerbotGroupRole.Tank)
        {
            // Peel: a creature on someone else first (the nearest), then the mark, then the one being fought, then whatever attacks the tank.
            Unit? peel = loose.Where(l => !l.OnSelf && !ReferenceEquals(l.Unit.Combat.Victim, player))
                .OrderBy(l => l.Distance).Select(l => l.Unit).FirstOrDefault();
            if (peel is not null) return peel;
            if (marked is not null) return marked;
            if (current is not null && player.Combat.IsInCombat) return current;
            if (loose.Count > 0) return loose.OrderBy(l => l.Distance).First().Unit;
        }
        else
        {
            if (marked is not null) return marked;
            if (current is not null && player.Combat.IsInCombat && (!isLeader || current.Combat.IsInCombat)) return current;
            if (!isLeader && leader is not null && ReferenceEquals(leader.Map, map) && leader.Combat.Victim is { } victim
                && (victim is Creature || victim is Player victimPlayer && PlayerbotHostility.MayEngage(player, victimPlayer, PlayerbotEngageReason.GroupProvoked, groups))
                && IsValidTarget(player, victim) && !Unreachable(victim) && !ShunnedOutsideSiege(group, victim) && Distance(player, victim) <= AssistYards)
                return victim;
            if (loose.Count > 0) return loose.OrderBy(l => l.OnSelf ? 0 : 1).ThenBy(l => l.Distance).First().Unit;
        }

        // A siege leader opens on the skull's unit the commander named; nothing else is pulled.
        if (isLeader && group.Siege is { } siegeRaid)
        {
            if (siegeRaid.Order is PlayerbotSiegeOrder.Pull or PlayerbotSiegeOrder.AssignTank or PlayerbotSiegeOrder.Assault
                && !siegeRaid.PullTarget.IsEmpty && map.FindObject(siegeRaid.PullTarget) is Unit pulled && IsValidTarget(player, pulled)
                && (pulled is Creature pulledCreature && pulledCreature.Entry == siegeRaid.Siege.ObjectiveEntry || !group.IsShunned(pulled.Guid, _session.World.NowMs)))
                return pulled;
            return null;
        }

        // The leader pulls: the objective, or what stands in the way.
        if (isLeader && group.Run is not null && group.State == PlayerbotGroupState.Travelling && player.MapId == group.Goal.MeetingMap
            && !MembersBehind(player, group) && !group.AnyResting(_session.World) && Ready(player))
        {
            // A run fights its way to the entrance: what stands in the way is cleared by the group, never run past or teleported over.
            return current ?? FindObstacle(player, group, TravelPoint(group));
        }

        // A world-boss raid pulls only once its coordinator has released the pull, and not after the kill.
        if (isLeader && group.WorldBoss is { } held) { if (held.KilledAtMs != 0) return null; if (held.PullHeld) return current is Creature { } fought && fought.Entry == group.ObjectiveEntry ? null : current; } // held: only a fight already on us (an add) is kept, the boss is never picked
        if (isLeader && group.State == PlayerbotGroupState.Engaging && !MembersBehind(player, group) && !group.AnyResting(_session.World)
            && Ready(player))
        {
            if (group.Run is { } run) return SelectRunTarget(player, group, run, current as Creature);
            if (current is not null) return current;
            if (group.Handoff?.Sweep is not null)
            {
                // A handed-over group's clear list: the next creature once it is in reach (the leader walks to it meanwhile), else what is in the way.
                if (PlayerbotGroupCoordinator.SweepTarget(group, player, _session.World.NowMs, creature => PlayerbotBrain.IsSomeoneElses(player, creature, Groups())) is not { } next)
                    return null;
                if (IsValidTarget(player, next) && Distance(player, next) <= PlayerbotLeaderHandoff.PullYards)
                    return next;
                return FindObstacle(player, group, new Vector3(next.X, next.Y, next.Z));
            }

            if (FindObjectiveCreature(player, group) is { } objective && Distance(player, objective) <= 60f)
            {
                group.NoteObjective(objective);
                return objective;
            }

            return FindObstacle(player, group, ObjectivePoint(player, group));
        }

        return null;
    }

    /// <summary>
    /// The unit of the group's raid marks this member sticks to: its focus icon first, then the skull (<see cref="PlayerbotParty.MarkedTarget{T}"/>),
    /// within <see cref="PlayerbotParty.MarkRangeYards"/> and valid for it.
    /// </summary>
    private Unit? MarkedTarget(Player player, PlayerbotGroupCoordinator.BotGroup group, PlayerbotGroupCoordinator.Member me, Map map, GroupManager? groups)
    {
        if (groups?.GetGroup(player.Guid) is not { } server) return null;
        byte[] order = me.FocusIcon == PlayerbotParty.SkullIcon ? SkullOnly : [me.FocusIcon, PlayerbotParty.SkullIcon];
        return PlayerbotParty.MarkedTarget(server.TargetIcons, order, guid => map.FindObject(guid) as Unit,
            unit => Distance(player, unit) <= PlayerbotParty.MarkRangeYards && IsValidTarget(player, unit) && !Unreachable(unit) && !ShunnedOutsideSiege(group, unit));
    }

    private static readonly byte[] SkullOnly = [PlayerbotParty.SkullIcon];

    /// <summary>The bot gave up <paramref name="unit"/> as unreachable (<see cref="PlayerbotUnreachableTargets"/>, <see cref="UnreachableMemoryMs"/>): it does not choose it again meanwhile.</summary>
    private bool Unreachable(Unit unit) => _unreachable.Has(unit.Guid, _session.World.NowMs);

    /// <summary>The group gave <paramref name="unit"/> up (a stall, no way to it, a fight that went nowhere): a member does not assist or follow a mark onto it. A siege raid keeps its own rules (the commander names its skull).</summary>
    private bool ShunnedOutsideSiege(PlayerbotGroupCoordinator.BotGroup group, Unit unit) => group.Siege is null && group.IsShunned(unit.Guid, _session.World.NowMs);

    /// <summary>
    /// The leader fights a boss of the wing that is not the objective noted (one met on the way to an earlier boss, or one that came with
    /// a pull): its death is credited to the run, whichever boss the route has reached.
    /// </summary>
    internal static void NoteBossFight(PlayerbotGroupCoordinator.BotGroup group, PlayerbotRunMachine run, Creature target)
    {
        if (group.Objective.IsEmpty && !run.IsKilled(target.Entry) && run.Plan.IsBoss(target.Entry)) group.NoteObjective(target);
    }

    /// <summary>
    /// A run's leader inside the instance pulls one pack at a time along the way to the current boss
    /// (<see cref="PlayerbotRunRoute.NextPull"/>), the boss last once the way is clear and it is in reach.
    /// </summary>
    private Creature? SelectRunTarget(Player player, PlayerbotGroupCoordinator.BotGroup group, PlayerbotRunMachine run, Creature? current)
    {
        if (current is not null) return current;
        if (run.CurrentBoss is not { } boss || player.Map is not { } map) return null;
        IReadOnlyList<Vector3> points = RunRoutePoints(player, boss.Position);
        var seen = new List<PlayerbotRunCreature>();
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature || !IsValidTarget(player, creature) || !map.Combat.Hooks.IsHostileTo(creature, player)
                || creature.Template.NpcFlags != 0 || creature.Template.CreatureType == 8 || PlayerbotBrain.IsSomeoneElses(player, creature, Groups())) continue;
            seen.Add(new PlayerbotRunCreature(creature.Guid.Value, creature.Entry, new Vector3(creature.X, creature.Y, creature.Z), creature.IsAlive,
                Hostile: true, creature.Combat.IsInCombat));
        }

        PlayerbotRunPull? pull = PlayerbotRunRoute.NextPull(points, 0, seen, boss.Entry, run.Plan.BossEntries);
        run.PullsLeft = pull?.PacksAhead ?? 0;
        if (pull is not null && map.FindObject(new ObjectGuid(pull.Lead.Guid)) is Creature lead && !group.IsShunned(lead.Guid, _session.World.NowMs)) return lead;
        if (FindObjectiveCreature(player, group) is { } objective && Distance(player, objective) <= 60f)
        {
            group.NoteObjective(objective);
            return objective;
        }

        return null;
    }

    /// <summary>The path from the leader to the boss, planned again every few seconds (the points a pull is chosen along).</summary>
    private IReadOnlyList<Vector3> RunRoutePoints(Player player, Vector3 bossPosition)
    {
        long now = Now;
        if (_runRoute is null || now - _runRouteAtMs > 3_000)
        {
            _runRouteAtMs = now;
            // Pending or refused alike: no points yet, planned again in a few seconds (nothing is set aside on this plan).
            _runRoute = PlayerbotNavigation.PlanToward(player, bossPosition, _options, ref _tileWait, _session.World.NowMs,
                out PlayerbotRoute? planned) == PlanOutcome.Route ? planned : null;
        }

        return _runRoute?.Points ?? [];
    }

    /// <summary>
    /// Whether <paramref name="threat"/> is an obstacle on the leader's way from <paramref name="leader"/> to <paramref name="goal"/> (<see cref="FindObstacle"/>): a creature that is
    /// not a civilian (a city is full of them and they are left alone), stands on the leader's level (<see cref="PlayerbotTargetFilter.OnOtherLevel"/>), is within <see cref="ClearYards"/>
    /// of the leader, and whose aggro reach touches the route (the plane distance of the creature to the segment, <see cref="PlayerbotGroupCoordinator.RouteYards"/>, is within its
    /// reach): one that would attack the walk. Before, any hostile creature within 20 yards was cleared, a civilian or a unit on the floor below included.
    /// </summary>
    internal static bool CountsAsObstacle(in PlayerbotThreat threat, bool civilian, Vector3 leader, Vector3 goal)
    {
        if (civilian || PlayerbotTargetFilter.OnOtherLevel(threat.Position, leader) || Vector3.Distance(threat.Position, leader) > ClearYards) return false;
        return PlayerbotGroupCoordinator.RouteYards(threat.Position, leader, goal) <= threat.Radius;
    }

    /// <summary>The nearest hostile creature within <see cref="ClearYards"/> of the leader that would attack the group (the way is cleared).</summary>
    private Creature? FindObstacle(Player player, PlayerbotGroupCoordinator.BotGroup group, Vector3 goal)
    {
        if (player.Map is not { } map) return null;
        Creature? best = null;
        float nearest = ClearYards;
        var leaderAt = new Vector3(player.X, player.Y, player.Z);
        foreach (PlayerbotThreat threat in PlayerbotRecovery.Threats(player))
        {
            if (threat.Source is not { IsAlive: true } creature || !IsValidTarget(player, creature)) continue;
            if (PlayerbotBrain.IsSomeoneElses(player, creature, Groups()) || group.IsShunned(creature.Guid, _session.World.NowMs)) continue;
            if (!CountsAsObstacle(threat, PlayerbotTargetFilter.IsCivilian(creature), leaderAt, goal)) continue;
            float distance = Distance(player, creature);
            if (distance <= nearest)
            {
                nearest = distance;
                best = creature;
            }
        }

        return best;
    }

    /// <summary>How long a leader gives up a target it can plan no way to.</summary>
    internal const uint NoPathShunMs = 60_000;

    /// <summary>How long a siege leader gives up a target it can plan no way to.</summary>
    internal const uint SiegeShunMs = NoPathShunMs;

    /// <summary>How long a member remembers a target it could plan no way to (<see cref="PlayerbotUnreachableTargets"/>).</summary>
    internal const uint UnreachableMemoryMs = 30_000;

    /// <summary>How long a group gives up a creature whose fight went nowhere (<see cref="PlayerbotFightWatch"/>).</summary>
    internal const uint FightStallShunMs = 300_000;

    /// <summary>The last <see cref="Fight"/> could plan no way to its target.</summary>
    private bool _noPathToTarget;

    /// <param name="leashCentre">The waypoint of a waiting siege leader, or the retreat point of a rallying one: the player chases a target out of reach only while the target is within <paramref name="leashYards"/> of it, and otherwise stands (a cast, or a swing once the target is in reach).</param>
    /// <param name="leashYards">The leash radius: <see cref="PlayerbotSiegeCommander.SiegeAnswerLeashYards"/>, or the disk once the step's answer bound is spent; <see cref="PlayerbotSiegeCommander.RallyYards"/> at a rally.</param>
    private void Fight(Player player, Unit target, uint interval, Vector3? leashCentre = null, float leashYards = PlayerbotSiegeCommander.SiegeAnswerLeashYards,
        Player? leader = null)
    {
        _noPathToTarget = false;
        if (!ReferenceEquals(_target, target))
        {
            _target = target;
            _attacking = false;
            _route = null;
            _rangedIdleMs = 0;
        }

        Goal = PlayerbotGoalKind.Assist;
        uint targetEntry = target is Creature creatureTarget ? creatureTarget.Entry : 0;
        string targetLabel = target is Creature ? targetEntry.ToString(CultureInfo.InvariantCulture) : "player";
        TargetEntry = targetEntry;
        LastTargetGuid = target.Guid;
        LastTargetEntry = targetEntry;
        if (_restUntilMs != 0) { _restUntilMs = 0; Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u), budgeted: false); }

        // A hurt ranged bot kites within the leash of the siege waypoint or rally point, else of its leader.
        Vector3? kiteCentre = leashCentre ?? (leader is not null && leader.Guid != player.Guid && ReferenceEquals(leader.Map, player.Map)
            ? new Vector3(leader.X, leader.Y, leader.Z) : null);
        if (_kite.Update(player, target, interval, kiteCentre, leashCentre is null ? PlayerbotSiegeCommander.SiegeAnswerLeashYards : leashYards, _risk))
        {
            _route = null;
            _rangedIdleMs = 0;
            Step = string.Create(CultureInfo.InvariantCulture, $"fight:{targetLabel}:kite");
            return;
        }

        if (_spells.Update(player, target, interval))
        {
            _rangedIdleMs = 0;
            Step = string.Create(CultureInfo.InvariantCulture, $"fight:{targetLabel}:cast");
            return;
        }

        float distance = Distance(player, target);
        // A ranged member that stands at its range with nothing to cast (a healer leader without an attack spell, a caster out of mana)
        // while nobody fights the target closes in after RangedIdleLimitMs, as the brain does (PlayerbotBrain.Update). Before, the
        // group AI never did: a healer leading a pair stood 15-22 yards from the objective for good (replays 2026-10-11, quest 9).
        bool engaged = player.Combat.ThreatenedBy.Contains(target) || target.Combat.Victim is not null;
        PlayerbotFightPosition position = PlayerbotBrain.DecidePosition(_spells.PreferredRange(player), distance,
            player.Class == Class.Hunter, ReferenceEquals(target.Combat.Victim, player),
            idleTooLong: _rangedIdleMs >= PlayerbotBrain.RangedIdleLimitMs && !engaged,
            inMeleeReach: MapCombat.CanReachWithMeleeAutoAttack(player, target));
        Step = string.Create(CultureInfo.InvariantCulture, $"fight:{targetLabel}:{position.ToString().ToLowerInvariant()}:{distance:F0}yd");
        switch (position)
        {
            case PlayerbotFightPosition.Hold:
                _rangedIdleMs = engaged ? 0 : _rangedIdleMs + interval;
                _route = null;
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                return;
            case PlayerbotFightPosition.ChaseToRange or PlayerbotFightPosition.ChaseToMelee when leashCentre is { } centre
                && Vector3.Distance(new Vector3(target.X, target.Y, target.Z), centre) > leashYards:
                _route = null;
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                return;
            case PlayerbotFightPosition.ChaseToRange or PlayerbotFightPosition.ChaseToMelee:
                if (!WalkTo(player, new Vector3(target.X, target.Y, target.Z), interval, toward: false, chase: true))
                {
                    Step = string.Create(CultureInfo.InvariantCulture, $"fight:{targetLabel}:no-path");
                    _target = null;
                    _noPathToTarget = true;
                }

                return;
        }

        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            _route = null;
            return;
        }

        // The server refuses a swing outside the auto-attack arc; a managed player turns to its victim as the client does.
        if (MathF.Sqrt(MathF.Pow(target.X - player.X, 2) + MathF.Pow(target.Y - player.Y, 2)) > CombatConstants.NoFacingChecksDistance
            && !MapCombat.HasInArc(player, target, CombatConstants.AutoAttackArc))
        {
            PlayerbotMotion.Face(_session, player, MathF.Atan2(target.Y - player.Y, target.X - player.X));
            return;
        }

        if (!_attacking || !ReferenceEquals(player.Combat.Victim, target))
            _attacking = Act(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(target.Guid.Value));
    }

    private void StopAttacking(Player player)
    {
        if (player.Combat.Victim is not null) Act(WorldOpcode.CmsgAttackstop, [], budgeted: false);
        _attacking = false;
        _target = null;
        _route = null;
        TargetEntry = 0;
    }

    private void ForgetFight()
    {
        LastTargetGuid = ObjectGuid.Empty;
        LastTargetEntry = 0;
        _fightWatch.Reset();
    }

    /// <summary>
    /// <see cref="Fight"/> could plan no way to <paramref name="target"/>: a member remembers it for <see cref="UnreachableMemoryMs"/> so it
    /// does not choose it again at every think; a leader shuns it for the group for <see cref="NoPathShunMs"/> (never the siege objective, and
    /// never a run's boss, which counts toward <see cref="RunStuckMs"/> instead).
    /// </summary>
    private void GiveUpUnreachable(PlayerbotGroupCoordinator.BotGroup group, Unit target, bool isLeader)
    {
        _fightWatch.Reset();
        if (!isLeader)
        {
            _unreachable.Mark(target.Guid, _session.World.NowMs, UnreachableMemoryMs);
            return;
        }

        if (group.Siege is { } siegeRaid && target is Creature siegeCreature && siegeCreature.Entry == siegeRaid.Siege.ObjectiveEntry)
            return;
        if (group.Run is { } run && target is Creature boss && (run.CurrentBoss?.Entry == boss.Entry || run.Plan.IsBoss(boss.Entry)))
        {
            NoteRunProgress(group, planned: false);
            return;
        }

        group.Shun(target.Guid, unchecked(_session.World.NowMs + NoPathShunMs));
    }

    /// <summary>
    /// A non-siege fight against <paramref name="fought"/> that neither drops its health nor closes on it for <see cref="PlayerbotFightWatch.StallMs"/>
    /// is given up for the group for <see cref="FightStallShunMs"/>.
    /// </summary>
    private void WatchFight(Player player, PlayerbotGroupCoordinator.BotGroup group, Creature fought)
    {
        if (!_fightWatch.Observe(fought.Guid, fought.Health, Distance(player, fought), _session.World.NowMs)) return;
        group.Shun(fought.Guid, unchecked(_session.World.NowMs + FightStallShunMs));
        StopAttacking(player);
        PlayerbotMovementControl.Stop(_session, player);
        ForgetFight();
    }

    private static List<Creature> Enemies(Player player)
    {
        var enemies = new List<Creature>();
        foreach (Unit unit in player.Combat.Attackers.Concat(player.Combat.ThreatenedBy))
            if (unit is Creature creature && creature.IsAlive && ReferenceEquals(creature.Map, player.Map) && !enemies.Contains(creature))
                enemies.Add(creature);
        return enemies;
    }

    /// <summary>vmangos IsValidHostileTarget: in the world, alive, on the bot's map and attackable by it.</summary>
    private static bool IsValidTarget(Player player, Unit target)
        => target is Player enemy ? PlayerbotHostility.IsHostilePlayer(player, enemy) : target is Creature creature && IsValidTarget(player, creature);

    private static bool IsValidTarget(Player player, Creature target)
        => target.IsInWorld && target.IsAlive && player.Map is { } map && ReferenceEquals(target.Map, map) && !target.IsInEvadeMode
            && float.IsFinite(target.X) && float.IsFinite(target.Y) && float.IsFinite(target.Z) && map.Combat.Hooks.CanAttack(player, target);

    // --- between fights -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A healer resurrects a dead member it can see (the member's body, not yet released) through the ordinary cast; the member's
    /// party intake accepts a group member's offer (CMSG_RESURRECT_RESPONSE). True while it walks there or casts.
    /// </summary>
    private bool Resurrect(Player player, PlayerbotGroupCoordinator.BotGroup group)
    {
        // Not while the group fights (the killer stands by the body), nor in a wipe: vmangos party bots resurrect out of combat.
        if (player.Map is not { } map || Now < _resurrectAtMs || group.State == PlayerbotGroupState.Wiped || group.InFight(_session.World.NowMs)
            || ResurrectionSpell(player) is not { } spell) return false;
        foreach (PlayerbotGroupCoordinator.Member member in group.Members)
        {
            if (member.Guid == player.Guid || _session.World.FindOnlinePlayer(member.Guid) is not { } dead || dead.IsAlive) continue;
            // An unreleased body is the dead player's unit; once it released, the ghost cannot be targeted and the cast goes at its corpse
            // (a 1.12 client does the same: TARGET_FLAG_CORPSE), wherever the ghost is (vmangos resolves the corpse to its owner).
            Corpse? corpse = (dead.Flags & PlayerFlags.Ghost) != 0 ? dead.Combat.Corpse : null;
            if ((dead.Flags & PlayerFlags.Ghost) != 0 && (corpse is null || !corpse.IsInWorld || !ReferenceEquals(corpse.Map, map))) continue;
            if (corpse is null && !ReferenceEquals(dead.Map, map)) continue;
            if (ArcaneCore.Game.Death.Resurrection.ResurrectionRequests.IsRequested(dead)) continue;
            WorldObject body = corpse ?? (WorldObject)dead;
            Goal = PlayerbotGoalKind.Group;
            if (Distance(player, body) > ResurrectYards)
            {
                WalkTo(player, new Vector3(body.X, body.Y, body.Z), _options.ThinkIntervalMs == 0 ? 1u : (uint)_options.ThinkIntervalMs, toward: false);
                return true;
            }

            if (!PlayerbotMovementControl.Stop(_session, player)) return true;
            _route = null;
            if (corpse is not null ? _spells.CastAtCorpse(player, spell, corpse) : _spells.CastAt(player, spell, dead))
            {
                LastResurrection = dead.Name;
                LastResurrectionAtCorpse = corpse is not null;
            }

            _resurrectAtMs = Now + 3_000;
            return true;
        }

        return false;
    }

    /// <summary>The resurrection spell the bot knows (its highest rank) and carries the reagents for (Rebirth's seed), or null.</summary>
    internal SpellInfo? ResurrectionSpell(Player player) => FindResurrectionSpell(_session, player);

    /// <summary>
    /// The resurrection spell <paramref name="player"/> knows (its highest rank) and carries the reagents for, or null: what the group's healer
    /// and a bot lending a hand to a stranger (<c>PlayerbotBrain.RequestHelp</c>) cast.
    /// </summary>
    internal static SpellInfo? FindResurrectionSpell(WorldSession session, Player player)
    {
        if (!PlayerbotGroupContent.CanHeal(player.Class) || session.Services.GetService<SpellFeature>() is not { } feature) return null;
        SpellInfo? best = null;
        foreach (uint id in feature.Spellbook.GetSpells(player))
            if (feature.System.Store.Get(id) is { } spell && ResurrectionSpells.Contains(spell.Name, StringComparer.Ordinal)
                && spell.Reagents.All(r => r.Item <= 0 || r.Count == 0 || player.Inventory.GetItemCount((uint)r.Item) >= r.Count)
                && (best is null || spell.Id > best.Id))
                best = spell;
        return best;
    }

    /// <summary>
    /// The nearest corpse with something for this bot: the round-robin loot it holds, or a quest item it needs (vmangos shows a quest
    /// drop to every member who needs it, whoever holds the corpse: <c>LootItem::AllowedForPlayer</c>). Walk to it and open it.
    /// </summary>
    private bool LootHeld(Player player, uint interval)
    {
        if (player.Map is not { } map || _session.Services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.Loot is not { } loot) return false;
        Creature? nearest = null;
        float best = LootYards;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature { IsAlive: false } corpse) continue;
            if (loot.FindLoot(guid) is not { } bag || bag.IsClosed || bag.IsEmpty || !bag.HasSomethingFor(player)) continue;
            if (_lootTries.TryGetValue(guid, out int tries) && tries >= 3) continue;
            float distance = Distance(player, corpse);
            if (distance <= best) { best = distance; nearest = corpse; }
        }

        if (nearest is null) return false;
        Goal = PlayerbotGoalKind.Loot;
        if (best > 4f)
        {
            if (!WalkTo(player, new Vector3(nearest.X, nearest.Y, nearest.Z), interval, toward: false)) CountLootTry(nearest.Guid);
            return true;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return true;
        _route = null;
        _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse);
        if (!Act(WorldOpcode.CmsgLoot, PlayerbotNavigation.GuidPayload(nearest.Guid.Value))) return true;
        _lootOpened = nearest.Guid;
        _lootOpenedMs = Now;
        CountLootTry(nearest.Guid);
        return true;
    }

    /// <summary>After CMSG_LOOT: take every item and the money from the loot window, then release it (the brain's looting).</summary>
    private bool FinishLoot(Player player)
    {
        if (_lootOpened.IsEmpty) return false;
        ObjectGuid source = _lootOpened;
        bool opened = false;
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Payload.Length < 14) continue;
            var reader = new PacketReader(packet.Payload);
            if (reader.ReadUInt64() != source.Value || reader.ReadByte() != 1) continue;
            opened = true;
            _ = reader.ReadUInt32();
            byte count = reader.ReadByte();
            for (int index = 0; index < count && reader.Remaining >= 22; index++)
            {
                byte slot = reader.ReadByte();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadInt32();
                if (reader.ReadByte() == 0) _lootActions.Enqueue((WorldOpcode.CmsgAutostoreLootItem, [slot]));
            }

            _lootActions.Enqueue((WorldOpcode.CmsgLootMoney, []));
            _lootActions.Enqueue((WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(source.Value)));
        }

        if (!opened && Now - _lootOpenedMs < 5_000) return true;
        if (!opened) Act(WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(source.Value), budgeted: false);
        _lootOpened = ObjectGuid.Empty;
        return opened;
    }

    private void CountLootTry(ObjectGuid corpse)
    {
        if (_lootTries.Count >= 64) _lootTries.Clear();
        _lootTries[corpse] = _lootTries.GetValueOrDefault(corpse) + 1;
    }

    private bool CanRest(Player player)
        => _session.Services.GetService<SpellFeature>() is { } spells
            && PlayerbotConsumables.TryFindRecovery(player, spells.System, RestBelowPct, RestBelowPct, out _);

    /// <summary>Eat or drink below <see cref="RestBelowPct"/> out of combat (PlayerbotConsumables); the leader waits meanwhile.</summary>
    private bool Rest(Player player)
    {
        SpellFeature? spells = _session.Services.GetService<SpellFeature>();
        if (spells is null || player.Combat.IsInCombat
            || spells.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting }
            || PlayerbotConsumables.HasActiveFoodDrink(player, spells.System)
            || !PlayerbotConsumables.TryFindRecovery(player, spells.System, RestBelowPct, RestBelowPct, out PlayerbotConsumable consumable)) return false;
        Goal = PlayerbotGoalKind.Rest;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return true;
        }

        if (player.StandState != StandState.Sit)
        {
            Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(1u));
            return true;
        }

        Item item = consumable.Item;
        if (!Act(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(item.BagSlot, item.Slot))) return true;
        _restKind = consumable.Kind;
        _restUntilMs = Math.Max(1u, unchecked(_session.World.NowMs + 20_000));
        return true;
    }

    /// <summary>Keeps a rest going while it works. <paramref name="endNow"/> (a pushing siege leader off its waypoint) stands the bot up at once.</summary>
    private bool ContinueRest(Player player, bool endNow)
    {
        if (!PlayerbotMovementControl.Stop(_session, player)) return true;
        SpellFeature? spells = _session.Services.GetService<SpellFeature>();
        bool eating = spells is not null && PlayerbotConsumables.HasActiveFoodDrink(player, spells.System);
        if (!endNow && !player.Combat.IsInCombat && eating && PlayerbotConsumables.NeedsRecovery(player, _restKind)
            && unchecked((int)(_restUntilMs - _session.World.NowMs)) > 0)
        {
            Goal = PlayerbotGoalKind.Rest;
            return true;
        }

        if (!Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u))) return true;
        _restUntilMs = 0;
        return false;
    }

    // --- death ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Dead: wait (unreleased) for a living member's resurrection while one could give it (a survivor retreating from a wipe comes
    /// back for it), at most
    /// <see cref="DeadWaitMs"/>; then release and run back (<see cref="PlayerbotRecovery"/>: to the body, or through the instance
    /// entrance when it died inside).
    /// </summary>
    private void UpdateDead(Player player, PlayerbotGroupCoordinator.BotGroup group, uint interval)
    {
        Goal = PlayerbotGoalKind.Recover;
        long now = Now;
        if (_deadSinceMs < 0) _deadSinceMs = now;
        bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
        if (!ghost && now - _deadSinceMs < DeadWaitMs && group.CanResurrect(_session.World, player))
        {
            PlayerbotMovementControl.Stop(_session, player);
            return;
        }

        _risk.ScanHazards(player);
        _recovery.Update(player, interval);
    }

    // --- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) of this bot's plans.</summary>
    private TileWait _tileWait;

    /// <summary>The Mark of Frost a player gets when Azuregos kills it (AzuregosKazzakAI.SpellMarkOfFrostPlayer; Kazzak does not apply it): a marked member sits the rest of the fight out.</summary>
    internal const uint MarkOfFrost = 23182;

    /// <summary>The world's spell service, resolved on the first world-boss think (a singleton for the session's life).</summary>
    private SpellFeature? _spellFeature;

    /// <summary>
    /// A raid member carrying the Mark of Frost does not fight again: it stops, and holds at the rally (the leader's approach point) until the mark is gone.
    /// True when it did (the member's think is over). A world-boss raid only; the leader is not held (its raid would stall).
    /// </summary>
    private bool SitOutMark(Player player, PlayerbotGroupCoordinator.BotGroup group, uint interval)
    {
        if (group.WorldBoss is null || (_spellFeature ??= _session.Services.GetService<SpellFeature>())?.System.HasAura(player, MarkOfFrost) != true) return false;
        if (_target is not null)
        {
            if (_attacking) StopAttacking(player);
            _target = null;
            _attacking = false;
            _route = null;
            LastTargetGuid = ObjectGuid.Empty;
            LastTargetEntry = 0;
        }

        Goal = PlayerbotGoalKind.Group;
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), group.Approach) > PlayerbotGroupCoordinator.WorldBossPresentSlackYards)
            Step = WalkTo(player, group.Approach, interval, toward: true) ? "worldboss:walk-rally-marked" : "worldboss:no-path-rally-marked";
        else
        {
            Step = "worldboss:hold-marked";
            Hold(player);
        }

        return true;
    }

    private void Hold(Player player)
    {
        _route = null;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
    }

    /// <summary>Walk towards <paramref name="goal"/>, planning again when it moved more than 2 yards since the last plan.</summary>
    /// <param name="toward">A far goal: a partial route towards it is fine (<see cref="PlayerbotNavigation.TryPlanToward"/>).</param>
    /// <param name="chase">The goal is an enemy the bot closes on (<see cref="PlayerbotRoute.Chase"/>), planned as the brain plans its chase: the loop watch does not count its ends. Without it a waiting leader's walks to its waypoint and its fight chases to guards and attackers near it read as a back-and-forth (A, B, A) and the loop watch blacklisted the waypoint.</param>
    private bool WalkTo(Player player, Vector3 goal, uint interval, bool toward, bool chase = false)
    {
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), goal) <= 1.5f)
        {
            Hold(player);
            return true;
        }

        if (_route is null || _route.Complete || _route.IsStaleFor(player.Map) || Vector3.Distance(_routeGoal, goal) > 2f)
        {
            // A lift is the way when the mesh has none (the Undercity lifts, the Great Lift): the ride is driven from the next think.
            PlayerbotRoute? direct = null;
            if (toward && PlayerbotElevator.TryBegin(_session, player, goal, _options, out direct))
            {
                _route = null;
                return true;
            }

            PlanOutcome planned = direct is not null ? PlanOutcome.Route
                : toward
                    ? PlayerbotNavigation.PlanToward(player, goal, _options, ref _tileWait, _session.World.NowMs, out direct)
                    : PlayerbotNavigation.PlanExact(player, goal, _options, ref _tileWait, _session.World.NowMs, out direct);
            _route = direct;
            if (planned != PlanOutcome.Route || _route is null)
            {
                _route = null;
                PlayerbotMovementControl.Stop(_session, player);
                // A tile of the way is still being read: the goal is kept and planned to again next think, not reported unreachable.
                return planned == PlanOutcome.Pending;
            }

            // Only the flag: a chase planned as the brain plans one (nearest mesh end, no fallbacks) would drop the far-end refusal that Fight reports as no-path.
            _route.Chase = chase;
            _routeGoal = goal;
        }

        if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, Math.Max(1u, interval), _session.World.NowMs)) _route = null;
        return true;
    }

    private GroupManager? Groups()
    {
        try
        {
            return _session.Services.GetService<SocialFeature>()?.Context.Groups;
        }
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            return null;
        }
    }

    private void UpdateEquipment(Player player)
    {
        ManagedActionBudget? previous = _session.ManagedBudget;
        if (previous is null) _session.ManagedBudget = new ManagedActionBudget(1);
        try
        {
            _equipment.Update(player);
        }
        finally
        {
            if (previous is null) _session.ManagedBudget = null;
        }
    }

    /// <summary>
    /// Run one client opcode through its world handler. <paramref name="budgeted"/> actions draw on the shared per-tick budget like
    /// the brain's; the others are a client's own answers and never wait.
    /// </summary>
    private bool Act(WorldOpcode opcode, byte[] payload, bool budgeted = true)
        => PlayerbotGroupCoordinator.Act(_session, opcode, payload, budgeted);

}

/// <summary>
/// One group-follow catch-up decision (<see cref="PlayerbotGroupAI.ShouldCatchUp"/>): the group's state, how far the member is from
/// its leader, and the follow options the party bots already use. A fight-through dungeon run sets <see cref="FightThroughRun"/>.
/// </summary>
internal readonly record struct PlayerbotGroupCatchUpFacts(
    PlayerbotGroupState State, float Distance, bool InCombat, bool LeaderInCombat, bool TeleportToLeader,
    PlayerbotCatchUpMode Mode = PlayerbotCatchUpMode.Teleport, float TeleportDistance = Party.PlayerbotParty.TeleportDistance,
    float LeaderCombatRange = Party.PlayerbotParty.PartyAssistRange, bool FightThroughRun = false);

/// <summary>The answer of <see cref="PlayerbotGroupAI.CatchUpNow"/>.</summary>
internal enum PlayerbotCatchUpResult : byte
{
    /// <summary>The bot was teleported to the leader.</summary>
    Done,

    /// <summary>It cannot be now (it or the leader is being teleported, the leader is in the air): try again next think.</summary>
    Pending,

    /// <summary>It cannot be: the group only walks, teleports to the leader are off, the bot is not driven by the server, or the service refused.</summary>
    Unable,
}
