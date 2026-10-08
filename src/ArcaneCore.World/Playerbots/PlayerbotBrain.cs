using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A deterministic, world-thread autonomous player. The feature/host owns scheduling and
/// persistence; this controller owns only bounded ordinary gameplay decisions for one session.
/// </summary>
internal sealed class PlayerbotBrain(WorldSession session, PlayerbotOptions options,
    PlayerbotLocalPlanner? planner = null, CancellationToken lifetime = default)
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
    private readonly PlayerbotTrainerDestinations _trainers = new(session, options);
    private readonly PlayerbotWorldDestinations _destinations = new(session, options);
    private readonly PlayerbotCombatSpells _combatSpells = new(session);
    private readonly PlayerbotRecovery _recovery = new(session, options);
    private readonly PlayerbotEquipment _equipment = new(session);
    private readonly CancellationTokenSource _planningStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task<string?>? _planTask;
    private string? _modelChoice;
    private long _nextPlanMs;
    private bool _needsRest;
    private bool _deathIntentRetired;
    private int _stopped;
    // Creatures the bot could not path to (or looped trying to reach): skipped for a while, so the next think does
    // not pick the same nearest unreachable target again.
    private readonly Dictionary<ObjectGuid, uint> _unreachable = [];
    private const uint UnreachableTargetMs = 30_000;
    private const float MeleeRange = PlayerbotClassRotation.MeleeRange;
    // A ranged bot standing at its range without anything to cast at a target that is not fighting it closes in after this long.
    private const uint RangedIdleLimitMs = 8_000;
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

    internal void Update(uint elapsedMs)
    {
        if (!_options.Enabled || _stopped != 0 || _session.Player is not { } player)
            return;
        if (!player.IsInWorld) return;

        bool dead = !player.IsAlive;
        if (dead)
        {
            if (!_deathIntentRetired)
            {
                RetireDeathIntent();
                _deathIntentRetired = true;
            }
            _goal = PlayerbotGoalKind.Recover;
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
                RetireDeathIntent();
                _deathIntentRetired = true;
            }
            _goal = PlayerbotGoalKind.Recover;
            _recovery.Update(player, interval);
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
            { TargetEntry = _quests.TargetEntry; return; }
            _target = null;
            _route = null;
            if (_destinations.Update(player, 0, interval, completedReturn)) TargetEntry = _destinations.TargetEntry;
            // Wait/retry at the real giver while combat or a normal interaction
            // refusal remains. A completed quest must not reopen opportunistic grind.
            return;
        }

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
            if (_needsRest || spellFeature is not null && PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, out _))
            {
                Rest(player);
                return;
            }
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
            _combatSpells.PullTarget = _target is { IsAlive: true } pull && ReferenceEquals(pull.Map, player.Map) ? pull : null;
            if (_combatSpells.UpdateOutOfCombat(player, interval)) return;
            UpdateLocalPlan(player);
            // A returned ID is only a proposal; each controller rechecks the live gameplay rules.
            if (_modelChoice == "town" && _town.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
            if (_quests.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _quests.Goal; QuestId = _quests.QuestId; TargetEntry = _quests.TargetEntry; return; }
            QuestId = _quests.QuestId;
            if (_town.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
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
            if (_modelChoice == "explore") { _target = null; Explore(player, interval); return; }
        }

        if (_target is null || _target.Map != player.Map)
        {
            Creature? previousTarget = _target;
            _target = FindTarget(player, _quests.PreferredCreatureEntry, IsUnreachable);
            _rangedIdleMs = 0;
            // Keep an exploration route across decisions; otherwise every thought changes
            // direction after only its first terrain step and the player never travels.
            if (_target is not null || previousTarget is not null) _route = null;
            _attacking = false;
            _lootOpened = false;
            if (_target is null)
            {
                if (_destinations.Update(player, _quests.PreferredCreatureEntry, interval))
                {
                    _route = null;
                    _goal = _quests.PreferredCreatureEntry != 0 ? PlayerbotGoalKind.Grind : PlayerbotGoalKind.Quest;
                    TargetEntry = _destinations.TargetEntry;
                    if (_destinations.QuestId != 0) QuestId = _destinations.QuestId;
                    return;
                }
                Explore(player, interval);
                return;
            }
            TargetEntry = _target.Entry;
        }
        Creature target = _target;

        TargetEntry = target.Entry;

        if (target.IsAlive)
        {
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
                _rangedIdleMs >= RangedIdleLimitMs && !fightingBot);
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

    private void Rest(Player player)
    {
        SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
        if (spellFeature is null || player.Combat.IsInCombat
            || spellFeature.System.GetState(player.Guid)?.CurrentCast is
                { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }
            || PlayerbotConsumables.HasActiveFoodDrink(player, spellFeature.System)
            || !PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, out PlayerbotConsumable consumable))
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
    {
        if (player.Map is not { } map)
            return null;

        GroupManager? groups = (player.Session as WorldSession)?.Services.GetService<Social.SocialFeature>()?.Context.Groups;
        uint grayLevel = ArcaneCore.Game.Progression.ExperienceFormulas.GrayLevel(player.Level);
        return player.VisibleObjects
            .Select(guid => map.FindObject(guid))
            .OfType<Creature>()
            .Where(creature => skip is null || !skip(creature))
            .Where(creature => creature.IsAlive && map.Combat.Hooks.CanAttack(player, creature))
            .Where(creature => creature.Level <= player.Level + 1)
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
            .ThenBy(creature => Distance(player, creature))
            .FirstOrDefault();
    }

    /// <summary>
    /// Where a bot fights its target from (vmangos PartyBotAI.cpp:755-766, GetDistancingTarget / RunAwayFromTarget :146-185 and the
    /// caster chase distance of SetCasterChaseDistance): a bot whose preferred range is melee closes to melee; a caster, healer or
    /// hunter closes only to its preferred range and holds there. A hunter inside its 8-yard Auto Shot dead zone, a ranged bot the
    /// enemy already reached in melee, and a ranged bot that found nothing to cast for a while at a target that is not fighting it
    /// fight in melee instead; nobody ranged otherwise runs into melee.
    /// </summary>
    internal static PlayerbotFightPosition DecidePosition(float preferredRange, float distance, bool hunter, bool targetOnBot,
        bool idleTooLong)
    {
        bool melee = preferredRange <= MeleeRange
            || (hunter && distance < PlayerbotClassRotation.HunterDeadZone)
            || (distance <= MeleeRange && targetOnBot)
            || idleTooLong;
        if (!melee) return distance > preferredRange ? PlayerbotFightPosition.ChaseToRange : PlayerbotFightPosition.Hold;
        return distance > MeleeRange ? PlayerbotFightPosition.ChaseToMelee : PlayerbotFightPosition.Melee;
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
        if (FindTarget(player, _quests.PreferredCreatureEntry) is { } target) candidates.Add(new("grind", PlayerbotGoalKind.Grind, target.Entry, QuestId));
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
        var destination = new System.Numerics.Vector3(
            player.X + (MathF.Cos(angle) * 48f),
            player.Y + (MathF.Sin(angle) * 48f),
            player.Z);
        if (PlayerbotNavigation.TryPlan(player, destination, _options, out _route))
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
