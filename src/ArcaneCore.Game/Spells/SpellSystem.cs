using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell system of the world: cast pipeline (vmangos Spell::prepare → cast → update →
/// finish/cancel), cooldowns and global cooldowns, power costs, effect dispatch and the aura
/// framework. One instance serves every map; all members run on the world thread.
/// <para>
/// Per-unit state lives here, keyed by GUID, instead of on <see cref="Unit"/> so the shared
/// object model stays untouched (docs/integration/spells.md); a unit's state is dropped once it
/// leaves the world.
/// </para>
/// </summary>
public sealed partial class SpellSystem
{
    private volatile SpellStore _store;
    private readonly Dictionary<ObjectGuid, UnitSpellState> _states = [];
    private readonly Func<uint> _clock;
    private readonly ILogger _logger;
    private readonly HashSet<(string Kind, uint Value)> _reportedUnsupported = [];
    private readonly Dictionary<SpellEffectName, SpellEffectHandler> _builtInEffectHandlers;

    public SpellSystem(
        SpellStore store,
        Func<uint> clock,
        ISpellUnitResolver? units = null,
        IDamageSink? damage = null,
        ITeleportSink? teleport = null,
        ISpellbook? spellbook = null,
        Random? random = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Units = units ?? new MapPlayerResolver();
        Damage = damage ?? new HealthOnlyDamageSink();
        Teleports = teleport ?? new NearTeleportSink();
        Spellbook = spellbook;
        Random = random ?? Random.Shared;
        _logger = logger ?? NullLogger.Instance;
        EffectHandlers = CreateEffectHandlers();
        _builtInEffectHandlers = new Dictionary<SpellEffectName, SpellEffectHandler>(EffectHandlers);
        AuraHandlers = CreateAuraHandlers();
        RegisterModules(SpellHandlerModules.BuiltIn);
    }

    /// <summary>
    /// The spell table, replaced as a whole by <c>.reload spell_template</c> (docs/areas/hot-reload.md).
    /// The store is immutable, so a reader sees either the old table or the new one; the volatile
    /// field publishes the swap to the session tasks that look spells up off the world thread.
    /// </summary>
    public SpellStore Store
    {
        get => _store;
        set => _store = value;
    }

    public ISpellUnitResolver Units { get; set; }

    public IDamageSink Damage { get; set; }

    public ITeleportSink Teleports { get; set; }

    /// <summary>
    /// A unit temporarily detached from its map during a player transfer (vmangos
    /// STATUS_TRANSFER). Retain its spell state while map simulation is paused.
    /// </summary>
    public Func<Unit, bool> IsInTransit { get; set; } = _ => false;

    public ISpellbook? Spellbook { get; set; }

    public Random Random { get; set; }

    /// <summary>
    /// The map update interval subtracted from global cooldowns (vmangos Player::AddGCD: "spell
    /// packets are handled on Map update so substract the update interval"; CONFIG_UINT32_INTERVAL_MAPUPDATE).
    /// </summary>
    public uint MapUpdateIntervalMs { get; set; } = 50;

    /// <summary>
    /// Raised on the world thread after a cast applied its effects to a target (caster, target,
    /// spell): vmangos Spell::DoAllEffectOnTarget → CreatureAI::SpellHit. Creature AI subscribes;
    /// a throwing handler is not caught here.
    /// </summary>
    public event Action<Unit, Unit, SpellInfo>? SpellHit;

    /// <summary>Units with live spell state.</summary>
    public int TrackedUnitCount => _states.Count;

    public uint NowMs => _clock();

    /// <summary>
    /// Whole Unix seconds (vmangos <c>time(nullptr)</c>) stamped on a holder when it is applied
    /// (<see cref="SpellAuraHolder.AppliedAtUnixSeconds"/>). The default is the system clock; a feature that compares
    /// the stamp with another wall-clock reading (the duel service) installs the clock it reads, so both sides agree.
    /// </summary>
    public Func<long> UnixSecondsClock { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The spell state of a unit, if it has any.</summary>
    public UnitSpellState? GetState(ObjectGuid guid) => _states.GetValueOrDefault(guid);

    // --- client requests ---------------------------------------------------------------

    /// <summary>
    /// CMSG_CAST_SPELL (vmangos WorldSession::HandleCastSpellOpcode): unknown spells, spells the
    /// player does not know and passive spells are ignored (logged, no reply); a negative spell
    /// explicitly aimed at oneself fails with BAD_TARGETS; everything else is prepared.
    /// </summary>
    public SpellCastResult HandleCastRequest(Player player, uint spellId, SpellCastTargets targets)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        if (spell is null)
        {
            return SpellCastResult.NotFound;
        }

        if (spell.IsPassive || (Spellbook is { } book && !book.HasSpell(player, spellId)))
        {
            _logger.LogWarning("{Player} cast spell {Spell} which they should not have", player.Name, spellId);
            return SpellCastResult.NotKnown;
        }

        if ((targets.Mask & SpellCastTargetFlags.Unit) != 0 && targets.Unit == player.Guid
            && IsExplicitUnitTarget(spell.Effects[0].TargetA) && !spell.IsPositive)
        {
            player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(spellId, SpellCastResult.BadTargets));
            return SpellCastResult.BadTargets;
        }

        return Prepare(player, spell, targets, triggered: false);
    }

    /// <summary>Cast a spell for the server (scripts, triggered spells, GM commands).</summary>
    public SpellCastResult CastSpell(Unit caster, uint spellId, SpellCastTargets targets, bool triggered)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        return spell is null ? SpellCastResult.NotFound : Prepare(caster, spell, targets, triggered);
    }

    /// <summary>
    /// CMSG_CANCEL_CAST (vmangos HandleCancelCastOpcode → InterruptNonMeleeSpells(false, spellId)):
    /// interrupt the cast in progress when it is <paramref name="spellId"/> (0 = any). Channels are
    /// not interrupted by this packet (withDelayed = false keeps vmangos' channel slot). A queued next-swing
    /// spell is cancelled as well, whatever <paramref name="spellId"/> is (SpellHandler.cpp:329-330 has no id
    /// filter on <c>InterruptSpell(CURRENT_MELEE_SPELL)</c>); this method is the only owner of that behaviour.
    /// </summary>
    public void CancelCast(Unit caster, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Preparing } cast
            && (spellId == 0 || cast.Spell.Id == spellId))
        {
            Cancel(cast);
        }

        // ranged (autorepeat lane): InterruptNonMeleeSpells(false, spellId) also takes the auto-repeat slot when the id matches.
        if (GetState(caster.Guid)?.AutoRepeatCast is { } autoRepeat && (spellId == 0 || autoRepeat.Spell.Id == spellId))
        {
            CancelAutoRepeat(caster);
        }

        CancelQueuedMeleeSpell(caster);
    }

    /// <summary>CMSG_CANCEL_CHANNELLING (vmangos HandleCancelChanneling): stop a non-triggered channel.</summary>
    public void CancelChannel(Unit caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Casting, IsTriggered: false } cast)
        {
            Cancel(cast);
        }
    }

    /// <summary>
    /// CMSG_CANCEL_AURA (vmangos HandleCancelAuraOpcode): only positive, displayed, cancelable,
    /// non-passive auras; a channeled spell's aura stops the channel instead.
    /// </summary>
    public void CancelAura(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        SpellInfo? spell = Store.Get(spellId);
        if (spell is null || spell.HasAttribute(SpellAttributes.NoAuraCancel) || spell.HasAttribute(SpellAttributes.DoNotDisplay)
            || spell.IsPassive || !spell.IsPositive)
        {
            return;
        }

        if (spell.IsChanneled)
        {
            if (GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Casting } cast && cast.Spell.Id == spellId)
            {
                Cancel(cast);
            }

            return;
        }

        RemoveAuras(player, spellId);
    }

    // --- world tick ---------------------------------------------------------------------

    /// <summary>Advance casts, channels, auras and cooldowns by <paramref name="diffMs"/> (world thread).</summary>
    public void Update(uint diffMs)
    {
        if (_states.Count == 0)
        {
            return;
        }

        uint now = NowMs;
        foreach (UnitSpellState state in _states.Values.ToArray())
        {
            if (!state.Unit.IsInWorld)
            {
                if (IsInTransit(state.Unit))
                {
                    continue;
                }

                Forget(state);
                continue;
            }

            if (IsQuestSettlementPending(state.Unit))
            {
                continue;
            }

            if (state.AutoRepeatCast is { } autoRepeat)
            {
                UpdateAutoRepeat(state, autoRepeat); // before the casts, as Unit::_UpdateSpells does (Unit.cpp:2673-2674)
            }

            if (state.CurrentCast is { } cast)
            {
                UpdateCast(cast, diffMs);
            }

            UpdateAuras(state, diffMs);
            UpdateAreaAuras(state);
            ExpireCooldowns(state, now);
            if (state.IsIdle)
            {
                _states.Remove(state.Unit.Guid);
            }
        }
    }

    /// <summary>
    /// A unit leaves the world (logout, despawn): its cast stops without packets and its state is
    /// dropped. A player's saveable auras and cooldowns are captured first by the world daemon
    /// (<see cref="CaptureState"/>, character_aura / character_spell_cooldown).
    /// </summary>
    public void RemoveUnit(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        RevokeAuraCaster(unit);
        if (_states.TryGetValue(unit.Guid, out UnitSpellState? state) && ReferenceEquals(state.Unit, unit))
        {
            Forget(state);
        }
    }

    // --- cast pipeline ------------------------------------------------------------------

    private SpellCastResult Prepare(Unit caster, SpellInfo spell, SpellCastTargets targets, bool triggered, bool autoRepeatShot = false)
    {
        if (IsQuestSettlementPending(caster))
        {
            SendCastResult(caster, spell, SpellCastResult.NotReady, triggered);
            return SpellCastResult.NotReady;
        }

        UnitSpellState state = GetOrCreateState(caster);
        if (!triggered && spell.IsNextMeleeSwing)
        {
            // Own slot: neither blocked by nor interrupting a generic cast or a channel.
            return QueueNextSwing(state, caster, spell, targets, ResolveUnitTarget(caster, targets));
        }

        if (!triggered && RangedSpellFacts.IsAutoRepeatRanged(spell))
        {
            // ranged (autorepeat lane): Auto Shot / wand Shoot toggle in their own slot (SpellSystem.AutoRepeat.cs).
            return PrepareAutoRepeat(state, caster, spell, targets);
        }

        if (!triggered && state.CurrentCast is { } current)
        {
            if (current.State == SpellCastState.Preparing)
            {
                SendCastResult(caster, spell, SpellCastResult.SpellInProgress, triggered);
                return SpellCastResult.SpellInProgress;
            }

            // A new cast ends a running channel (vmangos Unit::SetCurrentCastedSpell: generic
            // spells interrupt the channeled slot).
            Cancel(current);
        }

        Unit? unitTarget = ResolveUnitTarget(caster, targets);
        SpellCastResult result = CheckCast(state, spell, targets, unitTarget, triggered, strict: true);
        if (result != SpellCastResult.CastOk)
        {
            SendCastResult(caster, spell, result, triggered);
            return result;
        }

        int castTime = triggered ? 0 : CastTimeFor(caster, spell);
        var cast = new SpellCast(spell, caster, targets, triggered, castTime, PowerCostFor(caster, spell), DurationFor(caster, spell));
        cast.AutoRepeatShot = autoRepeatShot;
        if (!triggered)
        {
            OnGenericCastStarted(state, spell); // ranged (autorepeat lane): a wand is broken, Auto Shot gets a new wind-up (Unit::SetCurrentCastedSpell)
            state.CurrentCast = cast;
            SendToSet(caster, WorldOpcode.SmsgSpellStart, SpellPackets.BuildSpellStart(
                caster.Guid, caster.Guid, spell.Id, WithAmmoFlag(SpellCastFlags.Unknown2, spell), (uint)castTime, targets,
                RangedSpellFacts.IsRanged(spell) ? GetAmmoVisual(caster) : default), includeSelf: true); // ranged (hunter lane): ammo trailer
            AddGlobalCooldown(state, spell);
        }

        // vmangos Spell::prepare removes ACTION/LOOTING auras (and Stealth) before the cast bar runs (Spell.cpp:3443-3456).
        InterruptAtCastStart(cast);

        NotifyPrepared(cast);
        if (castTime == 0)
        {
            return Cast(cast);
        }

        return SpellCastResult.CastOk;
    }

    /// <summary>
    /// vmangos Spell::cast: re-check (with landing range leeway), start the cooldown, take the
    /// power, report success, send SMSG_SPELL_GO, start a channel, apply the effects, finish.
    /// </summary>
    private SpellCastResult Cast(SpellCast cast)
    {
        Unit caster = cast.Caster;
        SpellInfo spell = cast.Spell;
        UnitSpellState state = GetOrCreateState(caster);
        Unit? unitTarget = ResolveUnitTarget(caster, cast.Targets);
        SpellCastResult result = CheckCast(state, spell, cast.Targets, unitTarget, cast.IsTriggered, strict: false, skipCooldown: true);
        if (result != SpellCastResult.CastOk)
        {
            SendCastResult(caster, spell, result, cast.IsTriggered);
            if (!cast.IsTriggered)
            {
                SendInterrupted(cast);
            }

            Finish(cast);
            return result;
        }

        InterruptAtCastCompletion(cast); // rogue lane: ACTION_LATE / ATTACKING half (vmangos Spell.cpp:3697-3714), docs/integration/rogue-aura-interrupt.md
        AddCooldown(state, spell, cast.IsTriggered && !cast.AutoRepeatShot); // ranged (autorepeat lane): shots tell the client nothing
        TakePower(caster, spell, cast.PowerCost, cast.IsTriggered);
        TakeAmmo(caster, spell); // ranged (hunter lane): vmangos order TakePower, TakeReagents, TakeAmmo (Spell.cpp:3716-3718)
        SendCastResult(caster, spell, SpellCastResult.CastOk, cast.IsTriggered);
        cast.Completed = true;
        NotifyCast(cast);

        Dictionary<Unit, SpellTargetEntry> targetEffects = SelectTargets(cast, unitTarget);
        var hits = new List<ObjectGuid>();
        var misses = new List<(ObjectGuid Guid, SpellMissInfo Reason)>();
        foreach ((Unit target, SpellTargetEntry entry) in targetEffects)
        {
            // vmangos Spell::AddUnitTarget → Unit::SpellHitResult, once per target.
            entry.Miss = ReferenceEquals(target, caster) ? SpellMissInfo.None : CombatRules.RollHit(this, caster, target, spell);
            if (entry.Miss == SpellMissInfo.None)
            {
                hits.Add(target.Guid);
            }
            else
            {
                misses.Add((target.Guid, entry.Miss));
            }
        }

        SendToSet(caster, WorldOpcode.SmsgSpellGo, SpellPackets.BuildSpellGo(
            caster.Guid, caster.Guid, spell.Id, WithAmmoFlag(SpellCastFlags.Unknown9, spell), hits, misses, cast.Targets,
            RangedSpellFacts.IsRanged(spell) ? GetAmmoVisual(caster) : default), includeSelf: true); // ranged (hunter lane): ammo trailer

        int duration = cast.Duration;
        if (spell.IsChanneled && duration > 0 && !cast.IsTriggered)
        {
            cast.State = SpellCastState.Casting;
            cast.Timer = duration;
            cast.CastX = caster.X;
            cast.CastY = caster.Y;
            cast.CastZ = caster.Z;
            if (caster is Player player)
            {
                player.Session.Send(WorldOpcode.MsgChannelStart, SpellPackets.BuildChannelStart(spell.Id, (uint)duration));
            }

            caster.SetUInt64(UpdateFields.UnitFieldChannelObject, (unitTarget ?? caster).Guid.Value);
            caster.SetUInt32(UpdateFields.UnitChannelSpell, spell.Id);
        }

        foreach ((Unit target, SpellTargetEntry entry) in targetEffects)
        {
            if (entry.Miss != SpellMissInfo.None)
            {
                // vmangos SpellCaster::SendSpellMiss; a missed hostile spell still starts combat (zero damage).
                SendToSet(caster, WorldOpcode.SmsgSpelllogmiss, SpellPackets.BuildSpellLogMiss(spell.Id, caster.Guid, target.Guid, entry.Miss), includeSelf: true);
                InterruptTargetOfHostileSpell(cast, target, hit: false, dealsDamage: false); // rogue lane (vmangos Spell.cpp:1893-1897)
                if (!IsQuestSettlementPending(caster) && !IsQuestSettlementPending(target) && target.IsAlive && Relations.IsHostile(caster, target))
                {
                    Damage.DealSpellDamage(caster, target, spell, 0, periodic: false, startsCombat: StartsCombat(caster, target));
                }

                NotifyOutcome(cast, new SpellTargetOutcome(target, entry.Miss, 0, 0, false, entry.EffectMask));
                continue;
            }

            if (ApplyEffects(cast, target, entry.EffectMask, entry.Multipliers) is { } outcome)
            {
                NotifyOutcome(cast, outcome);
            }
        }

        // ranged (autorepeat lane): a non-triggered cast with the combat interrupt bit restarts the melee swing (vmangos Spell.cpp:3805-3810).
        if (!cast.IsTriggered && spell.InterruptFlags.HasFlag(SpellInterruptFlags.Combat) && !RangedSpellFacts.DoesNotResetCombatTimers(spell))
        {
            caster.Map?.FindUpdater<Combat.MapCombat>()?.ResetMeleeTimersAfterCast(caster);
        }

        if (cast.State != SpellCastState.Casting)
        {
            Finish(cast);
        }

        return SpellCastResult.CastOk;
    }

    private void UpdateCast(SpellCast cast, uint diffMs)
    {
        if (cast.Targets.Unit is { IsEmpty: false } targetGuid && Units.Find(cast.Caster, targetGuid) is null)
        {
            Cancel(cast);
            return;
        }

        if (cast.Caster is Player && cast.Timer != 0 && cast.HasMoved
            && (cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement)
                || cast.Spell.AuraInterruptFlags.HasFlag(SpellAuraInterruptFlags.Moving)
                || cast.Spell.ChannelInterruptFlags.HasFlag(SpellAuraInterruptFlags.Moving)))
        {
            // vmangos Spell::update: channels always cancel; casts only with the movement interrupt flag.
            if (cast.State == SpellCastState.Casting
                || (!cast.IsTriggered && cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement)))
            {
                Cancel(cast);
                return;
            }
        }

        // ranged (hunter lane): a player's cast bar does not run while feigning death (Spell.cpp:4082-4090).
        if (cast.State == SpellCastState.Preparing && cast.Timer != 0 && cast.Caster is Player && IsFeigningDeath(cast.Caster))
        {
            Cancel(cast);
            return;
        }

        cast.Timer = diffMs >= cast.Timer ? 0 : cast.Timer - (int)diffMs;
        if (cast.Timer > 0)
        {
            return;
        }

        if (cast.State == SpellCastState.Preparing)
        {
            Cast(cast);
        }
        else if (cast.State == SpellCastState.Casting)
        {
            EndChannel(cast, interrupted: false);
            Finish(cast);
        }
    }

    /// <summary>
    /// vmangos Spell::cancel: a preparing cast resets its global cooldown, tells the set it was
    /// interrupted (SMSG_SPELL_FAILED_OTHER, self included) and reports INTERRUPTED to the caster;
    /// a channel removes its auras, sends a zero channel update and the interrupt (no cast result).
    /// </summary>
    private void Cancel(SpellCast cast)
    {
        if (cast.State == SpellCastState.Finished)
        {
            return;
        }

        UnitSpellState state = GetOrCreateState(cast.Caster);
        if (cast.State == SpellCastState.Preparing)
        {
            state.GlobalCooldowns.Remove(cast.Spell.StartRecoveryCategory);
            SendInterrupted(cast);
            SendCastResult(cast.Caster, cast.Spell, SpellCastResult.Interrupted, cast.IsTriggered);
        }
        else
        {
            RemoveAurasByCaster(cast.Caster, cast.Spell.Id, cast.Caster.Guid);
            if (ResolveUnitTarget(cast.Caster, cast.Targets) is { } target && target != cast.Caster)
            {
                RemoveAurasByCaster(target, cast.Spell.Id, cast.Caster.Guid);
            }

            EndChannel(cast, interrupted: true);
            SendInterrupted(cast);
        }

        Finish(cast);
    }

    private static void EndChannel(SpellCast cast, bool interrupted)
    {
        _ = interrupted; // vmangos delays the field reset by 1 s on a normal end (ChannelResetEvent); reset at once here.
        Unit caster = cast.Caster;
        if (caster is Player player)
        {
            player.Session.Send(WorldOpcode.MsgChannelUpdate, SpellPackets.BuildChannelUpdate(0));
        }

        caster.SetUInt64(UpdateFields.UnitFieldChannelObject, 0);
        caster.SetUInt32(UpdateFields.UnitChannelSpell, 0);
    }

    private void Finish(SpellCast cast)
    {
        bool alreadyFinished = cast.State == SpellCastState.Finished;
        cast.State = SpellCastState.Finished;
        if (_states.TryGetValue(cast.Caster.Guid, out UnitSpellState? state))
        {
            if (ReferenceEquals(state.CurrentCast, cast))
            {
                state.CurrentCast = null;
            }

            if (ReferenceEquals(state.MeleeCast, cast))
            {
                state.MeleeCast = null;
            }

            if (ReferenceEquals(state.AutoRepeatCast, cast))
            {
                state.AutoRepeatCast = null;
            }
        }

        if (!alreadyFinished)
        {
            NotifyFinished(cast);
        }
    }

    private void Forget(UnitSpellState state)
    {
        RevokeAuraCaster(state.Unit);
        foreach (SpellCast? slot in new[] { state.CurrentCast, state.MeleeCast, state.AutoRepeatCast })
        {
            if (slot is { State: not SpellCastState.Finished } cast)
            {
                cast.State = SpellCastState.Finished;
                NotifyFinished(cast);
            }
        }

        state.CurrentCast = null;
        state.MeleeCast = null;
        state.AutoRepeatCast = null;

        foreach (SpellAuraHolder holder in state.Auras)
        {
            holder.IsRemoved = true;
        }

        state.Auras.Clear();
        _states.Remove(state.Unit.Guid);
    }

    // --- checks -------------------------------------------------------------------------

    /// <summary>
    /// The subset of vmangos Spell::CheckCast this area owns: caster alive, cooldowns, stun,
    /// movement, explicit target presence and liveness, range (CheckRange) and power (CheckPower).
    /// Line of sight is delegated to the vmap-los seam. Reagents, items, shapeshift, facing and area restrictions belong to other
    /// areas (docs/areas/spells.md).
    /// </summary>
    private SpellCastResult CheckCast(UnitSpellState state, SpellInfo spell, SpellCastTargets targets, Unit? unitTarget, bool triggered, bool strict, bool skipCooldown = false)
    {
        Unit caster = state.Unit;
        if (IsQuestSettlementPending(caster))
        {
            return SpellCastResult.NotReady;
        }

        // Registered first checks (stand state): vmangos Spell.cpp:5309.
        SpellCastResult start = RunCastChecks(SpellCheckPhase.Start, caster, spell, targets, unitTarget, triggered, strict);
        if (start != SpellCastResult.CastOk)
        {
            return start;
        }

        if (!caster.IsAlive && !spell.HasAttribute(SpellAttributes.AllowCastWhileDead) && _objectCastDepth == 0)
        {
            return SpellCastResult.CasterDead;
        }

        if (!triggered && !skipCooldown && !IsSpellReady(state, spell))
        {
            return SpellCastResult.NotReady;
        }

        // Registered caster-state checks (shapeshift, caster aura state): vmangos Spell.cpp:5349-5392.
        SpellCastResult casterState = RunCastChecks(SpellCheckPhase.Caster, caster, spell, targets, unitTarget, triggered, strict);
        if (casterState != SpellCastResult.CastOk)
        {
            return casterState;
        }

        if (!triggered)
        {
            // The caster's own state (stun, confuse, fear, silence, pacify) and the immunity-granting-spell bypass (vmangos Spell::CheckCasterAuras).
            SpellCastResult stateResult = Rules.Gating.CasterAuraGate.Check(this, caster, spell, CastTimeFor(caster, spell));
            if (stateResult != SpellCastResult.CastOk)
            {
                return stateResult;
            }
        }

        // ranged (autorepeat lane): an auto-repeat spell cast while moving is delayed, not refused (vmangos Spell.cpp:5395-5403).
        if (!triggered && caster is Player moving && RangedSpellFacts.IsAutoRepeatRanged(spell) && IsMoving(moving))
        {
            return SpellCastResult.Moving;
        }

        if (!triggered && strict && caster is Player mover && CastTimeFor(caster, spell) > 0
            && spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement) && IsMoving(mover))
        {
            return SpellCastResult.Moving;
        }

        // ranged (hunter lane): vmangos Spell::CheckItems (weapon and ammunition) runs before CheckRange (Spell.cpp:5694-5702).
        SpellCastResult rangedItems = CheckRangedItems(caster, spell);
        if (rangedItems != SpellCastResult.CastOk)
        {
            return rangedItems;
        }

        // ranged (hunter lane): Hunter's Mark needs an attackable unit (Spell.cpp:6436-6447).
        SpellCastResult stalked = CheckStalkedTarget(caster, spell, unitTarget);
        if (stalked != SpellCastResult.CastOk)
        {
            return stalked;
        }

        bool needsUnit = NeedsUnitTarget(spell);
        Unit? checkedTarget = null;
        if (needsUnit)
        {
            Unit? target = unitTarget ?? (targets.Mask == SpellCastTargetFlags.Self ? caster : null);
            if (target is null)
            {
                return SpellCastResult.BadTargets;
            }

            if (IsQuestSettlementPending(target))
            {
                return SpellCastResult.BadTargets;
            }

            // vmangos Spell::CheckCast (Spell.cpp:5572) asks SpellEntry::CanTargetAliveState (SpellEntry.h:944-950):
            // a dead explicit target is fine for a spell that can target the dead.
            if (!target.IsAlive && !spell.CanTargetDead)
            {
                return SpellCastResult.TargetsDead;
            }

            checkedTarget = target;

            // Registered target-state checks (target aura state): vmangos Spell.cpp:5636.
            SpellCastResult targetState = RunCastChecks(SpellCheckPhase.Target, caster, spell, targets, target, triggered, strict);
            if (targetState != SpellCastResult.CastOk)
            {
                return targetState;
            }
        }

        // Registered equipment checks: vmangos CheckItems (Spell.cpp:5698), before CheckRange (:5707) and CheckPower (:5721).
        SpellCastResult items = RunCastChecks(SpellCheckPhase.Items, caster, spell, targets, checkedTarget, triggered, strict);
        if (items != SpellCastResult.CastOk)
        {
            return items;
        }

        if (needsUnit)
        {
            Unit target = checkedTarget!;
            // ranged (hunter lane): a cast from a game object (trap) ignores range and the owner being far or dead (vmangos triggered casts).
            SpellCastResult range = _objectCastDepth > 0 ? SpellCastResult.CastOk : CheckRange(caster, spell, target, strict, RangedOptions.Range.Leeway == RangeLeewayMode.Retail);
            if (range != SpellCastResult.CastOk)
            {
                return range;
            }

            // Line of sight (vmap-los seam, docs/integration/vmap-los.md).
            SpellCastResult sight = Maps.Collision.SpellLineOfSight.Check(caster, spell, target, triggered);
            if (sight != SpellCastResult.CastOk)
            {
                return sight;
            }
        }
        else if (targets.HasDest)
        {
            SpellCastResult range = CheckDestRange(caster, spell, targets, strict);
            if (range != SpellCastResult.CastOk)
            {
                return range;
            }

            SpellCastResult sight = Maps.Collision.SpellLineOfSight.CheckDest(caster, spell, targets.Dest.X, targets.Dest.Y, targets.Dest.Z, triggered);
            if (sight != SpellCastResult.CastOk)
            {
                return sight;
            }
        }

        SpellCastResult targetRules = CheckTargetRules(caster, spell, targets, unitTarget, strict);
        if (targetRules != SpellCastResult.CastOk)
        {
            return targetRules;
        }

        SpellCastResult effectChecks = CheckEffects(caster, spell, targets, unitTarget, triggered, strict);
        if (effectChecks != SpellCastResult.CastOk)
        {
            return effectChecks;
        }

        // Registered checks inside vmangos CheckPower, before the amounts (combo points, Spell.cpp:7035-7038).
        SpellCastResult beforePower = RunCastChecks(SpellCheckPhase.Power, caster, spell, targets, unitTarget, triggered, strict);
        if (beforePower != SpellCastResult.CastOk)
        {
            return beforePower;
        }

        SpellCastResult power = CheckPower(caster, spell);
        if (power != SpellCastResult.CastOk)
        {
            return power;
        }

        // Registered checks after power and caster auras: the target aura state (Spell.cpp:5733-5742).
        return RunCastChecks(SpellCheckPhase.Final, caster, spell, targets, unitTarget, triggered, strict);
    }

    /// <summary>
    /// vmangos Spell::CheckRange: self-only range always passes; combat-range spells use melee
    /// reach (5 yd + both combat reaches); otherwise the SpellRange maximum plus the player leeway
    /// (1.25 yd at cast start, 6.25 yd on landing) against the combat distance (3D distance minus
    /// both combat reaches), with the minimum range giving TOO_CLOSE.
    /// </summary>
    internal static SpellCastResult CheckRange(Unit caster, SpellInfo spell, Unit target, bool strict, bool movementLeeway = true)
    {
        if (spell.RangeIndex == SpellConstants.RangeIndexSelfOnly || ReferenceEquals(caster, target))
        {
            return SpellCastResult.CastOk;
        }

        float distance = Distance3D(caster, target.X, target.Y, target.Z);
        float reach = CombatReach(caster) + CombatReach(target);
        if (spell.RangeIndex == SpellConstants.RangeIndexCombat)
        {
            // vmangos Spell::CheckRange (Spell.cpp:6882): a next-melee-swing spell passes; the swing itself checks reach.
            if (spell.IsNextMeleeSwing)
            {
                return SpellCastResult.CastOk;
            }

            // vmangos WorldObject::CanReachWithMeleeSpellAttack with Spell::CheckRange's range_mod
            // 1.0: reach = both combat reaches + 1.0 + BASE_MELEERANGE_OFFSET (4/3), at least
            // ATTACK_DISTANCE, compared in 2D ("melee spells ignore Z-axis checks").
            float meleeRange = Math.Max(SpellConstants.AttackDistance, reach + 1.0f + (4.0f / 3.0f));
            float dx = caster.X - target.X;
            float dy = caster.Y - target.Y;
            return (dx * dx) + (dy * dy) < meleeRange * meleeRange ? SpellCastResult.CastOk : SpellCastResult.OutOfRange;
        }

        float leeway = caster is Player
            ? (strict ? SpellConstants.PlayerStrictRangeLeeway : SpellConstants.PlayerLandingRangeLeeway)
            : (strict ? 0.0f : 2.25f);

        // ranged (hunter lane): + 2.66 yd when a player is involved and both run (Spell.cpp:6911, Object.cpp:1890-1912).
        if (movementLeeway)
        {
            leeway += RangeLeeway.Bonus(caster, target);
        }

        float combatDistance = Math.Max(0.0f, distance - reach);
        if (combatDistance > spell.Range.Max + leeway)
        {
            return SpellCastResult.OutOfRange;
        }

        return spell.Range.Min > 0 && combatDistance < spell.Range.Min ? SpellCastResult.TooClose : SpellCastResult.CastOk;
    }

    private static SpellCastResult CheckDestRange(Unit caster, SpellInfo spell, SpellCastTargets targets, bool strict)
    {
        if (spell.RangeIndex == SpellConstants.RangeIndexSelfOnly || spell.Range.Max <= 0)
        {
            return SpellCastResult.CastOk;
        }

        float leeway = caster is Player ? (strict ? SpellConstants.PlayerStrictRangeLeeway : SpellConstants.PlayerLandingRangeLeeway) : 0.0f;
        float distance = Distance3D(caster, targets.Dest.X, targets.Dest.Y, targets.Dest.Z);
        if (distance > spell.Range.Max + leeway)
        {
            return SpellCastResult.OutOfRange;
        }

        return spell.Range.Min > 0 && distance < spell.Range.Min ? SpellCastResult.TooClose : SpellCastResult.CastOk;
    }

    /// <summary>vmangos Spell::CheckPower: enough of the spell's power type (health costs must leave the caster alive).</summary>
    private SpellCastResult CheckPower(Unit caster, SpellInfo spell)
    {
        uint cost = PowerCostFor(caster, spell);
        if (cost == 0)
        {
            return SpellCastResult.CastOk;
        }

        if (spell.PowerType == SpellMath.PowerHealth)
        {
            return caster.Health <= cost ? SpellCastResult.CasterAurastate : SpellCastResult.CastOk;
        }

        if (spell.PowerType is < 0 or > (int)PowerType.Happiness)
        {
            return SpellCastResult.Unknown;
        }

        return GetPower(caster, (PowerType)spell.PowerType) < cost ? SpellCastResult.NoPower : SpellCastResult.CastOk;
    }

    /// <summary>
    /// vmangos SpellCaster::IsSpellReady + HasGCD: no running spell cooldown, category cooldown or
    /// global cooldown of the spell's StartRecoveryCategory.
    /// </summary>
    public bool IsSpellReady(Unit unit, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(spell);
        return GetState(unit.Guid) is not { } state || IsSpellReady(state, spell);
    }

    private bool IsSpellReady(UnitSpellState state, SpellInfo spell)
    {
        // ranged (hunter lane): a COOLDOWN_ON_EVENT spell waits while the object it created lives (Unit::AddGameObject).
        if (spell.HasAttribute(SpellAttributes.CooldownOnEvent) && SpellObjects.IsCreatedBySpell(state.Unit, spell.Id))
        {
            return false;
        }

        uint now = NowMs;
        if (state.SpellCooldowns.TryGetValue(spell.Id, out uint until) && until > now)
        {
            return false;
        }

        if (spell.Category != 0 && state.CategoryCooldowns.TryGetValue(spell.Category, out until) && until > now)
        {
            return false;
        }

        if (spell.PreventionType == SpellConstants.PreventionTypeSilence && state.SchoolLockouts.TryGetValue(spell.School, out until) && until > now)
        {
            return false;
        }

        return !(state.GlobalCooldowns.TryGetValue(spell.StartRecoveryCategory, out until) && until > now);
    }

    /// <summary>
    /// The explicit-target rules this round adds after range and line of sight (vmangos
    /// Spell::CheckCast / CheckTarget): party- and raid-only targets must be in the caster's
    /// group, and a client-requested dispel-only spell needs something to dispel.
    /// </summary>
    private SpellCastResult CheckTargetRules(Unit caster, SpellInfo spell, SpellCastTargets targets, Unit? unitTarget, bool strict)
    {
        if (!NeedsUnitTarget(spell) || (unitTarget ?? (targets.Mask == SpellCastTargetFlags.Self ? caster : null)) is not { } target)
        {
            return SpellCastResult.CastOk;
        }

        SpellCastResult group = CheckGroupTarget(caster, spell, target);
        if (group != SpellCastResult.CastOk)
        {
            return group;
        }

        if (strict && IsDispelOnly(spell)
            && !spell.Effects.Any(e => e.Effect == SpellEffectName.Dispel && DispellableAuras(caster, target, (uint)e.MiscValue).Count > 0))
        {
            return SpellCastResult.NothingToDispel;
        }

        return SpellCastResult.CastOk;
    }

    /// <summary>Party/raid-only explicit targets (vmangos Spell::CheckTarget for TARGET_SINGLE_PARTY / TARGET_SINGLE_FRIEND_2).</summary>
    private SpellCastResult CheckGroupTarget(Unit caster, SpellInfo spell, Unit target)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty)
            {
                continue;
            }

            if ((effect.TargetA == SpellImplicitTarget.UnitParty && !IsGroupMember(caster, target, raid: false))
                || (effect.TargetA == SpellImplicitTarget.UnitRaid && !IsGroupMember(caster, target, raid: true)))
            {
                return SpellCastResult.BadTargets;
            }
        }

        return SpellCastResult.CastOk;
    }

    private static bool IsDispelOnly(SpellInfo spell)
        => spell.Effects.Any(e => e.Effect == SpellEffectName.Dispel) && spell.Effects.All(e => e.IsEmpty || e.Effect == SpellEffectName.Dispel);

    // --- costs and cooldowns ------------------------------------------------------------

    /// <summary>Power cost including UNIT_FIELD_POWER_COST_MODIFIER for the spell's school (vmangos Spell::CalculatePowerCost).</summary>
    public static uint CalculatePowerCost(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        uint current = 0;
        uint max = 0;
        if (spell.PowerType is >= 0 and <= (int)PowerType.Happiness)
        {
            current = GetPower(caster, (PowerType)spell.PowerType);
            max = caster.GetUInt32(UpdateFields.UnitFieldMaxpower1 + spell.PowerType);
        }

        int cost = spell.CalculatePowerCost(
            caster.Level, current, max,
            caster.GetUInt32(UpdateFields.UnitFieldBaseHealth), caster.GetUInt32(UpdateFields.UnitFieldBaseMana), caster.Health);
        if (!spell.HasAttribute(SpellAttributesEx.UseAllMana))
        {
            cost += caster.GetInt32(UpdateFields.UnitFieldPowerCostModifier + (int)spell.School);

            // vmangos Spell::CalculatePowerCost (Spell.cpp:7026-7036): creature-level scaling, then the school
            // percent multiplier (aura 72: Clearcasting, cost talents). Casters lane, mana-spend-rule.
            if (((uint)spell.Attributes & Casters.CasterAttributes.ScalesWithCreatureLevel) != 0 && caster.Level > 0)
            {
                cost = (int)(cost / ((1.117f * spell.SpellLevel / caster.Level) - 0.1327f));
            }

            cost = (int)(cost * (1.0f + caster.GetFloat(UpdateFields.UnitFieldPowerCostMultiplier + (int)spell.School)));
        }

        return (uint)Math.Max(cost, 0);
    }

    private static void TakePower(Unit caster, SpellInfo spell, uint cost, bool triggered)
    {
        if (cost == 0)
        {
            return;
        }

        if (spell.PowerType == SpellMath.PowerHealth)
        {
            caster.Health -= Math.Min(cost, caster.Health);
            return;
        }

        if (spell.PowerType is >= 0 and <= (int)PowerType.Happiness)
        {
            var power = (PowerType)spell.PowerType;
            SetPower(caster, power, GetPower(caster, power) - Math.Min(cost, GetPower(caster, power)));

            // vmangos Spell::TakePower (Spell.cpp:5077-5079): paying mana starts the five second timer unless the
            // spell has SPELL_ATTR_EX2_DONT_BLOCK_MANA_REGEN. A triggered cast (trigger-spell auras, scripts) does
            // not start it: vmangos skips TakePower entirely for casts triggered by an aura. Casters lane, mana-spend-rule.
            if (power == PowerType.Mana && !triggered && ((uint)spell.AttributesEx2 & Casters.CasterAttributes.Ex2DontBlockManaRegen) == 0)
            {
                caster.Combat.NoteManaUsed(spell.Id);
            }
        }
    }

    /// <summary>vmangos Player::AddGCD (see <see cref="SpellConstants.GlobalCooldownCategory"/>).</summary>
    private void AddGlobalCooldown(UnitSpellState state, SpellInfo spell)
    {
        int duration = (int)spell.StartRecoveryTime;
        if (spell.StartRecoveryCategory == 0 && duration == 0)
        {
            return;
        }

        if (spell.StartRecoveryCategory == SpellConstants.GlobalCooldownCategory && duration == 1500
            && spell.DamageClass is not (SpellDamageClass.Melee or SpellDamageClass.Ranged)
            && !spell.HasAttribute(SpellAttributes.UsesRangedSlot) && !spell.HasAttribute(SpellAttributes.IsAbility))
        {
            duration = Math.Clamp((int)(duration * CastSpeed(state.Unit)), 1000, 1500);
        }

        if (duration < 1)
        {
            return;
        }

        duration = Math.Max(1, duration - (int)MapUpdateIntervalMs);
        state.GlobalCooldowns[spell.StartRecoveryCategory] = NowMs + (uint)duration;
    }

    /// <summary>
    /// vmangos Spell::SendSpellCooldown → SpellCaster::AddCooldown: start the spell and category
    /// cooldowns unless the spell is passive. The client starts its own timers from SMSG_SPELL_GO
    /// for casts it requested (vmangos sends nothing then); for server-initiated (triggered) casts
    /// SMSG_SPELL_COOLDOWN tells the client — decision recorded in docs/areas/spells.md.
    /// COOLDOWN_ON_EVENT spells are not started here (the event that starts them is not modelled yet).
    /// </summary>
    private void AddCooldown(UnitSpellState state, SpellInfo spell, bool triggered, bool onEvent = false)
    {
        if (spell.IsPassive || (spell.HasAttribute(SpellAttributes.CooldownOnEvent) && !onEvent))
        {
            return;
        }

        // ranged (hunter lane): a ranged-slot spell also waits out the weapon speed (Player.cpp:22193-22197).
        uint recovery = spell.RecoveryTime + RangedRecoveryMs(state.Unit, spell);
        if (recovery == 0 && spell.CategoryRecoveryTime == 0)
        {
            return;
        }

        uint now = NowMs;
        if (recovery > 0)
        {
            state.SpellCooldowns[spell.Id] = now + recovery;
        }

        if (spell.Category != 0 && spell.CategoryRecoveryTime > 0)
        {
            state.CategoryCooldowns[spell.Category] = now + spell.CategoryRecoveryTime;
        }

        if (triggered && state.Unit is Player player)
        {
            uint ms = Math.Max(recovery, spell.CategoryRecoveryTime);
            player.Session.Send(WorldOpcode.SmsgSpellCooldown, SpellPackets.BuildSpellCooldown(player.Guid, [(spell.Id, ms)]));
        }
    }

    /// <summary>Clear a spell's cooldown and its category cooldown, and tell the client (vmangos Player::RemoveSpellCooldown → SMSG_CLEAR_COOLDOWN).</summary>
    public void ClearCooldown(Unit unit, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return;
        }

        bool removed = state.SpellCooldowns.Remove(spellId);
        if (Store.Get(spellId) is { Category: not 0 } spell)
        {
            removed |= state.CategoryCooldowns.Remove(spell.Category);
        }

        if (removed && unit is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgClearCooldown, SpellPackets.BuildClearCooldown(spellId, player.Guid));
        }
    }

    /// <summary>Running cooldowns for SMSG_INITIAL_SPELLS (vmangos Player::SendInitialSpells cooldown block).</summary>
    public IReadOnlyList<InitialSpellCooldown> GetActiveCooldowns(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return [];
        }

        uint now = NowMs;
        var result = new List<InitialSpellCooldown>();
        foreach ((uint spellId, uint until) in state.SpellCooldowns)
        {
            if (until > now && Store.Get(spellId) is { } spell)
            {
                uint category = spell.Category != 0 && state.CategoryCooldowns.TryGetValue(spell.Category, out uint catUntil) && catUntil > now
                    ? catUntil - now : 0;
                result.Add(new InitialSpellCooldown(spellId, 0, spell.Category, until - now, category));
            }
        }

        return result;
    }

    private static void ExpireCooldowns(UnitSpellState state, uint now)
    {
        Expire(state.SpellCooldowns, now);
        Expire(state.CategoryCooldowns, now);
        Expire(state.GlobalCooldowns, now);
        foreach ((SpellSchool school, uint until) in state.SchoolLockouts.ToArray())
        {
            if (until <= now)
            {
                state.SchoolLockouts.Remove(school);
            }
        }

        static void Expire(Dictionary<uint, uint> cooldowns, uint now)
        {
            foreach ((uint key, uint until) in cooldowns.ToArray())
            {
                if (until <= now)
                {
                    cooldowns.Remove(key);
                }
            }
        }
    }

    // --- packets ------------------------------------------------------------------------

    /// <summary>
    /// vmangos Spell::SendCastResult: players only; nothing for triggered casts or spells with
    /// DO_NOT_REPORT_SPELL_FAILURE; passive spells fail with DONT_REPORT.
    /// </summary>
    private static void SendCastResult(Unit caster, SpellInfo spell, SpellCastResult result, bool triggered)
    {
        if (caster is not Player player || triggered || spell.HasAttribute(SpellAttributesEx2.DoNotReportSpellFailure))
        {
            return;
        }

        if (result != SpellCastResult.CastOk && spell.IsPassive)
        {
            result = SpellCastResult.DontReport;
        }

        player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(spell.Id, result));
    }

    /// <summary>
    /// vmangos Spell::SendInterrupted: SMSG_SPELL_FAILED_OTHER to the caster's set, self included.
    /// cmangos-classic also sends SMSG_SPELL_FAILURE first; vmangos dropped it as useless
    /// ("This first packet is apparently useless") — vmangos followed, recorded.
    /// </summary>
    private static void SendInterrupted(SpellCast cast)
        => SendToSet(cast.Caster, WorldOpcode.SmsgSpellFailedOther, SpellPackets.BuildSpellFailedOther(cast.Caster.Guid, cast.Spell.Id), includeSelf: true);

    /// <summary>vmangos WorldObject::SendMessageToSet: the unit's own client (when asked) and every player that sees it.</summary>
    internal static void SendToSet(Unit unit, WorldOpcode opcode, byte[] payload, bool includeSelf)
    {
        if (includeSelf && unit is Player self)
        {
            self.Session.Send(opcode, payload);
        }

        unit.Map?.BroadcastToObservers(unit, opcode, payload);
    }

    // --- helpers ------------------------------------------------------------------------

    private static bool IsQuestSettlementPending(Unit? unit)
        => unit is Player { IsQuestSettlementPending: true };

    private UnitSpellState GetOrCreateState(Unit unit)
    {
        if (!_states.TryGetValue(unit.Guid, out UnitSpellState? state) || !ReferenceEquals(state.Unit, unit))
        {
            state = new UnitSpellState(unit);
            _states[unit.Guid] = state;
        }

        return state;
    }

    private Unit? ResolveUnitTarget(Unit caster, SpellCastTargets targets)
        => (targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy)) != 0 ? Units.Find(caster, targets.Unit) : null;

    private static bool NeedsUnitTarget(SpellInfo spell)
        => spell.Effects.Any(e => !e.IsEmpty && IsExplicitUnitTarget(e.TargetA));

    internal static bool IsExplicitUnitTarget(SpellImplicitTarget target)
        => target is SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.UnitFriend or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitParty
            or SpellImplicitTarget.UnitRaid or SpellImplicitTarget.UnitFriendChainHeal;

    private static bool IsMoving(Player player)
        => (player.Movement.Flags & (MovementFlags.Forward | MovementFlags.Backward | MovementFlags.StrafeLeft
            | MovementFlags.StrafeRight | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0;

    internal static float CastSpeed(Unit unit)
    {
        float speed = unit.GetFloat(UpdateFields.UnitModCastSpeed);
        return speed > 0 ? speed : 1.0f;
    }

    private static float CombatReach(Unit unit)
    {
        float reach = unit.GetFloat(UpdateFields.UnitFieldCombatreach);
        return reach > 0 ? reach : SpellConstants.DefaultCombatReach;
    }

    private static float Distance3D(Unit unit, float x, float y, float z)
    {
        float dx = unit.X - x;
        float dy = unit.Y - y;
        float dz = unit.Z - z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>UNIT_FIELD_POWER1 + power type (vmangos Unit::GetPower).</summary>
    public static uint GetPower(Unit unit, PowerType power)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.GetUInt32(UpdateFields.UnitFieldPower1 + (int)power);
    }

    /// <summary>vmangos Unit::SetPower: clamped to UNIT_FIELD_MAXPOWER1 + power type.</summary>
    public static void SetPower(Unit unit, PowerType power, uint value)
    {
        ArgumentNullException.ThrowIfNull(unit);
        uint max = unit.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power);
        unit.SetUInt32(UpdateFields.UnitFieldPower1 + (int)power, Math.Min(value, max));
    }

    private void ReportUnsupported(string kind, uint value, uint spellId)
    {
        if (_reportedUnsupported.Add((kind, value)))
        {
            _logger.LogInformation("Spell {Spell}: {Kind} {Value} is not implemented yet", spellId, kind, value);
        }
    }
}
