using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A deterministic, world-thread autonomous player. The feature/host owns scheduling and
/// persistence; this controller owns only bounded ordinary gameplay decisions for one session.
/// </summary>
internal sealed class PlayerbotBrain(WorldSession session, PlayerbotOptions options,
    PlayerbotLocalPlanner? planner = null, CancellationToken lifetime = default, PlayerbotSharedSetAsides? shared = null)
{
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private PlayerbotRoute? _route;
    private Creature? _target;
    private uint _thinkElapsed;
    private bool _attacking;
    private bool _lootOpened;
    private uint _restUntilMs;
    private PlayerbotConsumableKind _restKind;
    private int _roamDirection;
    private int _exploreRefusals;
    private PlayerbotGoalKind _goal = PlayerbotGoalKind.Explore;
    private long _lootStartedMs;
    private readonly Queue<(WorldOpcode Opcode, byte[] Payload)> _lootActions = new();
    private bool _lootResponseReceived;
    private readonly PlayerbotQuestGoals _quests = new(session, options);
    private readonly PlayerbotTownGoals _town = new(session, options);
    private readonly PlayerbotEconomy _economy = new(session, options);
    private readonly PlayerbotTrainerDestinations _trainers = new(session, options);
    private readonly PlayerbotWorldDestinations _destinations = new(session, options);
    private readonly PlayerbotCombatSpells _combatSpells = new(session);
    private readonly PlayerbotRecovery _recovery = new(session, options);
    private readonly PlayerbotHomeReturn _homeReturn = new();
    private PlayerbotHomeArea? _home;
    private PlayerbotRisk? _risk;

    /// <summary>
    /// The bot's home area (<see cref="PlayerbotHomeArea"/>), or null for none. While set, goal selection prefers what lies inside it
    /// and a bot that strays out of it with no errand walks back (<see cref="PlayerbotHomeReturn"/>). Setting a home drops the current
    /// intent and the walk home in progress. World thread (or before the bot is started).
    /// </summary>
    internal PlayerbotHomeArea? Home
    {
        get => _home;
        set
        {
            if (ReferenceEquals(_home, value)) return;
            _home = value;
            _homeReturn.Reset();
            _quests.Home = value;
            _destinations.Home = value;
            DropIntent();
        }
    }

    /// <summary>
    /// The in-area filters (quest takers, quest and errand destinations) apply only while the bot is on its home map. A bot on
    /// another map is off-map (<see cref="PlayerbotHomeState.OffMap"/>): the area has no meaning there, and applying it would skip every
    /// creature and take no quest, so it behaves as a bot with no home until it is on the home map again.
    /// </summary>
    private void SyncHomeFilters(Player player)
    {
        PlayerbotHomeArea? active = _home is { } home && (player.Map?.MapId ?? player.MapId) == home.MapId ? home : null;
        _quests.Home = active;
        _destinations.Home = active;
    }

    /// <summary>The home the in-area filters use right now: the home while the bot is on its map, else null (tests).</summary>
    internal PlayerbotHomeArea? ActiveFilterHome => _quests.Home;

    /// <summary>The home line <c>.playerbot status</c> shows (<c>inside:120</c>, <c>returning:812</c>, <c>off-map</c>, <c>blocked:812</c>), null without a home.</summary>
    internal string? HomeReport => _home is null ? null : _home.ZoneFoundUnknown ? "zone-unknown" : _homeReturn.Report;

    /// <summary>The home line BOTINSPECT shows (place, map, centre, radius, distance, state), null without a home.</summary>
    internal string? HomeDescription => _home is null ? null : _homeReturn.Describe(_home, _options.Home.ReturnMode);

    /// <summary>The walk back to the home (inspection and tests).</summary>
    internal PlayerbotHomeReturn HomeReturn => _homeReturn;

    /// <summary>Risk against reward and retreat (<see cref="PlayerbotRisk"/>; inspection and tests).</summary>
    internal PlayerbotRisk Risk => _risk ??= NewRisk();

    private PlayerbotRisk NewRisk()
    {
        var risk = new PlayerbotRisk(_session, _options, _combatSpells);
        _recovery.Hazards = risk.HazardThreats;
        return risk;
    }

    /// <summary>The risk line BOTINSPECT and <c>.playerbot status</c> show (<see cref="PlayerbotRisk.Report"/>).</summary>
    internal string RiskReport => _options.Risk.Enabled ? Risk.Report : "decision=off";

    /// <summary>The ghost recovery (inspection and tests).</summary>
    internal PlayerbotRecovery Recovery => _recovery;
    private readonly PlayerbotEquipment _equipment = new(session);
    private readonly PlayerbotStallWatch _stall = new();
    private readonly PlayerbotSuspensions _suspensions = new() { Shared = shared };

    /// <summary>A goal this bot's stall set aside for every bot (<see cref="PlayerbotSharedSetAsides.Report"/>), until the feature logs it.</summary>
    internal PlayerbotSharedSetAside? SharedSetAside { get; set; }

    /// <summary>
    /// Whether the bot was outside its home area when it last gave a goal up for a stall (<see cref="GiveUpStalledGoal"/>): that stall
    /// was not reported to the shared set-asides, and the feature logs it as made outside the home.
    /// </summary>
    internal bool StallOutsideHome { get; private set; }
    // The creature entries attacking the bot at its last living update: its killers when it dies (the server clears a dead
    // player's attackers before the brain sees the death).
    private readonly HashSet<uint> _attackerEntries = [];
    // The last errand the bot was travelling for (quest, trainer, vendor): what led it where it keeps dying.
    private (PlayerbotGoalKind Goal, uint Entry, uint Quest, uint AtMs) _errand;
    // The creatures attacking the bot at its last living update (guid and entry): the risk's danger memory of its killers.
    private readonly List<(ObjectGuid Guid, uint Entry)> _attackers = [];
    private readonly CancellationTokenSource _planningStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task<string?>? _planTask;
    private string? _modelChoice;
    private long _nextPlanMs;
    private bool _needsRest;

    /// <summary>The bot sat down to wait for health without food (<see cref="PlayerbotRisk.KeepWaiting"/>); it stands up when the wait ends.</summary>
    private bool _satToWait;

    /// <summary>The most health and mana seen while resting (<see cref="Fingerprint"/>): a rise is progress for the stall watch.</summary>
    private ulong _restMark;
    private bool _deathIntentRetired;
    private int _stopped;
    // Creatures the bot could not path to (or looped trying to reach): skipped for a while, so the next think does
    // not pick the same nearest unreachable target again.
    private readonly Dictionary<ObjectGuid, uint> _unreachable = [];
    private const uint UnreachableTargetMs = 30_000;
    private const float MeleeRange = PlayerbotClassRotation.MeleeRange;
    // A ranged bot standing at its range without anything to cast at a target that is not fighting it closes in after this long
    // (the group AI uses it too: PlayerbotGroupAI.Fight).
    internal const uint RangedIdleLimitMs = 8_000;
    private uint _rangedIdleMs;

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _planningStop.Cancel();
        _planningStop.Dispose();
    }

    internal PlayerbotGoalKind Goal => _goal;
    internal Creature? InspectionTarget => _target;
    internal uint TargetEntry { get; private set; }
    internal uint QuestId { get; private set; }

    /// <summary>The current stall (<see cref="PlayerbotStallWatch"/>), or null while the bot makes progress.</summary>
    internal string? StallReport => _stall.Report;

    /// <summary>The last stall reported, kept after progress resumed.</summary>
    internal string? LastStall => _stall.LastReport;

    /// <summary>Stalls reported so far.</summary>
    internal int StallCount => _stall.Count;

    /// <summary>Goals given up for stalls so far (<see cref="PlayerbotStallWatch.GiveUps"/>).</summary>
    internal int StallGiveUps => _stall.GiveUps;

    /// <summary>
    /// The quest objectives (quest, creature entry) this bot leaves to a group while it waits for partners or does them with its group
    /// (<see cref="Groups.PlayerbotGroupCoordinator"/>): the brain does not hunt them alone.
    /// </summary>
    internal HashSet<(uint Quest, uint Entry)> GroupHeld { get; } = [];

    /// <summary>
    /// The bot comes back from its group (the group AI drove it): whatever route, target, loot or rest the brain had belongs to
    /// another place. Its quests, suspensions and memories stay.
    /// </summary>
    internal void ResumeAfterGroup()
    {
        DropIntent();
        _lootActions.Clear();
        _lootResponseReceived = false;
        _restUntilMs = 0;
        _needsRest = false;
        _satToWait = false;
        _thinkElapsed = 0;
        _combatSpells.Reset();
        _stall.Reset();
        _homeReturn.Reset();
        _goal = PlayerbotGoalKind.Explore;
    }

    /// <summary>Whether goals skip creatures of <paramref name="entry"/> for now (a stall or a death loop set it aside; tests).</summary>
    internal bool IsEntrySetAside(uint entry) => _suspensions.IsEntrySuspended(entry, _session.World.NowMs);

    internal void Update(uint elapsedMs)
    {
        if (!_options.Enabled || _stopped != 0 || _session.Player is not { } player)
            return;
        _destinations.SkipQuest ??= _quests.IsRefused;
        _quests.Suspensions = _town.Suspensions = _trainers.Suspensions = _destinations.Suspensions = _suspensions;
        _quests.HeldForGroup ??= (quest, entry) => GroupHeld.Contains((quest, entry)) || GroupHeld.Contains((0, entry));
        if (!player.IsInWorld) return;
        PlayerbotThinkProbe.Phase("brain");
        SyncHomeFilters(player);
        _recovery.UnsafeBody ??= JudgeBody;
        _recovery.LandingUnsafe ??= (bot, at) => Risk.TooStrongAt(bot, at) is not null;
        _recovery.EscapeLanding ??= EscapeLanding;

        bool dead = !player.IsAlive;
        if (dead)
        {
            _stall.Reset();
            // The walk home planned from where the bot fell: after the resurrection (maybe far away) it plans afresh.
            _homeReturn.Reset();
            if (!_deathIntentRetired)
            {
                RecordDeath(player);
                RetireDeathIntent();
                // A death ends a tram trip (its stage, car and entrance consent belong to the living bot): the ghost's corpse run may
                // walk the tram map to a body there (PlayerbotTram.AllowsWalkingOn), and a bot revived there takes the nearest exit.
                PlayerbotTram.Cancel(player);
                // And a lift ride: the corpse loses the transport. This must run BEFORE the acknowledgements below
                // (PlayerbotMovementControl.Update): the death root's ack then carries the cleared block, relayed as MSG_MOVE_ROOT.
                PlayerbotElevator.Cancel(player);
                _deathIntentRetired = true;
            }
            _goal = PlayerbotGoalKind.Recover;
        }
        else
        {
            // Alive again: the next death is a new one, even when pending movement orders hold this update below. The recovery is
            // reset here too, before that early return: a death in the window it held would otherwise start in the last death's
            // spirit-healer fallback (PlayerbotRecovery.Update goes straight to it), never release its new body, and fault.
            if (_deathIntentRetired) PlayerbotElevator.SettleAfterRevive(player);
            _deathIntentRetired = false;
            _recovery.Reset();
            RememberAttackers(player);
        }
        // Death cleanup must happen before this seam, but pending movement orders still
        // require their ordinary acknowledgements while alive or ghosted.
        if (PlayerbotMovementControl.Update(_session, player)) return;

        if (dead)
        {
            _thinkElapsed += elapsedMs;
            if (_thinkElapsed >= _options.ThinkIntervalMs)
            {
                uint recoveryInterval = _thinkElapsed;
                _thinkElapsed = 0;
                PlayerbotThinkProbe.Phase("recovery");
                Risk.ScanHazards(player); // a ghost keeps watching the creatures round its body (the revive spot)
                _recovery.Update(player, recoveryInterval);
            }
            return;
        }
        _deathIntentRetired = false;
        _recovery.Reset();

        if (_restUntilMs != 0)
        {
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            SpellFeature? restingSpells = _session.Services.GetService<SpellFeature>();
            bool activeRecovery = restingSpells is not null
                && PlayerbotConsumables.HasActiveFoodDrink(player, restingSpells.System);
            if (!player.Combat.IsInCombat && activeRecovery && PlayerbotConsumables.NeedsRecovery(player, _restKind)
                && unchecked((int)(_restUntilMs - _session.World.NowMs)) > 0)
                return;
            if (!_session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u))) return;
            _restUntilMs = 0;
            _goal = PlayerbotGoalKind.Explore;
            return;
        }

        if (_lootActions.TryPeek(out var nextLoot))
        {
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            if (_session.TryManagedAction(nextLoot.Opcode, nextLoot.Payload)) _lootActions.Dequeue();
            return;
        }

        _thinkElapsed = checked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs)
            return;
        uint interval = _thinkElapsed;
        _thinkElapsed = 0;

        // A tram trip (or a bot found on the Deeprun Tram map) owns the bot until it stands on the continent again (PlayerbotTram): nothing
        // else moves it meanwhile, and the stall watch does not judge a bot waiting for its car. A fight on the way to the entrance ends the
        // trip (the goal that began it begins it again afterwards).
        if (player.IsAlive)
        {
            PlayerbotThinkProbe.Phase("tram");
            if (player.Combat.IsInCombat && PlayerbotTram.TripOf(player) is { Stage: TramStage.ToEntrance }) PlayerbotTram.Cancel(player);
            TramStep tram = PlayerbotTram.Drive(_session, player, _options, interval, out string? tramFailure);
            if (tram == TramStep.Traveling)
            {
                _stall.Reset();
                _target = null;
                _route = null;
                _attacking = false;
                return;
            }

            if (tram == TramStep.Failed)
            {
                _stall.ReportExternal(tramFailure ?? "tram: the trip failed (no reason recorded)");
                GiveUpStalledGoal(player, shareable: false);
                return;
            }

            // A lift ride (PlayerbotElevator) owns the bot the same way, until it stands on the other level; a fight on the way to the
            // landing ends it. A failed ride is the bot's own set-aside, like a failed tram trip: the lift did not carry it.
            if (player.Combat.IsInCombat && PlayerbotElevator.TripOf(player) is { Stage: ElevatorStage.ToStop }) PlayerbotElevator.Cancel(player);
            ElevatorStep lift = PlayerbotElevator.Drive(_session, player, _options, interval, out string? liftFailure);
            if (lift == ElevatorStep.Traveling)
            {
                _stall.Reset();
                _target = null;
                _route = null;
                _attacking = false;
                return;
            }

            if (lift == ElevatorStep.Failed)
            {
                _stall.ReportExternal(liftFailure ?? "elevator: the ride failed (no reason recorded)");
                GiveUpStalledGoal(player, shareable: false);
                return;
            }
        }

        PlayerbotThinkProbe.Phase("stall watch");
        if (player.IsAlive && _stall.Observe(player, _goal, TargetEntry, QuestId, _session.World.NowMs,
                (uint)Math.Clamp(_options.StallSeconds, 10, 86_400) * 1000u, Fingerprint))
            GiveUpStalledGoal(player);


        if (PlayerbotMotion.ConsumeLoop(player))
        {
            // The motion gave up a loop (PlayerbotMotion): drop this intent, skip its target for a while, and let the
            // goals choose again; the looped destinations are refused by PlayerbotNavigation.TryPlan meanwhile.
            if (_target is { } looped) MarkUnreachable(looped);
            _target = null;
            _route = null;
            _attacking = false;
            TargetEntry = 0;
            _goal = PlayerbotGoalKind.Explore;
            return;
        }

        if (!player.IsAlive)
        {
            if (!_deathIntentRetired)
            {
                RecordDeath(player);
                RetireDeathIntent();
                _deathIntentRetired = true;
            }
            _goal = PlayerbotGoalKind.Recover;
            PlayerbotThinkProbe.Phase("recovery");
            _recovery.Update(player, interval);
            return;
        }

        // A retreat owns the bot until nothing threatens it (PlayerbotRetreat); out of combat the bot lays the trail back.
        PlayerbotThinkProbe.Phase("risk");
        if (Risk.UpdateRetreat(player, interval))
        {
            DropIntent();
            _goal = PlayerbotGoalKind.Retreat;
            return;
        }

        Risk.Track(player);
        // Every walk of this bot keeps out of its hazards (PlayerbotHazards), unless the estimate is off.
        PlayerbotNavigation.Guard(player, _options.Risk.Enabled ? Risk : null);

        // The way out of a ring of strong creatures (PlayerbotRisk.Refused) owns the bot, out of combat, until walked or bounded.
        if (!player.Combat.IsInCombat && Risk.EscapeRoute is { } escape)
        {
            DropIntent();
            _goal = PlayerbotGoalKind.Escape;
            if (!PlayerbotNavigation.TryAdvance(_session, escape, _options, interval, _session.World.NowMs)) Risk.EndEscape();
            return;
        }

        if (_lootOpened && _target is { } pendingLoot)
        {
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            if (_lootResponseReceived || _session.World.Uptime.TotalMilliseconds - _lootStartedMs >= 10_000)
            {
                if (!_lootResponseReceived) _session.TryManagedAction(WorldOpcode.CmsgLootRelease,
                    PlayerbotNavigation.GuidPayload(pendingLoot.Guid.Value));
                _lootOpened = false;
                _lootResponseReceived = false;
                _target = null;
                _route = null;
                _attacking = false;
                Rest(player);
            }
            else _lootResponseReceived = LootManagedPackets(pendingLoot.Guid.Value);
            return;
        }

        PlayerbotThinkProbe.Phase("quest return");
        uint completedReturn = _quests.CompletedReturnQuest(player);
        if (completedReturn != 0)
        {
            _goal = PlayerbotGoalKind.Quest;
            QuestId = completedReturn;
            // Leave ongoing spell execution to its ordinary completion. Travel does
            // not clear casts/cooldowns or manufacture an out-of-combat state.
            if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
                { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }) return;
            if (_attacking || player.Combat.Victim is not null)
            {
                if (_session.TryManagedAction(WorldOpcode.CmsgAttackstop, []))
                { _attacking = false; _target = null; _route = null; }
                return;
            }
            if (!player.Combat.IsInCombat && _quests.Update(player, interval))
            { QuestId = _quests.QuestId; TargetEntry = _quests.TargetEntry; return; }
            _target = null;
            _route = null;
            PlayerbotThinkProbe.Phase("quest destinations");
            // The trip's target is the ender the destinations chose for this quest, or none yet: never the entry of an earlier goal. A
            // failed plan leaves both TargetEntry values as they were, and the stall watch then reported, and GiveUpStalledGoal set aside,
            // a creature this trip has nothing to do with (GitHub #186: quest 1111's return stalled with target 4147, a Saltstone Basilisk
            // the bot had been fighting, and 11259, the ender of quest 5361 it had been walking to before).
            _destinations.Update(player, 0, interval, completedReturn);
            TargetEntry = _destinations.QuestId == completedReturn ? _destinations.TargetEntry : 0u;
            // Wait/retry at the real giver while combat or a normal interaction
            // refusal remains. A completed quest must not reopen opportunistic grind.
            return;
        }

        PlayerbotThinkProbe.Phase("threats");
        Creature? outgoingVictim = player.Combat.Victim is Creature victim
            && player.Map is { } map && victim.IsInWorld && victim.IsAlive && ReferenceEquals(victim.Map, map)
            && map.Combat.Hooks.CanAttack(player, victim)
            ? victim
            : null;
        if (outgoingVictim is not null)
        {
            bool changed = !ReferenceEquals(_target, outgoingVictim);
            _target = outgoingVictim;
            if (changed)
            {
                _route = null;
                _attacking = false;
            }
            _lootOpened = false;
            TargetEntry = outgoingVictim.Entry;
        }

        bool hasDeadLootTarget = _target is { IsAlive: false } && ReferenceEquals(_target.Map, player.Map);
        Creature? defensiveAttacker = outgoingVictim is null && !hasDeadLootTarget && player.Combat.Victim is null
            ? FindDefensiveAttacker(player)
            : null;
        if (defensiveAttacker is not null)
        {
            bool changed = !ReferenceEquals(_target, defensiveAttacker);
            _target = defensiveAttacker;
            if (changed)
            {
                _route = null;
                _attacking = false;
            }
            _lootOpened = false;
            TargetEntry = defensiveAttacker.Entry;
        }
        // A caster or hunter fights without a melee swing, so its victim is not Combat.Victim: a living target that has the
        // bot on its threat list (or attacks it) is still the bot's fight, not a combat linger.
        bool rangedFight = defensiveAttacker is null && outgoingVictim is null && _target is { IsAlive: true } engaged
            && ReferenceEquals(engaged.Map, player.Map)
            && (player.Combat.ThreatenedBy.Contains(engaged) || ReferenceEquals(engaged.Combat.Victim, player));
        if (defensiveAttacker is null && !rangedFight && player.Combat.IsInCombat && player.Combat.Victim is null
            && !hasDeadLootTarget)
        {
            PlayerbotMovementControl.Stop(_session, player);
            // Combat linger after an ordinary attack stop must not reopen an optional
            // grind target. Let the authoritative combat timer/handler clear the state.
            _target = null;
            _route = null;
            _attacking = false;
            _lootOpened = false;
            TargetEntry = 0;
            _goal = PlayerbotGoalKind.Combat;
            return;
        }

        if (!player.Combat.IsInCombat && outgoingVictim is null && !hasDeadLootTarget)
        {
            SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
            // After a retreat, or before a pull the bot is too hurt for: recover to World:Playerbots:Risk:RecoverHealthPct first.
            if (Risk.KeepWaiting(player))
            {
                uint wanted = (uint)Math.Clamp(_options.Risk.RecoverHealthPct, 10f, 100f);
                DropIntent();
                _goal = PlayerbotGoalKind.Rest;
                if (spellFeature is not null && PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, wanted, wanted, out _))
                    Rest(player, wanted);
                else if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                // No food or drink: sit, as a player waiting for health would (vmangos Player::RegenerateHealth: half as much again
                // per tick when not standing up).
                else if (player.StandState == StandState.Stand
                         && _session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Sit)))
                    _satToWait = true;
                return;
            }

            // The wait is over: stand up from it before anything else (a sitting player cannot cast, and the client stands
            // before it moves).
            if (_satToWait)
            {
                if (player.StandState != StandState.Sit) _satToWait = false;
                else if (_session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand)))
                    _satToWait = false;
                return;
            }

            if (_needsRest || spellFeature is not null && PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, out _))
            {
                Rest(player);
                return;
            }
            PlayerbotThinkProbe.Phase("equipment");
            if (_equipment.Update(player))
            {
                _target = null;
                _route = null;
                _attacking = false;
                TargetEntry = 0;
                _goal = PlayerbotGoalKind.Explore;
                return;
            }
            // Class upkeep between fights (forms, buffs on the bot and its group, the pet, heals, stealth before a pull),
            // vmangos UpdateOutOfCombatAI_<Class>; the creature the bot is closing in on is its pull target.
            PlayerbotThinkProbe.Phase("class upkeep");
            _combatSpells.PullTarget = _target is { IsAlive: true } pull && ReferenceEquals(pull.Map, player.Map) ? pull : null;
            if (_combatSpells.UpdateOutOfCombat(player, interval)) return;
            PlayerbotThinkProbe.Phase("local plan");
            UpdateLocalPlan(player);
            PlayerbotThinkProbe.Phase("home");
            if (_home is { } home && TryReturnHome(player, home, interval)) return;
            PlayerbotThinkProbe.Phase("town");
            // A returned ID is only a proposal; each controller rechecks the live gameplay rules.
            if (_modelChoice == "town" && _town.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
            PlayerbotThinkProbe.Phase("quests");
            if (_quests.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _quests.Goal; QuestId = _quests.QuestId; TargetEntry = _quests.TargetEntry; return; }
            QuestId = _quests.QuestId;
            PlayerbotThinkProbe.Phase("town");
            if (_town.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
            PlayerbotThinkProbe.Phase("economy");
            if (_economy.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _economy.Goal; TargetEntry = _economy.TargetEntry; return; }
            PlayerbotThinkProbe.Phase("trainers");
            if (_quests.PreferredCreatureEntry == 0 && _trainers.HasCandidate(player))
            {
                // An abandoned idle-grind target must not keep reopening combat instead
                // of travelling to a useful trainer. Stop through the ordinary handler;
                // active combat, casts, quest returns and pending loot keep priority.
                if (_attacking || player.Combat.Victim is not null)
                {
                    if (_session.TryManagedAction(WorldOpcode.CmsgAttackstop, []))
                    { _attacking = false; _target = null; _route = null; }
                    _goal = PlayerbotGoalKind.Train;
                    return;
                }
                _target = null;
                _route = null;
                if (_trainers.Update(player, interval))
                { _goal = PlayerbotGoalKind.Train; TargetEntry = _trainers.TargetEntry; return; }
            }
            PlayerbotThinkProbe.Phase("quest destinations");
            if (_quests.PreferredCreatureEntry == 0 && player.Combat.Victim is null
                && _destinations.HasQuestCandidate(player))
            {
                // Available quest travel precedes optional idle grinding. Actual combat,
                // visible interactions, completed returns, supplies and trainers keep their
                // existing priority; path failure still permits the ordinary fallback.
                if (spellFeature?.System.GetState(player.Guid)?.CurrentCast is
                    { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }) return;
                if (_session.ManagedBudget is { Remaining: <= 0 }) return;
                if (_destinations.Update(player, 0, interval))
                {
                    _target = null;
                    _route = null;
                    _attacking = false;
                    _goal = PlayerbotGoalKind.Quest;
                    TargetEntry = _destinations.TargetEntry;
                    QuestId = _destinations.QuestId;
                    return;
                }
            }
            if (_modelChoice == "explore") { _target = null; PlayerbotThinkProbe.Phase("explore"); Explore(player, interval); return; }
        }

        if (_target is null || _target.Map != player.Map)
        {
            PlayerbotThinkProbe.Phase("choose target");
            Creature? previousTarget = _target;
            uint preferred = _quests.PreferredCreatureEntry;
            _target = _options.Risk.Enabled
                ? Risk.ChooseTarget(player, preferred, IsSkipped, questObjective: preferred != 0)
                : FindTarget(player, preferred, IsSkipped);
            _rangedIdleMs = 0;
            // Keep an exploration route across decisions; otherwise every thought changes
            // direction after only its first terrain step and the player never travels.
            if (_target is not null || previousTarget is not null) _route = null;
            _attacking = false;
            _lootOpened = false;
            if (_target is null)
            {
                PlayerbotThinkProbe.Phase("grind destinations");
                if (_destinations.Update(player, _quests.PreferredCreatureEntry, interval))
                {
                    _route = null;
                    _goal = _quests.PreferredCreatureEntry != 0 ? PlayerbotGoalKind.Grind : PlayerbotGoalKind.Quest;
                    TargetEntry = _destinations.TargetEntry;
                    if (_destinations.QuestId != 0) QuestId = _destinations.QuestId;
                    return;
                }
                PlayerbotThinkProbe.Phase("explore");
                Explore(player, interval);
                return;
            }
            TargetEntry = _target.Entry;
            // The way round a pack on the approach (PlayerbotRisk.FindDetour): followed before the direct chase.
            if (Risk.Detour is { } detour) { _route = detour; Risk.Detour = null; }
        }
        Creature target = _target;

        TargetEntry = target.Entry;

        PlayerbotThinkProbe.Phase("combat");
        if (target.IsAlive)
        {
            // A losing fight is given up (PlayerbotRiskModel.Judge) before the next blow.
            if (player.Combat.IsInCombat && Risk.ObserveFight(player, target))
            {
                DropIntent();
                _goal = PlayerbotGoalKind.Retreat;
                Risk.UpdateRetreat(player, interval);
                return;
            }

            if (_combatSpells.Update(player, target, interval))
            {
                _rangedIdleMs = 0;
                _goal = PlayerbotGoalKind.Combat;
                return;
            }

            float distance = Distance(player, target);
            bool fightingBot = player.Combat.ThreatenedBy.Contains(target) || ReferenceEquals(target.Combat.Victim, player);
            PlayerbotFightPosition position = DecidePosition(_combatSpells.PreferredRange(player), distance,
                player.Class == Game.Class.Hunter, ReferenceEquals(target.Combat.Victim, player),
                _rangedIdleMs >= RangedIdleLimitMs && !fightingBot, MapCombat.CanReachWithMeleeAutoAttack(player, target));
            if (position == PlayerbotFightPosition.Hold)
            {
                // In range: stand and let the rotation cast (or wand) at the next decision.
                _rangedIdleMs = fightingBot ? 0 : _rangedIdleMs + interval;
                _route = null;
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                _goal = PlayerbotGoalKind.Combat;
                return;
            }

            if (position is PlayerbotFightPosition.ChaseToRange or PlayerbotFightPosition.ChaseToMelee)
            {
                PlayerbotThinkProbe.Phase("chase");
                if (_route is null && !PlayerbotNavigation.TryPlan(player,
                        new System.Numerics.Vector3(target.X, target.Y, target.Z), _options, out _route))
                {
                    MarkUnreachable(target);
                    _target = null;
                    PlayerbotMovementControl.Stop(_session, player);
                    return;
                }

                _goal = PlayerbotGoalKind.Grind;
                if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, interval, _session.World.NowMs))
                    _route = null;
                return;
            }

            // The server refuses a swing at a target outside the auto-attack arc (SMSG_ATTACKSWING_BADFACING) and a managed player
            // never turns by itself: face the victim first, as the client does when it attacks.
            if (!FacesTarget(player, target))
            {
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0 && !PlayerbotMovementControl.Stop(_session, player)) return;
                _goal = PlayerbotGoalKind.Combat;
                PlayerbotMotion.Face(_session, player, MathF.Atan2(target.Y - player.Y, target.X - player.X));
                return;
            }

            if (!_attacking || player.Combat.Victim is null)
            {
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
                { PlayerbotMovementControl.Stop(_session, player); return; }
                _goal = PlayerbotGoalKind.Combat;
                _attacking = _session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                    PlayerbotNavigation.GuidPayload(target.Guid.Value));
            }
            return;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        _goal = PlayerbotGoalKind.Loot;
        _lootOpened = _session.TryManagedAction(WorldOpcode.CmsgLoot,
            PlayerbotNavigation.GuidPayload(target.Guid.Value));
        if (_lootOpened) _lootStartedMs = (long)_session.World.Uptime.TotalMilliseconds;
        return;
    }

    /// <summary>
    /// The walk back to the home area, in the out-of-combat block after the class upkeep and before the quest, town and trainer
    /// steps. A bot on the home map, outside the radius (or still walking back and outside the return radius) with no errand drops its
    /// intent and walks to the centre. Errands that are really needed keep their priority and may take the bot anywhere: a vendor or
    /// trainer that has something useful to do (<see cref="PlayerbotTownGoals.HasCandidate"/>, <see cref="PlayerbotTrainerDestinations.HasCandidate"/>)
    /// and a quest exchange already under way. A completed quest's turn-in, combat, looting, rest and retreat run earlier in
    /// <see cref="Update"/> and are not touched; a dead bot follows the ghost recovery and walks home after the resurrection. A bot on
    /// another map is teleported home unless the return mode is <see cref="PlayerbotHomeReturnMode.Walk"/> (then it is reported off-map
    /// and left alone).
    /// <para>
    /// Walk or teleport (<see cref="PlayerbotHomeOptions.ReturnMode"/>, <see cref="PlayerbotHomeReturn.Judge"/>): Auto teleports a bot on
    /// another map, farther than <see cref="PlayerbotHomeOptions.TeleportDistanceYards"/>, whose straight line home crosses an area far
    /// above its level, or whose walk was set aside as blocked, and walks otherwise. The teleport is never made in combat (this block runs
    /// out of combat only, and <see cref="PlayerbotHomeReturn.TryTeleport"/> checks again), dead or as a ghost (the dead branch of
    /// <see cref="Update"/> returns before this), in a group, or to a map <see cref="PlayerbotMapPolicy"/> does not allow. A party AI, a
    /// bot-led group, the battlegrounds coordinator or a scripted controller drives the bot instead of this brain, so the home (and its
    /// teleport) is suspended then (ManagedPlayerbotFeature.Think).
    /// </para>
    /// </summary>
    private bool TryReturnHome(Player player, PlayerbotHomeArea home, uint interval)
    {
        uint now = _session.World.NowMs;
        PlayerbotHomeOptions homeOptions = _options.Home;
        PlayerbotHomeState state = _homeReturn.Evaluate(player, home, now);
        switch (state)
        {
            case PlayerbotHomeState.Inside:
            case PlayerbotHomeState.OffMap when homeOptions.ReturnMode == PlayerbotHomeReturnMode.Walk:
            case PlayerbotHomeState.Blocked when !(homeOptions.ReturnMode == PlayerbotHomeReturnMode.Auto && _homeReturn.WalkBlocked):
                return false;
        }

        if (_quests.Busy || _town.HasCandidate(player)
            || _quests.PreferredCreatureEntry == 0 && _trainers.HasCandidate(player)) return false;
        PlayerbotHomeTeleportReason teleport = _homeReturn.Judge(player, home, homeOptions);
        if (teleport != PlayerbotHomeTeleportReason.None)
        {
            if (!_homeReturn.TryTeleport(_session, player, home, _options, teleport, now)) return false;
            DropIntent();
            _goal = PlayerbotGoalKind.Home;
            return true;
        }

        if (state != PlayerbotHomeState.Returning) return false;
        DropIntent();
        if (!_homeReturn.Update(_session, player, home, _options, interval, now)) return false;
        _goal = PlayerbotGoalKind.Home;
        return true;
    }

    /// <summary>
    /// A new stall (<see cref="PlayerbotStallWatch"/>): set the goal's target aside for <see cref="PlayerbotSuspensions.SuspendMs"/>
    /// and drop the intent, so the next decision picks something else. A quest goal sets its quest and its giver aside (a completed
    /// quest whose ender cannot be reached no longer holds the bot at its giver for good), a vendor or trainer goal that NPC, a
    /// fight its target; an exploring bot turns anywhere.
    /// <para>
    /// The stall counts towards the goals set aside for every bot (<see cref="PlayerbotSharedSetAsides"/>) only while the bot is inside
    /// its home area, or has none. A homed bot outside it (on another map, on its way back, or on a turn-in or capital errand) stalls
    /// where its home does not keep it: that says nothing about the goal for the bots whose home it is (live 2026-10-11: quest 109,
    /// ended at Sentinel Hill inside every bot's home, was set aside for all of them by bots stalled in Redridge and at Raven Hill).
    /// Its own set-aside is unchanged. <see cref="StallOutsideHome"/> tells the feature's log line (and so bot QA) which it was.
    /// </para>
    /// <para>
    /// A failed tram trip (<see cref="PlayerbotTram"/>, <paramref name="shareable"/> false) is the bot's own too: it says the tram did not
    /// carry the bot (no car, no stop, no way to the car's side), not that the goal beyond it is unreachable, and the place it is reported
    /// (the exit the bot left by) is not where the goal failed. Shared, three bots behind one stopped car would set every bot's goal on the
    /// other side aside, and bot QA would read it as that quest's fault. Its log line is never marked outside the home either
    /// (ManagedPlayerbotFeature.LogStall marks only "stalled" lines).
    /// </para>
    /// </summary>
    internal void GiveUpStalledGoal(Player player, bool shareable = true)
    {
        uint now = _session.World.NowMs;
        StallOutsideHome = _home is { } home && !home.Contains(player);
        if (shareable && !StallOutsideHome && shared?.Report(_goal, TargetEntry, QuestId, player.Guid.Value, now, player.MapId,
                new System.Numerics.Vector3(player.X, player.Y, player.Z)) is { } everyone)
            SharedSetAside = everyone;
        switch (_goal)
        {
            case PlayerbotGoalKind.Quest:
                _suspensions.SuspendQuest(QuestId, now);
                _suspensions.SuspendEntry(TargetEntry, now);
                break;
            case PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Train:
                _suspensions.SuspendEntry(TargetEntry, now);
                break;
            case PlayerbotGoalKind.Grind or PlayerbotGoalKind.Combat or PlayerbotGoalKind.Loot:
                if (_target is { } target) MarkUnreachable(target);
                break;
            case PlayerbotGoalKind.Home:
                _homeReturn.MarkBlocked(now);
                break;
            default:
                _exploreRefusals = 3;
                break;
        }

        _target = null;
        _route = null;
        _attacking = false;
        _lootOpened = false;
        PlayerbotMovementControl.Stop(_session, player);
        if (shareable) EscapeIsland(player);
    }

    /// <summary>
    /// A stall gave a goal up on a navigation-mesh island (<see cref="PlayerbotNavigation.IsOnMeshIsland"/>): no route leaves it, so
    /// the next goal would stall the same, and so would every one after it. Out of combat and alive, the bot is moved off as from an
    /// unsafe landing (<see cref="EscapeLanding"/>): a homed bot teleports to its home's centre
    /// (<see cref="PlayerbotHomeTeleportReason.Island"/>, at most once per home-teleport cooldown), a bot without a home to its bind point.
    /// Only in the open world: a dungeon's own map is left to its group and the instance.
    /// <para>
    /// Live 2026-10-11 (bot QA #180, live-w23-r1, main 85023136): Wfbotac and Wfbotaj stood inside the round wall of the Molsen Farm
    /// (Westfall), on an island the mesh joins to nothing, and gave up goal after goal there for hours, across restarts (Wfbotac was saved
    /// there, and stalled within two minutes of every login). True when the bot was moved.
    /// </para>
    /// </summary>
    private bool EscapeIsland(Player player)
    {
        if (!player.IsAlive || player.Combat.IsInCombat || player.Map is not { InstanceId: 0 }) return false;
        if (!PlayerbotNavigation.IsOnMeshIsland(player, _options)) return false;
        uint now = _session.World.NowMs;
        // Home first, but only when the teleport home would be made: a refused TryTeleport would set the walk of a bot inside its home
        // aside as blocked (PlayerbotHomeReturn.Refuse), and the bind point is the way out then, as from an unsafe landing.
        if (_home is { } home)
        {
            _homeReturn.Evaluate(player, home, now);
            if (_homeReturn.TeleportRefusal(_session, player, home, _options, now) is null
                && _homeReturn.TryTeleport(_session, player, home, _options, PlayerbotHomeTeleportReason.Island, now))
            {
                DropIntent();
                _goal = PlayerbotGoalKind.Home;
                return true;
            }
        }

        return TryTeleportToBindPoint(player, "stalled on ground no route leaves", urgent: false);
    }

    /// <summary>What counts as progress for the stall watch besides moving.</summary>
    private PlayerbotStallWatch.Fingerprint Fingerprint(Player player)
    {
        uint items = 0;
        foreach (Item item in player.Inventory.AllItems) items += item.Count;
        int quests = 0, rewarded = 0;
        uint objectives = 0;
        if (_session.Services.GetService<Npc.QuestNpcFeature>()?.Services.StateOf(player) is { Loaded: true } state)
        {
            foreach (ArcaneCore.Game.Quests.QuestStatusData status in state.Quests.Statuses.Values)
            {
                quests++;
                if (status.Rewarded) rewarded++;
                foreach (uint count in status.CreatureOrGOCount) objectives += count;
            }
        }

        int spells = _session.Services.GetService<SpellFeature>()?.Spellbook.GetSpells(player).Count ?? 0;
        // Resting is standing still by design: health or mana rising above the best seen during the rest is its progress.
        ulong vitals = (ulong)player.Health + (player.PowerType == PowerType.Mana ? ArcaneCore.Game.Spells.SpellSystem.GetPower(player, PowerType.Mana) : 0u);
        _restMark = _goal == PlayerbotGoalKind.Rest ? Math.Max(_restMark, vitals) : 0;
        return new(player.Level, player.GetUInt32(UpdateFields.PlayerXp), player.Money, items, quests, rewarded, objectives, spells, _restMark);
    }

    /// <summary>
    /// With the risk estimate on, a bot killed within <see cref="DeadlyErrandMs"/> of walking an errand (the way to a trainer,
    /// vendor or quest destination led through something that killed it) sets that errand aside at the first death, not only in a
    /// death loop (<see cref="RecordDeath"/>).
    /// </summary>
    internal const uint DeadlyErrandMs = 30_000;

    /// <summary>The goals set aside for a while (stalls, deadly errands; inspection and tests).</summary>
    internal PlayerbotSuspensions Suspensions => _suspensions;

    /// <summary>The quest exchanges (inspection and tests).</summary>
    internal PlayerbotQuestGoals QuestGoals => _quests;

    /// <summary>Set the goal the brain holds, as its decisions would (tests of <see cref="GiveUpStalledGoal"/>).</summary>
    internal void HoldGoalForTests(PlayerbotGoalKind goal, uint entry, uint quest)
    {
        _goal = goal;
        TargetEntry = entry;
        QuestId = quest;
    }

    /// <summary>Keep the errand the bot is on (each think; tests call it directly).</summary>
    internal void NoteErrand(PlayerbotGoalKind goal, uint entry, uint quest)
    {
        if (goal is PlayerbotGoalKind.Quest or PlayerbotGoalKind.Train or PlayerbotGoalKind.Vendor && (entry != 0 || quest != 0))
            _errand = (goal, entry, quest, _session.World.NowMs);
    }

    private void RetireDeathIntent()
    {
        // Death starts a new gameplay lifetime. Drop only controller-owned, transient
        // intent; the recovery helper still owns the ordinary release/reclaim flow.
        _target = null;
        TargetEntry = 0;
        _route = null;
        _attacking = false;
        _lootOpened = false;
        _lootResponseReceived = false;
        _lootActions.Clear();
        _restUntilMs = 0;
        _restKind = default;
        _needsRest = false;
        _satToWait = false;
        _thinkElapsed = 0;
        _combatSpells.Reset();
    }

    private void RetireIdleCombatRoute()
    {
        // Service/quest controllers own separate routes. Do not later resume a
        // previous grind path from a different position after their work finishes.
        _target = null;
        _route = null;
        _attacking = false;
    }

    private void DropIntent()
    {
        _target = null;
        _route = null;
        _attacking = false;
        _lootOpened = false;
        TargetEntry = 0;
    }

    /// <param name="wantedPct">Eat or drink below this percentage (the ordinary 45/35 when 0).</param>
    private void Rest(Player player, uint wantedPct = 0)
    {
        SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
        if (spellFeature is null || player.Combat.IsInCombat
            || spellFeature.System.GetState(player.Guid)?.CurrentCast is
                { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }
            || PlayerbotConsumables.HasActiveFoodDrink(player, spellFeature.System)
            || !PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, wantedPct == 0 ? 45 : wantedPct,
                wantedPct == 0 ? 35 : wantedPct, out PlayerbotConsumable consumable))
        { _needsRest = false; return; }

        Item item = consumable.Item;

        _goal = PlayerbotGoalKind.Rest;
        _needsRest = true;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        { PlayerbotMovementControl.Stop(_session, player); return; }
        if (player.StandState != StandState.Sit)
        { _session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(1u)); return; }
        if (!_session.TryManagedAction(WorldOpcode.CmsgUseItem,
            PlayerbotNavigation.UseItemPayload(item.BagSlot, item.Slot))) return;
        _restKind = consumable.Kind;
        _restUntilMs = unchecked(_session.World.NowMs + 20_000);
        _needsRest = false;
    }

    private bool LootManagedPackets(ulong target)
    {
        bool opened = false;
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Opcode != WorldOpcode.SmsgLootResponse || packet.Payload.Length < 14)
                continue;

            var reader = new PacketReader(packet.Payload);
            if (reader.ReadUInt64() != target || reader.ReadByte() != 1)
                continue;
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
                byte slotType = reader.ReadByte();
                if (slotType == 0)
                    _lootActions.Enqueue((WorldOpcode.CmsgAutostoreLootItem, [slot]));
            }

            _lootActions.Enqueue((WorldOpcode.CmsgLootMoney, []));
            _lootActions.Enqueue((WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(target)));
        }
        return opened;
    }

    internal static Creature? FindTarget(Player player, uint preferredEntry = 0, Func<Creature, bool>? skip = null)
        => FindTargets(player, preferredEntry, skip).FirstOrDefault();

    /// <summary>
    /// The candidates <see cref="FindTarget"/> chooses from, best first (the quest objective, then the nearest). A non-objective
    /// may be at most <paramref name="maxLevelsAbove"/> levels above the bot (1 without the risk estimate); the named quest
    /// objective may be any level when the risk estimate weighs it (<paramref name="maxLevelsAbove"/> above 1).
    /// </summary>
    internal static IEnumerable<Creature> FindTargets(Player player, uint preferredEntry = 0, Func<Creature, bool>? skip = null,
        int maxLevelsAbove = 1)
    {
        if (player.Map is not { } map)
            return [];

        GroupManager? groups = (player.Session as WorldSession)?.Services.GetService<Social.SocialFeature>()?.Context.Groups;
        uint grayLevel = ArcaneCore.Game.Progression.ExperienceFormulas.GrayLevel(player.Level);
        return player.VisibleObjects
            .Select(guid => map.FindObject(guid))
            .OfType<Creature>()
            .Where(creature => skip is null || !skip(creature))
            .Where(creature => creature.IsAlive && map.Combat.Hooks.CanAttack(player, creature))
            .Where(creature => creature.Level <= player.Level + maxLevelsAbove
                || maxLevelsAbove > 1 && preferredEntry != 0 && creature.Entry == preferredEntry)
            // Grey creatures give no experience (XP::GetGrayLevel); idle grinding leaves them alone. A named quest
            // objective is still allowed: the quest asks for that creature whatever its level.
            .Where(creature => preferredEntry != 0 && creature.Entry == preferredEntry || creature.Level > grayLevel)
            .Where(creature => !IsSomeoneElses(player, creature, groups))
            .Where(creature => preferredEntry != 0 ? creature.Entry == preferredEntry : creature.Template.CreatureType != 8)
            // Ordinary idle grinding must not initiate attacks on town/service NPCs.
            // Explicit quest objectives and the existing defensive-victim path keep
            // their normal authoritative combat checks.
            .Where(creature => preferredEntry != 0 || creature.Template.NpcFlags == 0)
            .Where(creature => float.IsFinite(creature.X) && float.IsFinite(creature.Y) && float.IsFinite(creature.Z))
            .OrderBy(creature => preferredEntry != 0 && creature.Entry == preferredEntry ? 0 : 1)
            .ThenBy(creature => Distance(player, creature));
    }

    /// <summary>Whether <paramref name="target"/> is inside the arc the server swings in (<see cref="CombatConstants.AutoAttackArc"/>).</summary>
    private static bool FacesTarget(Player player, Creature target)
        => MathF.Sqrt(MathF.Pow(target.X - player.X, 2) + MathF.Pow(target.Y - player.Y, 2)) <= CombatConstants.NoFacingChecksDistance
            || MapCombat.HasInArc(player, target, CombatConstants.AutoAttackArc);

    /// <summary>
    /// Where a bot fights its target from (vmangos PartyBotAI.cpp:755-766, GetDistancingTarget / RunAwayFromTarget :146-185 and the
    /// caster chase distance of SetCasterChaseDistance): a bot whose preferred range is melee closes to melee; a caster, healer or
    /// hunter closes only to its preferred range and holds there. A hunter inside its 8-yard Auto Shot dead zone, a ranged bot the
    /// enemy already reached in melee, and a ranged bot that found nothing to cast for a while at a target that is not fighting it
    /// fight in melee instead; nobody ranged otherwise runs into melee.
    /// <para>
    /// <paramref name="inMeleeReach"/> is the server's own swing reach (<see cref="MapCombat.CanReachWithMeleeAutoAttack"/>: the 2D
    /// distance within the combat reach and less than 6 yards of height between them). A target that reach allows is in melee whatever
    /// the 3D <paramref name="distance"/> says: on a slope the 3D distance stays above <see cref="MeleeRange"/> although the bot already
    /// stands at the target (2 yards apart, 3.9 yards of height), the chase has nowhere closer to go, and the bot stood there without
    /// swinging while the creature, which the same reach rule lets hit, wore it down (Dawnrover and the Kobold Laborers on the Elwynn
    /// hillside, ttk=860s reason=behind in the 2026-10-08 replays).
    /// </para>
    /// </summary>
    internal static PlayerbotFightPosition DecidePosition(float preferredRange, float distance, bool hunter, bool targetOnBot,
        bool idleTooLong, bool inMeleeReach = false)
    {
        bool closeEnough = distance <= MeleeRange || inMeleeReach;
        bool melee = preferredRange <= MeleeRange
            || (hunter && distance < PlayerbotClassRotation.HunterDeadZone)
            || (closeEnough && targetOnBot)
            || idleTooLong;
        if (!melee) return distance > preferredRange ? PlayerbotFightPosition.ChaseToRange : PlayerbotFightPosition.Hold;
        return closeEnough ? PlayerbotFightPosition.Melee : PlayerbotFightPosition.ChaseToMelee;
    }

    /// <summary>
    /// Whether <paramref name="creature"/> belongs to another player's fight: tapped by someone outside the bot's group (the
    /// client's grey name: UNIT_DYNFLAG_TAPPED without TAPPED_BY_PLAYER as the bot sees it, LootService's viewer filter over
    /// Creature.LootTapPlayerGuid), or fighting a player who is neither the bot nor in its group (its victim, or any player on
    /// its threat list). vmangos bots take such targets only from their leader's fight (PartyBotAI::SelectAttackTarget).
    /// </summary>
    internal static bool IsSomeoneElses(Player player, Creature creature, GroupManager? groups)
    {
        uint seen = creature.GetValueFor(UpdateFields.UnitDynamicFlags, player);
        if ((seen & Game.Loot.LootService.UnitDynFlagTapped) != 0 && (seen & Game.Loot.LootService.UnitDynFlagTappedByPlayer) == 0)
            return true;
        if (creature.Combat.Victim is Player victim && IsStranger(player, victim, groups))
            return true;
        if (!creature.Combat.HasThreatList) return false;
        foreach (ThreatEntry entry in creature.Combat.Threat.Entries)
        {
            if (entry.Target is Player other && IsStranger(player, other, groups))
                return true;
        }
        return false;
    }

    private static bool IsStranger(Player player, Player other, GroupManager? groups)
        => other.Guid != player.Guid && groups?.AreInSameGroup(player.Guid, other.Guid) != true;

    private static Creature? FindDefensiveAttacker(Player player)
    {
        if (player.Map is not { } map) return null;
        return player.Combat.Attackers
            .OfType<Creature>()
            .Where(creature => creature.IsInWorld && creature.IsAlive
                && ReferenceEquals(creature.Map, map)
                && map.Combat.Hooks.CanAttack(player, creature))
            .OrderBy(creature => Distance(player, creature))
            .FirstOrDefault();
    }

    private void UpdateLocalPlan(Player player)
    {
        if (planner is null || !_options.AllowLocalLlm || _planningStop.IsCancellationRequested) return;
        if (_planTask?.IsCompleted == true)
        {
            _modelChoice = _planTask.IsCompletedSuccessfully ? _planTask.Result : null;
            _planTask = null;
        }
        long now = (long)_session.World.Uptime.TotalMilliseconds;
        if (_planTask is not null || now < _nextPlanMs) return;
        _nextPlanMs = now + 5000;
        var candidates = new List<PlayerbotPlanCandidate>();
        if (_quests.HasCandidate(player)) candidates.Add(new("quest", PlayerbotGoalKind.Quest, _quests.TargetEntry, _quests.QuestId));
        if (_town.HasCandidate(player)) candidates.Add(new("town", PlayerbotGoalKind.Vendor, _town.TargetEntry, 0));
        if (FindTarget(player, _quests.PreferredCreatureEntry, IsSkipped) is { } target) candidates.Add(new("grind", PlayerbotGoalKind.Grind, target.Entry, QuestId));
        candidates.Add(new("explore", PlayerbotGoalKind.Explore, 0, 0));
        var facts = new PlayerbotPlannerFacts(player.Level, player.Health, player.MaxHealth, player.Combat.IsInCombat,
            player.MapId, (uint)(_session.Services.GetService<SpellFeature>()?.Spellbook.GetSpells(player).Count ?? 0),
            player.Inventory.GetItemCount(117));
        CancellationToken token = _planningStop.Token;
        _planTask = Task.Run(() => planner.SelectAsync(facts, candidates, token), token);
    }

    private void MarkUnreachable(Creature creature)
    {
        uint now = _session.World.NowMs;
        foreach (ObjectGuid expired in _unreachable.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            _unreachable.Remove(expired);
        if (_unreachable.Count < 64) _unreachable[creature.Guid] = unchecked(now + UnreachableTargetMs);
    }

    private bool IsUnreachable(Creature creature)
        => _unreachable.TryGetValue(creature.Guid, out uint until) && unchecked((int)(until - _session.World.NowMs)) > 0;

    /// <summary>
    /// A creature the goals do not choose now: unreachable, of an entry set aside (stall, death loop), or outside the bot's home area
    /// (outside the circle or, for a zone-clipped home, out of the home's zone: <see cref="PlayerbotHomeArea.Contains(Unit)"/>; a
    /// creature on another map than the home is not judged: the bot is off-map and behaves as one with no home). This holds wherever
    /// the bot is on the home map, so a bot away on an errand (a vendor in Stormwind) picks no fight on the road (fail closed).
    /// The defensive-attacker path does not use this: a bot always fights back.
    /// </summary>
    private bool IsSkipped(Creature creature)
        => IsUnreachable(creature) || _suspensions.IsEntrySuspended(creature.Entry, _session.World.NowMs)
            || _home is { } home && creature.MapId == home.MapId && !home.Contains(creature);

    private void RememberAttackers(Player player)
    {
        NoteErrand(_goal, TargetEntry, QuestId);
        _attackerEntries.Clear();
        _attackers.Clear();
        foreach (Unit unit in player.Combat.Attackers.Concat(player.Combat.ThreatenedBy))
            if (unit is Creature creature && _attackerEntries.Add(creature.Entry) | !_attackers.Contains((creature.Guid, creature.Entry)))
                _attackers.Add((creature.Guid, creature.Entry));
    }

    /// <summary>
    /// The bot just died (<see cref="PlayerbotStallWatch.RecordDeath"/>): a creature entry that has now killed it twice is set aside
    /// for the goals, and a death loop takes the spirit healer for this death instead of the body in the same place again, and sets
    /// aside the errand it was last travelling for (its quest and NPC, every trainer for a trainer visit): the route to it led
    /// through the place (replay of the 2026-10-08 rehearsal: without that, Dawnrover walked from the Goldshire graveyard to its
    /// next trainer in Stormwind past the Scourge invasion point and died on the road again). A second death within
    /// <see cref="PlayerbotStallWatch.DeathLoopYards"/> of an earlier one in the window sets the errand aside the same way (its
    /// recovery takes the spirit healer when one can be had, <see cref="JudgeBody"/>).
    /// </summary>
    private void RecordDeath(Player player)
    {
        uint now = _session.World.NowMs;
        (bool loop, IReadOnlyList<uint> setAside) = _stall.RecordDeath(now, player.MapId,
            new System.Numerics.Vector3(player.X, player.Y, player.Z), _attackerEntries);
        _attackerEntries.Clear();
        Risk.OnDeath(player, _attackers);
        _attackers.Clear();
        foreach (uint entry in setAside) _suspensions.SuspendEntry(entry, now);
        if (loop) StallOutsideHome = false; // the death loop is the report now; the mark was the last living stall's
        // A second death where the bot already died (the repeat death that sends a ghost to the spirit healer, JudgeBody) sets the
        // errand that led there aside as a death loop does, so the revived bot does not walk straight back.
        bool repeat = loop || _stall.DeathsNear(player.MapId, new System.Numerics.Vector3(player.X, player.Y, player.Z), now) >= 2;
        if (!repeat)
        {
            if (_options.Risk.Enabled && _errand.Entry != 0 && unchecked(now - _errand.AtMs) <= DeadlyErrandMs)
            {
                _suspensions.SuspendEntry(_errand.Entry, now);
                if (_errand.Goal == PlayerbotGoalKind.Quest) _suspensions.SuspendQuest(_errand.Quest, now);
                _errand = default;
            }

            return;
        }

        if (loop) _recovery.TakeSpiritHealer();
        if (_errand.Goal == PlayerbotGoalKind.Quest) _suspensions.SuspendQuest(_errand.Quest, now);
        if (_errand.Goal == PlayerbotGoalKind.Train) _suspensions.SuspendTraining(now);
        _suspensions.SuspendEntry(_errand.Entry, now);
        _errand = default;
    }

    /// <summary>
    /// Whether this ghost's body is unsafe to reclaim (<see cref="PlayerbotRecovery.UnsafeBody"/>, <see cref="PlayerbotRecovery.JudgeBody"/>):
    /// the bot already died within <see cref="PlayerbotStallWatch.DeathLoopYards"/> of it inside the death watch's window (this death
    /// and an earlier one, <see cref="PlayerbotStallWatch.DeathsNear"/>: the third would be a death loop, which takes the healer
    /// already); a creature round it is too strong for the bot (<see cref="PlayerbotRisk.TooStrongNear"/>, with the estimate on); or it
    /// lies outside the home while the spirit healer leaves the bot inside the home or at least
    /// <see cref="PlayerbotRecovery.OutsideHomeGainYards"/> nearer its centre
    /// (<see cref="PlayerbotRecovery.SpiritHealerLanding"/>). Live 2026-10-11: Wfbotaz (level 14, homed at Sentinel Hill) reclaimed its
    /// body among Raven Hill's level 20+ skeletons and spiders again and again, two death loops in fifteen minutes (world.log
    /// 05:53:21 and 06:00:42, attackers 202/531/569 and 202/217/569).
    /// <para>
    /// Tightened after the live run of 2026-10-11 (08:04-08:10 UTC, world.log): 80 bodies were given up in six minutes (31 too strong,
    /// 10 died there, 39 outside the home), each costing resurrection sickness and a quarter of the durability. A repeat death counts
    /// only while a creature that would attack the revived bot still stands in reach of the body (a cleared spot is safe whatever
    /// happened there; the third death is a death loop, which asks for the healer anyway). Too strong is a matter of strength, not of a
    /// pull's worth (<see cref="PlayerbotRisk.TooStrongNear"/>). A body outside the home counts only when the home return could not
    /// teleport the revived bot home (<see cref="PlayerbotHomeReturnMode.Walk"/>, or within <see cref="PlayerbotHomeReturn.TeleportCooldownMs"/>
    /// of its last return teleport): otherwise the bot reclaims the body and the Auto return walks or teleports it home for nothing.
    /// </para>
    /// </summary>
    private (PlayerbotUnsafeBody Reason, string? Detail) JudgeBody(Player player, Corpse corpse)
    {
        var body = new System.Numerics.Vector3(corpse.X, corpse.Y, corpse.Z);
        int deaths = _stall.DeathsNear(corpse.MapId, body, _session.World.NowMs);
        bool diedHere = deaths >= 2 && PlayerbotRecovery.Threats(player).Concat(Risk.HazardThreats(player))
            .Any(threat => threat.Reaches(body, PlayerbotRecovery.CampMarginYards));
        PlayerbotEngagement? strong = diedHere || !_options.Risk.Enabled ? null : Risk.TooStrongNear(player, body);
        bool outside = false, nearer = false;
        float bodyFromHome = float.NaN, healerFromHome = float.NaN;
        bool teleportHome = _options.Home.ReturnMode != PlayerbotHomeReturnMode.Walk && _homeReturn.CanTeleportNow(_session.World.NowMs);
        if (!diedHere && strong is null && !teleportHome && _home is { } home && corpse.MapId == home.MapId
            && !home.Contains(corpse.MapId, corpse.X, corpse.Y, corpse.Z))
        {
            outside = true;
            bodyFromHome = home.DistanceTo(corpse.X, corpse.Y);
            if (_recovery.SpiritHealerLanding(player, corpse) is { } landing)
            {
                healerFromHome = home.DistanceTo(landing.X, landing.Y);
                nearer = home.Contains(home.MapId, landing.X, landing.Y, landing.Z)
                    || healerFromHome + PlayerbotRecovery.OutsideHomeGainYards <= bodyFromHome;
            }
        }

        PlayerbotUnsafeBody reason = PlayerbotRecovery.JudgeBody(diedHere, strong is not null, outside, nearer);
        string? detail = reason switch
        {
            PlayerbotUnsafeBody.DiedHere => string.Create(CultureInfo.InvariantCulture,
                $"{deaths} deaths within {PlayerbotStallWatch.DeathLoopYards:F0} yd in {PlayerbotStallWatch.DeathLoopWindowMs / 1000}s"),
            PlayerbotUnsafeBody.TooStrong => strong.ToString(),
            PlayerbotUnsafeBody.OutsideHome => string.Create(CultureInfo.InvariantCulture,
                $"body {bodyFromHome:F0} yd from the home's centre, healer {healerFromHome:F0} yd"),
            _ => null,
        };
        return (reason, detail);
    }

    /// <summary>
    /// The spirit healer just revived the bot among creatures too strong for it (<see cref="PlayerbotRecovery.EscapeLanding"/>), in the
    /// same think, out of combat: a homed bot teleports to its home's centre (<see cref="PlayerbotHomeTeleportReason.UnsafeLanding"/>,
    /// past the cooldown), a bot without a home (or whose home teleport is refused) to its bind point, where its hearthstone leads
    /// (<see cref="TeleportService.TeleportToHomebind"/>, vmangos <c>Player::TeleportToHomebind</c>; the tram's way out uses the same).
    /// The errand that led to the place was set aside at the death (<see cref="RecordDeath"/>). Live 2026-10-11: Wfbotfe (homed) and
    /// Dawnrover (unhomed, death loops at Raven Hill at 08:23:32 and 08:27:47, then a fault at 08:30:25) were revived at the Raven Hill
    /// graveyard among its Skeletal Horrors, attacked before their next think, and died there again and again. True when the bot was moved.
    /// </summary>
    private bool EscapeLanding(Player player)
    {
        uint now = _session.World.NowMs;
        if (!player.IsAlive || player.Combat.IsInCombat) return false;
        if (_home is { } home)
        {
            _homeReturn.Evaluate(player, home, now);
            if (_homeReturn.TryTeleport(_session, player, home, _options, PlayerbotHomeTeleportReason.UnsafeLanding, now, urgent: true))
            {
                DropIntent();
                _goal = PlayerbotGoalKind.Home;
                return true;
            }
        }

        // The near teleport pending now is the resurrection's own, into the landing: the escape replaces it (urgent).
        return TryTeleportToBindPoint(player, "was revived by the spirit healer among creatures too strong for it", urgent: true);
    }

    /// <summary>The shortest time between two of the brain's own bind-point teleports that are not urgent (<see cref="TryTeleportToBindPoint"/>).</summary>
    internal const uint BindTeleportCooldownMs = PlayerbotHomeReturn.TeleportCooldownMs;

    private uint _bindTeleportAt;
    private bool _bindTeleported;

    /// <summary>
    /// Why the bot may not teleport to its bind point now, or null: the rules of the return teleport home (PlayerbotHomeReturn.RefusalOf
    /// and TryTeleport; vmangos PartyBotAI.cpp:808) for a teleport the brain makes of its own accord. <c>TeleportService.TeleportToHomebind</c>
    /// checks nothing itself, and a bot can run this brain while in a group (PlayerbotPartyAI.Drives is false while it never engaged and no
    /// real member is online): it must not be pulled away from its group, out of a duel or a battleground, nor in the air. A teleport
    /// under way is waited for, except (<paramref name="urgent"/>) a near one, which the next teleport replaces (the resurrection's own into
    /// an unsafe landing). Not urgent, at most one every <see cref="BindTeleportCooldownMs"/>: a bind point that is itself an island cannot
    /// loop the bot through it at every stall.
    /// </summary>
    private string? BindTeleportRefusal(Player player, TeleportService? teleports, bool urgent, uint now)
    {
        if (teleports is null || !_session.IsManaged) return "no teleport service";
        HomeBind bind = player.Home;
        if (bind == default) return "no bind point";
        if (!player.IsAlive) return "dead";
        if (player.Combat.IsInCombat) return "in combat";
        if (player.Duel is not null) return "in a duel";
        if (_session.Services.GetService<ArcaneCore.World.Social.SocialFeature>()?.Context.Groups.GetGroup(player.Guid) is not null) return "in a group";
        if (player.Map?.Template is { IsBattleground: true }) return "on a battleground";
        if ((player.Movement.Flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) != 0) return "in the air";
        ArcaneCore.Kernel.WorldData.MapTemplate? template = ArcaneCore.Game.Maps.Templates.WorldMaps.Of(_session.World).Registry.Find(bind.MapId);
        if (template is null || template.IsBattleground) return "bind map unknown";
        if (!PlayerbotMapPolicy.Allows(_options.AllowedMaps, bind.MapId, template, current: false)) return "bind map not allowed";
        if (teleports.IsBeingTeleported(player) && !(urgent && teleports.IsBeingTeleportedNear(player))) return "a teleport under way";
        if (!urgent && _bindTeleported && unchecked(now - _bindTeleportAt) < BindTeleportCooldownMs)
            return "teleported to its bind point less than 5 minutes ago";
        return null;
    }

    /// <summary>
    /// Teleport the bot to its bind point (where its hearthstone leads; vmangos <c>Player::TeleportToHomebind</c>) when
    /// <see cref="BindTeleportRefusal"/> allows it, and log why (<paramref name="why"/>). True when the bot was sent; its intent is dropped
    /// and it explores from there.
    /// </summary>
    private bool TryTeleportToBindPoint(Player player, string why, bool urgent)
    {
        uint now = _session.World.NowMs;
        TeleportService? teleports = _session.Services.GetService<ArcaneCore.World.Teleport.TeleportFeature>()?.Teleports;
        ILogger? logger = _session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotBrain>();
        var from = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        HomeBind bind = player.Home;
        if (BindTeleportRefusal(player, teleports, urgent, now) is { } refusal)
        {
            logger?.LogDebug("Playerbot {Bot} {Why} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: does not teleport to its bind point ({Refusal})",
                player.Name, why, from.X, from.Y, from.Z, player.MapId, refusal);
            return false;
        }

        if (!teleports!.TeleportToHomebind(player)) return false;
        _bindTeleported = true;
        _bindTeleportAt = now;
        DropIntent();
        _goal = PlayerbotGoalKind.Explore;
        logger?.LogInformation(
            "Playerbot {Bot} {Why} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: teleports to its bind point ({BX:F1}, {BY:F1}, {BZ:F1}) map {BMap}",
            player.Name, why, from.X, from.Y, from.Z, player.MapId, bind.X, bind.Y, bind.Z, bind.MapId);
        return true;
    }

    private void Explore(Player player, uint elapsedMs)
    {
        if (_route is { Complete: false })
        {
            _goal = PlayerbotGoalKind.Explore;
            if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, _session.World.NowMs))
                _route = null;
            return;
        }

        if (player.Map is null)
            return;
        // Wander onward: keep roughly the current facing (within +-60 degrees, golden-ratio spread so successive legs
        // differ). Only after repeated refusals turn anywhere. The old golden-angle-from-zero choice sent each leg
        // ~137 degrees from the last one, so a bot that lost its route every few thinks ran in circles.
        int attempt = _roamDirection++;
        float spread = (((attempt + (int)(player.Guid.Low % 7)) * 0.618034f) % 1f) - 0.5f;
        float angle = _exploreRefusals >= 3
            ? (attempt + player.Guid.Low) * 2.39996323f
            : player.Orientation + (spread * MathF.PI * 2f / 3f);
        // Beyond 0.8 of the home radius the next leg heads back in instead of wandering further out; so does a leg that would end
        // out of a clipped home's zone (a wander across the Westfall-Duskwood border inside the circle). Back in is the centre first,
        // then, while legs are refused, a fan around it (InwardLeg).
        var destination = new System.Numerics.Vector3(
            player.X + (MathF.Cos(angle) * 48f),
            player.Y + (MathF.Sin(angle) * 48f),
            player.Z);
        if (_home is { } area && (player.Map?.MapId ?? player.MapId) == area.MapId
            && (area.DistanceTo(player.X, player.Y) > area.ExploreRadius
                || area.ZoneClipped && !area.InZone(destination.X, destination.Y, destination.Z)))
            destination = InwardLeg(area, player, _exploreRefusals);
        if (PlayerbotNavigation.TryPlanToward(player, destination, _options, out _route))
        {
            _exploreRefusals = 0;
            _goal = PlayerbotGoalKind.Explore;
            if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, elapsedMs, _session.World.NowMs))
                _route = null;
        }
        else
        {
            _exploreRefusals++;
            PlayerbotMovementControl.Stop(_session, player);
        }
    }

    /// <summary>
    /// The turns from the straight way to the home's centre that an exploring bot heading back in tries, one more per refused leg
    /// (<see cref="InwardLeg"/>). Before, every leg there aimed straight at the centre, the refusals' turn and the stall watch's
    /// give-up included, so one refused leg was refused on every think. Live 2026-10-11 (bot QA issue #154, 200 bots homed at Sentinel
    /// Hill, radius 550): 74 of the run's 204 stalls lay 460 to 480 yards from the centre, just past the 440-yard explore radius;
    /// replayed from the live characters database on the real terrain, the bots stood there with that one leg on the motion's loop
    /// blacklist (<see cref="PlayerbotMotion.LoopBlacklistMs"/>), refused hundreds of times a minute.
    /// </summary>
    internal static readonly float[] InwardFanDegrees = [0f, 25f, -25f, 50f, -50f, 75f, -75f];

    /// <summary>The longest exploring leg.</summary>
    internal const float ExploreLegYards = 48f;

    /// <summary>
    /// The end of an exploring bot's leg back in after <paramref name="refusals"/> refused legs: the first turn of
    /// <see cref="InwardFanDegrees"/> from that one on whose leg ends strictly closer to the centre than the bot stands and, for a
    /// clipped home, in its zone. A leg turned θ off the centre is at most the bot's distance × cos θ long (the point of that line
    /// nearest the centre), so it never ends farther out: a full 48-yard leg 75 degrees off from 55 yards out would end 62.9 yards
    /// out, past a 60-yard radius. When no turn qualifies (the bot at the centre, every turn out of the zone), the straight leg
    /// towards the centre.
    /// </summary>
    internal static System.Numerics.Vector3 InwardLeg(PlayerbotHomeArea home, Player player, int refusals)
    {
        float distance = home.DistanceTo(player.X, player.Y);
        float inward = MathF.Atan2(home.Centre.Y - player.Y, home.Centre.X - player.X);
        for (int turn = 0; turn < InwardFanDegrees.Length; turn++)
        {
            float degrees = InwardFanDegrees[(int)((uint)(refusals + turn) % (uint)InwardFanDegrees.Length)];
            float length = MathF.Min(ExploreLegYards, distance * MathF.Cos(degrees * MathF.PI / 180f));
            if (length < 1f) continue;
            float angle = inward + (degrees * MathF.PI / 180f);
            var end = new System.Numerics.Vector3(player.X + (MathF.Cos(angle) * length), player.Y + (MathF.Sin(angle) * length), player.Z);
            if (home.DistanceTo(end.X, end.Y) >= distance) continue;
            if (home.ZoneClipped && !home.InZone(end.X, end.Y, end.Z)) continue;
            return end;
        }

        float straight = MathF.Min(ExploreLegYards, distance);
        return new System.Numerics.Vector3(player.X + (MathF.Cos(inward) * straight), player.Y + (MathF.Sin(inward) * straight), player.Z);
    }

    private static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));
}

/// <summary>Where a bot fights from this decision (<see cref="PlayerbotBrain.DecidePosition"/>).</summary>
internal enum PlayerbotFightPosition : byte
{
    /// <summary>At its range: stand and cast.</summary>
    Hold,

    /// <summary>Beyond its range: close in to it.</summary>
    ChaseToRange,

    /// <summary>A melee fight out of reach: close in to melee.</summary>
    ChaseToMelee,

    /// <summary>In reach of a melee fight: swing.</summary>
    Melee,
}
