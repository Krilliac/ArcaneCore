using ArcaneCore.Game.Entities;
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
    private readonly Dictionary<ObjectGuid, UnitSpellState> _states = [];
    private readonly Func<uint> _clock;
    private readonly ILogger _logger;
    private readonly HashSet<(string Kind, uint Value)> _reportedUnsupported = [];

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
        Store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Units = units ?? new MapPlayerResolver();
        Damage = damage ?? new HealthOnlyDamageSink();
        Teleports = teleport ?? new NearTeleportSink();
        Spellbook = spellbook;
        Random = random ?? Random.Shared;
        _logger = logger ?? NullLogger.Instance;
        EffectHandlers = CreateEffectHandlers();
        AuraHandlers = CreateAuraHandlers();
    }

    /// <summary>The spell table (replaceable, e.g. after a reload).</summary>
    public SpellStore Store { get; set; }

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

    /// <summary>Units with live spell state.</summary>
    public int TrackedUnitCount => _states.Count;

    public uint NowMs => _clock();

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
    /// not interrupted by this packet (withDelayed = false keeps vmangos' channel slot).
    /// </summary>
    public void CancelCast(Unit caster, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Preparing } cast
            && (spellId == 0 || cast.Spell.Id == spellId))
        {
            Cancel(cast);
        }
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

    private SpellCastResult Prepare(Unit caster, SpellInfo spell, SpellCastTargets targets, bool triggered)
    {
        if (IsQuestSettlementPending(caster))
        {
            SendCastResult(caster, spell, SpellCastResult.NotReady, triggered);
            return SpellCastResult.NotReady;
        }

        UnitSpellState state = GetOrCreateState(caster);
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

        int castTime = triggered ? 0 : spell.GetCastTime(caster.Level, CastSpeed(caster));
        var cast = new SpellCast(spell, caster, targets, triggered, castTime, CalculatePowerCost(caster, spell));
        if (!triggered)
        {
            state.CurrentCast = cast;
            SendToSet(caster, WorldOpcode.SmsgSpellStart, SpellPackets.BuildSpellStart(
                caster.Guid, caster.Guid, spell.Id, SpellCastFlags.Unknown2, (uint)castTime, targets), includeSelf: true);
            AddGlobalCooldown(state, spell);
        }

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

        AddCooldown(state, spell, cast.IsTriggered);
        TakePower(caster, spell, cast.PowerCost);
        SendCastResult(caster, spell, SpellCastResult.CastOk, cast.IsTriggered);

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
            caster.Guid, caster.Guid, spell.Id, SpellCastFlags.Unknown9, hits, misses, cast.Targets), includeSelf: true);

        int duration = spell.GetDuration();
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
                if (!IsQuestSettlementPending(caster) && !IsQuestSettlementPending(target) && target.IsAlive && Relations.IsHostile(caster, target))
                {
                    Damage.DealSpellDamage(caster, target, spell, 0, periodic: false);
                }

                continue;
            }

            ApplyEffects(cast, target, entry.EffectMask, entry.Multipliers);
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
        cast.State = SpellCastState.Finished;
        if (_states.TryGetValue(cast.Caster.Guid, out UnitSpellState? state) && ReferenceEquals(state.CurrentCast, cast))
        {
            state.CurrentCast = null;
        }
    }

    private void Forget(UnitSpellState state)
    {
        RevokeAuraCaster(state.Unit);
        if (state.CurrentCast is { } cast)
        {
            cast.State = SpellCastState.Finished;
            state.CurrentCast = null;
        }

        foreach (SpellAuraHolder holder in state.Auras)
        {
            holder.IsRemoved = true;
        }

        state.Auras.Clear();
        _states.Remove(state.Unit.Guid);
    }

    // --- checks -------------------------------------------------------------------------

    /// <summary>
    /// The subset of vmangos Spell::CheckCast this area owns: caster alive, cooldowns and school
    /// lockouts, stun, movement, explicit target presence and liveness, range (CheckRange), line of
    /// sight through <see cref="LineOfSight"/>, party/raid-only targets, nothing to dispel, and
    /// power (CheckPower). Reagents, items, shapeshift, facing and area restrictions belong to
    /// other areas (docs/areas/spells.md).
    /// </summary>
    private SpellCastResult CheckCast(UnitSpellState state, SpellInfo spell, SpellCastTargets targets, Unit? unitTarget, bool triggered, bool strict, bool skipCooldown = false)
    {
        Unit caster = state.Unit;
        if (IsQuestSettlementPending(caster))
        {
            return SpellCastResult.NotReady;
        }

        if (!caster.IsAlive && !spell.HasAttribute(SpellAttributes.AllowCastWhileDead))
        {
            return SpellCastResult.CasterDead;
        }

        if (!triggered && !skipCooldown && !IsSpellReady(state, spell))
        {
            return SpellCastResult.NotReady;
        }

        if (!triggered && (caster.UnitFlags & UnitFlags.Stunned) != 0 && spell.InterruptFlags.HasFlag(SpellInterruptFlags.Stun))
        {
            return SpellCastResult.Stunned;
        }

        if (!triggered && strict && caster is Player mover && spell.GetCastTime(caster.Level, CastSpeed(caster)) > 0
            && spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement) && IsMoving(mover))
        {
            return SpellCastResult.Moving;
        }

        if (NeedsUnitTarget(spell))
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

            if (!target.IsAlive)
            {
                return SpellCastResult.TargetsDead;
            }

            SpellCastResult range = CheckRange(caster, spell, target, strict);
            if (range != SpellCastResult.CastOk)
            {
                return range;
            }

            if (!IsInLineOfSight(spell, caster, target))
            {
                return SpellCastResult.LineOfSight;
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
        }
        else if (targets.HasDest)
        {
            SpellCastResult range = CheckDestRange(caster, spell, targets, strict);
            if (range != SpellCastResult.CastOk)
            {
                return range;
            }

            if (!IsInLineOfSight(spell, caster, targets.Dest.X, targets.Dest.Y, targets.Dest.Z))
            {
                return SpellCastResult.LineOfSight;
            }
        }

        return CheckPower(caster, spell);
    }

    /// <summary>
    /// vmangos Spell::CheckRange: self-only range always passes; combat-range spells use melee
    /// reach (5 yd + both combat reaches); otherwise the SpellRange maximum plus the player leeway
    /// (1.25 yd at cast start, 6.25 yd on landing) against the combat distance (3D distance minus
    /// both combat reaches), with the minimum range giving TOO_CLOSE.
    /// </summary>
    internal static SpellCastResult CheckRange(Unit caster, SpellInfo spell, Unit target, bool strict)
    {
        if (spell.RangeIndex == SpellConstants.RangeIndexSelfOnly || ReferenceEquals(caster, target))
        {
            return SpellCastResult.CastOk;
        }

        float distance = Distance3D(caster, target.X, target.Y, target.Z);
        float reach = CombatReach(caster) + CombatReach(target);
        if (spell.RangeIndex == SpellConstants.RangeIndexCombat)
        {
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
        uint cost = CalculatePowerCost(caster, spell);
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
        uint now = NowMs;
        if (state.SpellCooldowns.TryGetValue(spell.Id, out uint until) && until > now)
        {
            return false;
        }

        if (spell.Category != 0 && state.CategoryCooldowns.TryGetValue(spell.Category, out until) && until > now)
        {
            return false;
        }

        if (state.SchoolLockouts.TryGetValue(spell.School, out until) && until > now)
        {
            return false;
        }

        return !(state.GlobalCooldowns.TryGetValue(spell.StartRecoveryCategory, out until) && until > now);
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
        }

        return (uint)Math.Max(cost, 0);
    }

    private static void TakePower(Unit caster, SpellInfo spell, uint cost)
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
    private void AddCooldown(UnitSpellState state, SpellInfo spell, bool triggered)
    {
        if (spell.IsPassive || spell.HasAttribute(SpellAttributes.CooldownOnEvent))
        {
            return;
        }

        if (spell.RecoveryTime == 0 && spell.CategoryRecoveryTime == 0)
        {
            return;
        }

        uint now = NowMs;
        if (spell.RecoveryTime > 0)
        {
            state.SpellCooldowns[spell.Id] = now + spell.RecoveryTime;
        }

        if (spell.Category != 0 && spell.CategoryRecoveryTime > 0)
        {
            state.CategoryCooldowns[spell.Category] = now + spell.CategoryRecoveryTime;
        }

        if (triggered && state.Unit is Player player)
        {
            uint ms = Math.Max(spell.RecoveryTime, spell.CategoryRecoveryTime);
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

    private static bool IsExplicitUnitTarget(SpellImplicitTarget target)
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
