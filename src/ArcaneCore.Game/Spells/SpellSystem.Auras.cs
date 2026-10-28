using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>An aura type implementation: apply/remove and, for periodic auras, the tick.</summary>
/// <param name="Apply">Called with true when the aura is applied and false when it is removed.</param>
/// <param name="Tick">Called on each periodic tick.</param>
public sealed record AuraHandler(Action<SpellSystem, SpellAuraHolder, SpellAura, bool>? Apply, Action<SpellSystem, SpellAuraHolder, SpellAura>? Tick);

public sealed partial class SpellSystem
{
    private readonly ConditionalWeakTable<Unit, AuraCasterOwner> _auraCasterOwners = new();

    /// <summary>Visible aura slots (vmangos MAX_AURAS); 0-31 positive, 32-47 negative (MAX_POSITIVE_AURAS).</summary>
    public const int MaxAuras = 48;

    public const int MaxPositiveAuras = 32;

    private Dictionary<AuraType, AuraHandler> AuraHandlers { get; }

    /// <summary>Install or replace an aura type's handler (seam for stat/combat/movement areas).</summary>
    public void RegisterAura(AuraType type, AuraHandler handler)
        => AuraHandlers[type] = handler ?? throw new ArgumentNullException(nameof(handler));

    public bool HasAuraHandler(AuraType type) => AuraHandlers.ContainsKey(type);

    /// <summary>
    /// The first aura set (vmangos SpellAuras.cpp AuraHandler table): periodic damage/heal/energize,
    /// OBS_MOD_HEALTH/MANA, PERIODIC_TRIGGER_SPELL and DUMMY, plus the crowd-control handlers installed by
    /// <see cref="CcAuraHandlers"/> (root, stun, silence, pacify, disarm, fear, confuse).
    /// </summary>
    private static Dictionary<AuraType, AuraHandler> CreateAuraHandlers() => ImmunityAuraHandlers.Install(CcAuraHandlers.Install(new()
    {
        // vmangos Aura::HandleAuraDummy (SpellAuras.cpp:1700-2215) is a switch on the spell id: RegisterDummyAuraHandler adds a case.
        [AuraType.Dummy] = new AuraHandler(static (s, h, a, apply) => s.ApplyDummyAura(h, a, apply), static (s, h, a) => s.TickDummyAura(h, a)),
        // vmangos Aura::HandleAuraEmpathy (SpellAuras.cpp:5599-5615): Beast Lore marks the target so the client shows its beast info.
        [AuraType.Empathy] = new AuraHandler(static (_, h, _, apply) =>
        {
            if (apply)
            {
                h.Target.SetFlag(UpdateFields.UnitDynamicFlags, Ranged.UnitDynFlags.SpecialInfo);
            }
            else
            {
                h.Target.RemoveFlag(UpdateFields.UnitDynamicFlags, Ranged.UnitDynFlags.SpecialInfo);
            }
        }, null),
        // Threat reads installed modifiers by school when damage/healing is resolved.
        [AuraType.ModThreat] = new AuraHandler(null, null),
        [AuraType.PeriodicDamage] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicDamage(h, a)),
        [AuraType.PeriodicDamagePercent] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicDamage(h, a)),
        [AuraType.PeriodicHeal] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicHeal(h, a)),
        [AuraType.ObsModHealth] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicHeal(h, a)),
        [AuraType.PeriodicEnergize] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicEnergize(h, a)),
        [AuraType.ObsModMana] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicEnergize(h, a)),
        [AuraType.PeriodicTriggerSpell] = new AuraHandler(null, static (s, h, a) => s.TickTriggerSpell(h, a)),

        // The proc engine (Spells/Procs, docs/areas/procs.md) reads these when an event happens; applying them changes nothing
        // (vmangos HandleNoImmediateEffect / HandleAuraProcTriggerSpell, SpellAuras.cpp:80, 93, 107-108, 174).
        [AuraType.DamageShield] = new AuraHandler(null, null),
        [AuraType.ReflectSpells] = new AuraHandler(null, null),
        [AuraType.ProcTriggerSpell] = new AuraHandler(null, null),
        [AuraType.ProcTriggerDamage] = new AuraHandler(null, null),
        [AuraType.AddTargetTrigger] = new AuraHandler(null, null),

        // vmangos Aura::HandleReflectSpellsSchool (SpellAuras.cpp:5422-5431): the caster's RESIST_MISS_CHANCE spell mods raise the chance.
        [AuraType.ReflectSpellsSchool] = new AuraHandler(static (s, h, a, apply) => s.ApplyReflectSchoolMods(h, a, apply), null),
    }));

    /// <summary>
    /// Add the SPELL_AURA_DUMMY apply/remove case of one spell (a case of vmangos <c>Aura::HandleAuraDummy</c>'s switch on the spell id, for
    /// example the owner-to-pet auras of SpellAuras.cpp:2201-2208). The DUMMY aura type has a single handler slot; it dispatches here by spell id,
    /// so any number of spells can each own a case. A second registration for the same spell is a startup error, like
    /// <see cref="RegisterProcScript"/>.
    /// </summary>
    public void RegisterDummyAuraHandler(uint spellId, Action<SpellSystem, SpellAuraHolder, SpellAura, bool> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (!_dummyAuraHandlers.TryAdd(spellId, apply))
        {
            throw new InvalidOperationException($"spell {spellId} already has a dummy aura handler");
        }
    }

    /// <summary>Whether <paramref name="spellId"/> has a DUMMY aura case (<see cref="RegisterDummyAuraHandler"/>).</summary>
    public bool HasDummyAuraHandler(uint spellId) => _dummyAuraHandlers.ContainsKey(spellId);

    private readonly Dictionary<uint, Action<SpellSystem, SpellAuraHolder, SpellAura, bool>> _dummyAuraHandlers = [];

    /// <summary>
    /// A periodic SPELL_AURA_DUMMY (vmangos Aura::Aura's switch that sets m_isPeriodic and periodictime for a few dummy spells, SpellAuras.cpp
    /// ~1760-1790, with its Aura::PeriodicDummyTick case): the aura ticks every <paramref name="periodMs"/> and each tick runs <paramref name="tick"/>.
    /// A second registration for the same spell is a startup error.
    /// </summary>
    public void RegisterPeriodicDummyAura(uint spellId, uint periodMs, Action<SpellSystem, SpellAuraHolder, SpellAura> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentOutOfRangeException.ThrowIfZero(periodMs);
        if (!_periodicDummyAuras.TryAdd(spellId, (periodMs, tick)))
        {
            throw new InvalidOperationException($"spell {spellId} already has a periodic dummy aura");
        }
    }

    private readonly Dictionary<uint, (uint PeriodMs, Action<SpellSystem, SpellAuraHolder, SpellAura> Tick)> _periodicDummyAuras = [];

    /// <summary>Whether <paramref name="spellId"/> has a periodic DUMMY aura tick (<see cref="RegisterPeriodicDummyAura"/>).</summary>
    public bool HasPeriodicDummyAura(uint spellId) => _periodicDummyAuras.ContainsKey(spellId);

    private void TickDummyAura(SpellAuraHolder holder, SpellAura aura)
    {
        if (_periodicDummyAuras.TryGetValue(holder.Spell.Id, out (uint PeriodMs, Action<SpellSystem, SpellAuraHolder, SpellAura> Tick) dummy))
        {
            dummy.Tick(this, holder, aura);
        }
    }

    private void ApplyDummyAura(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (_dummyAuraHandlers.TryGetValue(holder.Spell.Id, out Action<SpellSystem, SpellAuraHolder, SpellAura, bool>? handler))
        {
            handler(this, holder, aura, apply);
        }
    }

    /// <summary>
    /// SPELL_EFFECT_APPLY_AURA for a handler outside this file (vmangos Spell::EffectApplyAura): the effect's aura joins the target's pending
    /// holder. The area aura effects that only put their source on the caster (vmangos Spell::EffectApplyAreaAura) call it for the caster.
    /// </summary>
    internal void ApplyAuraEffect(SpellEffectContext context) => EffectApplyAura(context);

    /// <summary>
    /// vmangos <c>aura->ApplyModifier(false); m_modifier.m_amount = amount; aura->ApplyModifier(true)</c>: a script sets one aura's amount, the
    /// handler sees the old amount go and the new one come (a periodic aura's next tick deals it).
    /// </summary>
    internal void SetAuraAmount(SpellAuraHolder holder, SpellAura aura, int amount)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(aura);
        if (aura.Amount == amount)
        {
            return;
        }

        ApplyAuraModifier(holder, aura, false);
        aura.Amount = amount;
        ApplyAuraModifier(holder, aura, true);
        RaiseHolderAmountsChanged(holder);
    }

    /// <summary>vmangos SpellAuraHolder::ModStackAmount(num) for a script (see the private overload): true when the stacks ran out.</summary>
    internal bool ModAuraStackAmount(SpellAuraHolder holder, int num)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return ModStackAmount(holder, num);
    }

    /// <summary>
    /// vmangos <c>holder->Refresh(caster, target, holder)</c> with the holder itself (SpellAuras.cpp:365-378, Aura::Refresh :311-320): the duration
    /// stays what it is, the apply time is now, and every periodic aura starts its ticks over (tick count 0, a fresh timer).
    /// </summary>
    internal void RestartHolderTicks(SpellAuraHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        holder.AppliedAtUnixSeconds = UnixSecondsClock();
        holder.AppliedInProcEvent = CurrentProcEvent;
        foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
        {
            aura.PeriodicTimer = PeriodicTiming.InitialTimer(holder.Spell, aura);
            aura.TickCount = 0;
            aura.ElapsedWithoutTickMs = 0; // the fresh timer is a full period away: the stall watch starts over with it (Ignite's refresh)
        }

        WriteAuraApplications(holder);
        SendAuraDuration(holder);
    }

    private void ApplyReflectSchoolMods(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (apply && ResolveAuraCaster(holder) is { } caster && caster.GetCharmerOrOwnerPlayerOrSelf() is { } modOwner)
        {
            aura.Amount = (int)SpellModifiers.Apply(modOwner, holder.Spell, SpellModOp.ResistMissChance, aura.Amount);
        }
    }

    /// <summary>vmangos Spell::EffectApplyAura: add this effect's aura to the target's pending holder.</summary>
    private void EffectApplyAura(SpellEffectContext context)
    {
        if (!context.Target.IsAlive && !context.Spell.IsPassive && !context.Spell.IsDeathPersistent && !context.Spell.CanTargetDead)
        {
            return;
        }

        SpellEffectInfo effect = context.Effect;
        if (effect.AuraType == AuraType.None)
        {
            return;
        }

        if (!AuraHandlers.ContainsKey(effect.AuraType))
        {
            ReportUnsupported(context.Cast, context.EffectIndex, "aura", (uint)effect.AuraType);
        }

        context.PendingHolder ??= new SpellAuraHolder(
            context.Spell, context.Target, context.Caster,
            _auraCasterOwners.GetValue(context.Caster, static caster => new AuraCasterOwner(caster)),
            context.Cast.State == SpellCastState.Casting ? context.Cast.Timer : context.Cast.Duration)
        {
            IsItemEquipAura = context.Cast.IsItemEquipCast,
            IsTriggered = context.Cast.IsTriggered,
            Charges = context.Spell.ProcCharges > 0
                ? Math.Max(0, ModInt(context.Caster, context.Spell, SpellModOp.Charges, (int)context.Spell.ProcCharges))
                : 0,
        };
        if (context.Cast.CastItem is { } castItem && context.PendingHolder.CastItemGuid.IsEmpty)
        {
            context.PendingHolder.CastItemGuid = castItem.Guid;
        }

        if (context.Cast.State == SpellCastState.Casting && context.PendingHolder.ChannelTarget == default)
        {
            context.PendingHolder.ChannelTarget = new ObjectGuid(context.Caster.GetUInt64(UpdateFields.UnitFieldChannelObject));
        }

        int amount = context.Cast.CustomAuraAmounts is { } custom && custom.TryGetValue(context.EffectIndex, out int requested)
            ? requested
            : SnapshotAuraAmount(context);
        var aura = new SpellAura(context.EffectIndex, effect.AuraType, amount, ModifiedAmplitude(context.Caster, context.Spell, effect), effect.MiscValue,
            context.Target.PowerType);
        if (aura.Type == AuraType.Dummy && _periodicDummyAuras.TryGetValue(context.Spell.Id, out (uint PeriodMs, Action<SpellSystem, SpellAuraHolder, SpellAura> Tick) dummy))
        {
            aura.Amplitude = dummy.PeriodMs; // vmangos Aura::Aura: m_isPeriodic = true, m_modifier.periodictime for the scripted dummy
        }

        aura.PeriodicTimer = PeriodicTiming.InitialTimer(context.Spell, aura);
        context.PendingHolder.SetAura(aura);
    }

    /// <summary>
    /// vmangos Aura::CalculatePeriodic (SpellAuras.cpp:8078-8083): a periodic aura type's amplitude through the caster's
    /// SPELLMOD_ACTIVATION_TIME modifiers (read without spending a charge); every other amplitude as the data states it.
    /// </summary>
    private uint ModifiedAmplitude(Unit caster, SpellInfo spell, SpellEffectInfo effect)
        => effect.Amplitude != 0 && PeriodicTiming.TakesActivationTimeMod(effect.AuraType)
            ? (uint)Math.Max(ModInt(caster, spell, SpellModOp.ActivationTime, (int)effect.Amplitude), 0)
            : effect.Amplitude;

    /// <summary>
    /// Put a holder on its target (vmangos Unit::AddSpellAuraHolder + SpellAuraHolder::_AddSpellAuraHolder):
    /// the same spell from the same caster stacks up to StackAmount and refreshes, otherwise
    /// replaces; a positive spell from another caster replaces too (simplified vmangos
    /// IsSingleFromSpell / stacking rules — docs/areas/spells.md). A slot is searched in the
    /// positive (0-31) or negative (32-47) range; the update fields and SMSG_UPDATE_AURA_DURATION follow.
    /// </summary>
    internal void ValidateAuraHolder(SpellAuraHolder holder, bool requirePositiveDuration = false)
    {
        bool invalidDuration = holder.InitialDurationArg < -1
            || (holder.IsPermanent
                ? holder.Duration != -1 || holder.MaxDuration != -1
                : holder.MaxDuration <= 0
                    || holder.Duration > holder.MaxDuration
                    || (requirePositiveDuration ? holder.Duration <= 0 : holder.Duration < 0));
        if (invalidDuration && !holder.DurationInvalidReported)
        {
            holder.DurationInvalidReported = true;
            AnomalyLedger.Shared.Report(
                "aura",
                "duration-invalid",
                FormatSpellSubject(holder.Spell.Id),
                $"target={FormatUnitSubject(holder.Target)} durationMs={holder.Duration} maxDurationMs={holder.MaxDuration} initialDurationArg={holder.InitialDurationArg} permanent={holder.IsPermanent}",
                AnomalySeverity.Warning);
            if (holder.IsPermanent)
            {
                holder.MaxDuration = -1;
                holder.Duration = -1;
            }
            else
            {
                if (holder.MaxDuration <= 0)
                {
                    holder.MaxDuration = 300;
                }

                if (holder.Duration > holder.MaxDuration || (requirePositiveDuration && holder.Duration <= 0))
                {
                    holder.Duration = holder.MaxDuration;
                }
                else if (holder.Duration < 0)
                {
                    holder.Duration = 0;
                }
            }
        }

        uint maxStacks = Math.Max(holder.Spell.StackAmount, 1u);
        if ((holder.StackAmount == 0 || holder.StackAmount > maxStacks) && !holder.StackOverflowReported)
        {
            holder.StackOverflowReported = true;
            AnomalyLedger.Shared.Report(
                "aura",
                "stack-overflow",
                FormatSpellSubject(holder.Spell.Id),
                $"target={FormatUnitSubject(holder.Target)} stackAmount={holder.StackAmount} maxStackAmount={holder.Spell.StackAmount}",
                AnomalySeverity.Warning);
            holder.StackAmount = (byte)Math.Clamp((uint)holder.StackAmount, 1u, maxStacks);
        }

        foreach (SpellAura? aura in holder.AuraSpan)
        {
            if (aura is not null)
            {
                ValidateAuraPeriodicState(holder, aura);
            }
        }
    }

    private static void ValidateAuraPeriodicState(SpellAuraHolder holder, SpellAura aura)
    {
        bool missingExpectedPeriod = PeriodicTiming.TakesActivationTimeMod(aura.Type)
            && aura.Type != AuraType.ObsModMana
            && !aura.IsPeriodic;
        bool invalidTimerOrPeriod = aura.IsPeriodic
            && ((int)aura.Period <= 0
                || aura.PeriodicTimer > (int)aura.MaximumPeriodicTimer
                || aura.PeriodicTimer < -(int)aura.MaximumPeriodicTimer);
        if ((missingExpectedPeriod || invalidTimerOrPeriod) && !aura.PeriodicStalledReported)
        {
            aura.PeriodicStalledReported = true;
            AnomalyLedger.Shared.Report(
                "aura",
                "periodic-stalled",
                FormatSpellSubject(holder.Spell.Id),
                $"target={FormatUnitSubject(holder.Target)} effectIndex={aura.EffectIndex} auraType={aura.Type} periodMs={aura.Period} periodicTimerMs={aura.PeriodicTimer}",
                AnomalySeverity.Warning);
            if (aura.IsPeriodic && (int)aura.Period > 0
                && (aura.PeriodicTimer > (int)aura.MaximumPeriodicTimer || aura.PeriodicTimer < -(int)aura.MaximumPeriodicTimer))
            {
                aura.PeriodicTimer = (int)aura.Period;
            }
        }
    }

    internal void AddAuraHolder(SpellAuraHolder holder)
    {
        ValidateAuraHolder(holder, requirePositiveDuration: true);
        holder.ResolvePolarity(Store.Get);
        UnitSpellState state = GetOrCreateState(holder.Target);
        SpellAuraHolder? existing = state.Auras.FirstOrDefault(h => h.Spell.Id == holder.Spell.Id
            && (!h.IsItemEquipAura || !holder.IsItemEquipAura || h.ItemGuid == holder.ItemGuid)
            && (h.CasterGuid == holder.CasterGuid || holder.IsPositive));
        if (existing is not null)
        {
            // vmangos Unit.cpp:3134-3135: the refresh and stack branch is for the same caster AND the same cast item; the same spell from
            // another item of the caster replaces the holder ("can be only single").
            bool sameOwner = existing.CasterGuid == holder.CasterGuid && ReferenceEquals(existing.CasterOwner, holder.CasterOwner)
                && existing.CastItemGuid == holder.CastItemGuid;
            if (sameOwner && CanBeRefreshedBy(existing, holder))
            {
                RefreshHolderInPlace(existing, holder);
                return;
            }

            if (sameOwner && holder.Spell.StackAmount > 0)
            {
                // vmangos Unit.cpp:3149-3154: "Aura can stack on self -> Stack it".
                ModStackAmount(existing, holder.StackAmount, holder);
                return;
            }

            RemoveHolder(state, existing, AuraRemoveMode.Stack);
        }

        // vmangos Unit::AddSpellAuraHolder -> RemoveNoStackAurasDueToAuraHolder returning false (Unit.cpp:3216-3224): a stacking rule refuses
        // the new holder before it is added, so no handler, slot or packet ever sees it.
        foreach (Func<SpellAuraHolder, bool> refuses in HolderAddRefusals)
        {
            if (refuses(holder))
            {
                holder.IsRemoved = true;
                return;
            }
        }

        if (!MakeRoomForVisibleAura(state, holder))
        {
            holder.IsSlotLimited = true;
            holder.IsRemoved = true;
            return;
        }

        holder.AppliedAtUnixSeconds = UnixSecondsClock();
        holder.AppliedInProcEvent = CurrentProcEvent;
        holder.ApplyOrder = ++_auraApplyOrder;
        CalculateHeartbeat(holder);
        holder.Slot = holder.NeedsVisibleSlot ? FindFreeSlot(holder.Target, holder.IsPositive) : SpellAuraHolder.NoSlot;
        state.Auras.Add(holder);
        if (IsOwnedTrackingAura(holder.Spell))
        {
            _stalkedHolders.Add(holder);
        }

        SitDownForStandingCancelsAura(holder);
        if (holder.Slot != SpellAuraHolder.NoSlot)
        {
            WriteAuraFields(holder, add: true);
            VisibleAuraSlotChanged?.Invoke(holder.Target, holder.Slot);
            SendAuraDuration(holder);
        }

        foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
        {
            ApplyAuraModifier(holder, aura, true);
        }

        HandleSpellSpecificBoosts(holder, apply: true);
        RaiseHolderAdded(holder);
    }

    /// <summary>Remove every aura of <paramref name="spellId"/> from <paramref name="target"/> (vmangos Unit::RemoveAurasDueToSpell).</summary>
    public void RemoveAuras(Unit target, uint spellId) => RemoveAuras(target, spellId, AuraRemoveMode.Default);

    /// <summary>vmangos Unit::RemoveAurasDueToSpell(spellId, except, mode) / RemoveAurasDueToSpellByCancel: the removal reason is kept on the holder.</summary>
    public void RemoveAuras(Unit target, uint spellId, AuraRemoveMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsQuestSettlementPending(target) && GetState(target.Guid) is { } state && ReferenceEquals(state.Unit, target))
        {
            foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Spell.Id == spellId).ToArray())
            {
                RemoveHolder(state, holder, mode);
            }
        }
    }

    /// <summary>vmangos Unit::RemoveAurasByCasterSpell.</summary>
    public void RemoveAurasByCaster(Unit target, uint spellId, ObjectGuid caster)
        => RemoveAurasByCaster(target, spellId, caster, AuraRemoveMode.Default);

    /// <summary>vmangos Unit::RemoveAurasByCasterSpell with an explicit removal mode (e.g. <see cref="AuraRemoveMode.Channel"/>).</summary>
    public void RemoveAurasByCaster(Unit target, uint spellId, ObjectGuid caster, AuraRemoveMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsQuestSettlementPending(target) && GetState(target.Guid) is { } state && ReferenceEquals(state.Unit, target))
        {
            foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Spell.Id == spellId && h.CasterGuid == caster).ToArray())
            {
                RemoveHolder(state, holder, mode);
            }
        }
    }

    /// <summary>The <c>Auras</c> options (docs/areas/aura-engine.md); retail defaults until the world feature binds the configuration.</summary>
    public AuraOptions AuraOptions { get; set; } = new();

    private static readonly List<SpellAuraHolder> EmptyAuras = [];

    /// <summary>The aura holders on a unit (world thread).</summary>
    public List<SpellAuraHolder> GetAuras(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return GetState(unit.Guid) is { } state && ReferenceEquals(state.Unit, unit) ? state.Auras : EmptyAuras;
    }

    /// <summary>Whether <paramref name="unit"/> carries an aura holder of <paramref name="spellId"/> (world thread; allocation-free, hot-path safe).</summary>
    public bool HasAura(Unit unit, uint spellId)
    {
        IReadOnlyList<SpellAuraHolder> holders = GetAuras(unit);
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i].Spell.Id == spellId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Durations and periodic ticks (vmangos SpellAuraHolder::UpdateHolder → Aura::UpdateAura):
    /// each periodic aura ticks every amplitude ms; an expired holder is removed after its
    /// last tick.
    /// </summary>
    private void UpdateAuras(UnitSpellState state, uint diffMs)
    {
        if (state.Auras.Count == 0)
        {
            state.HasTickingHolders = false;
            return;
        }

        ObserveAuraCount(state);

        // Idle fast path: when every holder is a valid permanent passive with nothing to tick, UpdateAuraHolder would only re-validate
        // fields that are already valid, so the rent/copy/clear/return is skipped. The count is a read-only scan over the live list rather
        // than a figure maintained at each add/remove: holder state (Duration, AreaParent, IsRemoved, heartbeat fields, an aura's period)
        // is mutated from many files, so a scan cannot go stale. Units that do tick take the unchanged path below, in the same order.
        bool ticking = AuraIdleFastPathEnabled ? AnyTickingHolder(state) : true;
        state.HasTickingHolders = ticking;
        if (!ticking)
        {
            AuraIdleSkips++;
            return;
        }

        // A pooled snapshot (handlers may add or remove holders); reentrant, unlike a shared list.
        int count = state.Auras.Count;
        AuraSnapshotRents++;
        SpellAuraHolder[] snapshot = System.Buffers.ArrayPool<SpellAuraHolder>.Shared.Rent(count);
        state.Auras.CopyTo(snapshot);
        try
        {
            for (int i = 0; i < count; i++)
            {
                try
                {
                    UpdateAuraHolder(state, snapshot[i], diffMs);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("aura.tick", ex);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<SpellAuraHolder>.Shared.Return(snapshot, clearArray: true);
        }
    }

    /// <summary>
    /// Whether <see cref="UpdateAuraHolder"/> could do anything for <paramref name="holder"/> this tick. False only for a live, permanent,
    /// holder with no area parent, no stand-up cancel, no heartbeat and no periodic effect (a passive talent or racial) whose duration,
    /// stack and periodic fields already pass <see cref="ValidateAuraHolder"/> (or whose failure was already reported): for that holder
    /// UpdateAuraHolder only re-validates fields that are already valid. Every other holder (removed, timed, periodic, area child,
    /// heartbeat, standing-cancels, or with a field the validator would repair or report once) counts as ticking. Reads fields only.
    /// </summary>
    internal static bool HolderNeedsTick(SpellAuraHolder holder)
    {
        if (holder.IsRemoved || !holder.IsPermanent || holder.AreaParent is not null
            || (holder.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0
            || holder.HeartbeatRandValue > 0f || holder.PveHeartbeatTimer >= 0)
        {
            return true;
        }

        // ValidateAuraHolder's duration check: a bad duration is reported and repaired once (DurationInvalidReported); after that the
        // validator does nothing more for it, so an unrepairable InitialDurationArg must not keep the unit off the fast path.
        if (!holder.DurationInvalidReported && (holder.InitialDurationArg < -1 || holder.Duration != -1 || holder.MaxDuration != -1))
        {
            return true;
        }

        // ValidateAuraHolder's stack check: a zero or over-max stack is reported and clamped once (StackOverflowReported).
        if (!holder.StackOverflowReported && (holder.StackAmount == 0 || holder.StackAmount > Math.Max(holder.Spell.StackAmount, 1u)))
        {
            return true;
        }

        ReadOnlySpan<SpellAura?> auras = holder.AuraSpan;
        for (int i = 0; i < auras.Length; i++)
        {
            if (auras[i] is not { } aura)
            {
                continue;
            }

            // Mirrors ValidateAuraPeriodicState and the periodic loop: a periodic aura ticks; a non-periodic aura of a type that should
            // have a period is reported once (PeriodicStalledReported), after which it is inert again.
            if (aura.IsPeriodic
                || (PeriodicTiming.TakesActivationTimeMod(aura.Type) && aura.Type != AuraType.ObsModMana && !aura.PeriodicStalledReported))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any of <paramref name="state"/>'s holders needs a tick per <see cref="HolderNeedsTick"/> (allocation-free scan, no copy; stops at the first).</summary>
    internal static bool AnyTickingHolder(UnitSpellState state)
    {
        List<SpellAuraHolder> holders = state.Auras;
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i] is null || HolderNeedsTick(holders[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Test seam: false turns the idle fast path off so a scenario can be captured with the original always-snapshot behaviour.</summary>
    internal bool AuraIdleFastPathEnabled { get; set; } = true;

    /// <summary>A unit holding more aura holders than this is reported as <c>aura/holders-excess</c> (a client shows 32 buffs and 16 debuffs; passives and hidden auras add a few hundred at most).</summary>
    internal const int AuraHoldersExcessBound = 512;

    /// <summary>Test seam and cost counter: how many times <see cref="UpdateAuras"/> rented and filled a snapshot (one per unit per tick that had something to tick).</summary>
    internal long AuraSnapshotRents { get; private set; }

    /// <summary>Test seam: how many <see cref="UpdateAuras"/> passes skipped the snapshot because no holder needed ticking.</summary>
    internal long AuraIdleSkips { get; private set; }

    /// <summary>The largest <c>state.Auras.Count</c> any unit had at an aura update (a leak check; also named in the <c>aura/holders-excess</c> detail).</summary>
    internal int MaxAuraCountSeen { get; private set; }

    private void ObserveAuraCount(UnitSpellState state)
    {
        int held = state.Auras.Count;
        if (held > MaxAuraCountSeen)
        {
            MaxAuraCountSeen = held;
        }

        if (held <= AuraHoldersExcessBound)
        {
            state.AuraExcessReported = false;
            return;
        }

        if (state.AuraExcessReported)
        {
            return;
        }

        state.AuraExcessReported = true;
        AnomalyLedger.Shared.Report(
            "aura",
            "holders-excess",
            FormatUnitSubject(state.Unit),
            $"holders={held} bound={AuraHoldersExcessBound} maxSeen={MaxAuraCountSeen}",
            AnomalySeverity.Warning);
    }

    private void UpdateAuraHolder(UnitSpellState state, SpellAuraHolder holder, uint diffMs)
    {
        if (holder is null)
        {
            return;
        }

        if (holder.IsRemoved)
        {
            if (state.Auras.Contains(holder))
            {
                if (!holder.ExpiredReported)
                {
                    holder.ExpiredReported = true;
                    AnomalyLedger.Shared.Report(
                        "aura",
                        "expired-not-removed",
                        FormatSpellSubject(holder.Spell.Id),
                        $"target={FormatUnitSubject(holder.Target)} durationMs={holder.Duration} isRemoved=true",
                        AnomalySeverity.Warning);
                }

                state.Auras.Remove(holder);
            }

            return;
        }

        if (!holder.IsPermanent && holder.Duration <= 0)
        {
            if (holder.ExpiryBatchIndex is not null)
            {
                // Ran out earlier and is held for the spell batch: it goes at the first update in a later batch.
                if (!HoldAuraExpiry(holder))
                {
                    ExpireHolder(state, holder);
                }

                return;
            }

            if (!holder.ExpiredReported)
            {
                holder.ExpiredReported = true;
                AnomalyLedger.Shared.Report(
                    "aura",
                    "expired-not-removed",
                    FormatSpellSubject(holder.Spell.Id),
                    $"target={FormatUnitSubject(holder.Target)} durationMs={holder.Duration} maxDurationMs={holder.MaxDuration}",
                    AnomalySeverity.Warning);
            }

            RemoveHolder(state, holder, AuraRemoveMode.Expire);
            return;
        }

        ValidateAuraHolder(holder, requirePositiveDuration: false);

        if (holder.AreaParent is { } parent && (parent.IsRemoved || !ReferenceEquals(parent.Target.Map, holder.Target.Map)))
        {
            // vmangos AreaAura::Update: the aura goes with its source or when the owner left the map.
            RemoveHolder(state, holder);
            return;
        }

        if (IsQuestSettlementPending(holder.Target)
            || IsQuestSettlementPending(ResolveAuraCaster(holder)))
        {
            return;
        }

        // vmangos Unit::SetStandState (Unit.cpp:9302-9310): standing up removes STANDING_CANCELS auras (food, drink).
        if ((holder.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0
            && holder.Target.StandState is StandState.Stand or StandState.Dead)
        {
            RemoveHolder(state, holder);
            return;
        }

        if (UpdateHeartbeat(state, holder, diffMs))
        {
            return;
        }

        bool runs = !holder.IsPermanent && holder.Duration > 0;
        if (!holder.IsPermanent)
        {
            holder.Duration = Math.Max(0, holder.Duration - (int)diffMs);
        }

        // vmangos SpellAuraHolder::Update: the per-second power cost (Health Funnel) comes right after the duration step.
        if (runs)
        {
            holder.ExpiryBatchIndex = null; // running (again, after a refresh): no expiry is held
            ChargePerSecondCost(holder, diffMs);
            if (holder.IsRemoved)
            {
                return;
            }
        }

        IReadOnlyList<SpellAura?> auras = holder.Auras;
        for (int a = 0; a < auras.Count; a++)
        {
            if (auras[a] is not { } aura)
            {
                continue;
            }

            ValidateAuraPeriodicState(holder, aura);
            if (!aura.IsPeriodic || (int)aura.Period <= 0)
            {
                continue;
            }

            // vmangos Aura::Update: at most one tick per update (Auras:PeriodicCatchUp restores the burst).
            int due = PeriodicTiming.Advance(aura, diffMs, AuraOptions.PeriodicCatchUp);
            if (due > 0)
            {
                aura.ElapsedWithoutTickMs = 0;
            }
            else
            {
                aura.ElapsedWithoutTickMs += diffMs;
                if ((aura.PeriodicTimer <= 0 || aura.ElapsedWithoutTickMs > (long)aura.MaximumPeriodicTimer)
                    && !aura.PeriodicStalledReported)
                {
                    aura.PeriodicStalledReported = true;
                    AnomalyLedger.Shared.Report(
                        "aura",
                        "periodic-stalled",
                        FormatSpellSubject(holder.Spell.Id),
                        $"target={FormatUnitSubject(holder.Target)} effectIndex={aura.EffectIndex} auraType={aura.Type} periodMs={aura.Period} periodicTimerMs={aura.PeriodicTimer} elapsedWithoutTickMs={aura.ElapsedWithoutTickMs}",
                        AnomalySeverity.Warning);
                }
            }

            for (int tick = 0; tick < due && !holder.IsRemoved; tick++)
            {
                aura.TickCount++;
                using ProcEventScope tickEvent = BeginProcEvent(); // one tick is one event: its procs, its damage and the kill it causes
                try
                {
                    AuraHandlers.GetValueOrDefault(aura.Type)?.Tick?.Invoke(this, holder, aura);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("aura.tick", ex);
                }
            }
        }

        if (!holder.IsRemoved && !holder.IsPermanent && holder.Duration == 0 && !HoldAuraExpiry(holder))
        {
            ExpireHolder(state, holder);
        }
    }

    /// <summary>A timed holder ran out: removed with Expire (Channel while its caster still channels it).</summary>
    private void ExpireHolder(UnitSpellState state, SpellAuraHolder holder)
    {
        AuraRemoveMode expireMode = holder.Spell.IsChanneled
            && ResolveAuraCaster(holder) is { } channelCaster
            && channelCaster.GetUInt32(UpdateFields.UnitChannelSpell) == holder.Spell.Id
            ? AuraRemoveMode.Channel
            : AuraRemoveMode.Expire;
        RemoveHolder(state, holder, expireMode);
        if ((!holder.IsRemoved || state.Auras.Contains(holder)) && !holder.ExpiredReported)
        {
            holder.ExpiredReported = true;
            AnomalyLedger.Shared.Report(
                "aura",
                "expired-not-removed",
                FormatSpellSubject(holder.Spell.Id),
                $"target={FormatUnitSubject(holder.Target)} durationMs={holder.Duration} reason=post-remove-still-present",
                AnomalySeverity.Warning);
            state.Auras.Remove(holder);
        }
    }

    /// <summary>
    /// vmangos SpellAuraHolder::CanBeRefreshedBy (SpellAuras.cpp:381-394): the same caster's same spell, with no stack amount
    /// and no proc charges (a charge aura refreshes through the replace path, which fixes a client visual bug).
    /// </summary>
    internal static bool CanBeRefreshedBy(SpellAuraHolder existing, SpellAuraHolder other)
        => existing.CasterGuid == other.CasterGuid && existing.Spell.Id == other.Spell.Id
            && existing.Spell.StackAmount == 0 && existing.Spell.ProcCharges == 0;

    /// <summary>
    /// vmangos SpellAuraHolder::Refresh + Aura::Refresh (SpellAuras.cpp:311-379): the SAME holder keeps its slot, takes the new
    /// duration and apply time, restarts its periodic timers and re-applies an amount that changed; no remove or add happens
    /// (so no HolderRemoved/HolderAdded, and diminishing returns do not see a crowd control end and restart). Documented
    /// deviation: an unchanged amount is not un-applied and re-applied (vmangos does, unconditionally), so side-effect-only
    /// handlers do not fire twice; the 1.7 "do not reset health/mana on a stat refresh" rule belongs to the stat lane.
    /// </summary>
    private void RefreshHolderInPlace(SpellAuraHolder existing, SpellAuraHolder fresh)
    {
        existing.AppliedAtUnixSeconds = UnixSecondsClock();
        existing.AppliedInProcEvent = CurrentProcEvent; // SpellAuras.cpp:368: the refresh resets m_applyTime, so the refreshing hit does not proc it
        existing.Duration = fresh.Duration;
        existing.MaxDuration = fresh.MaxDuration;
        existing.ChannelTarget = fresh.ChannelTarget;
        ValidateAuraHolder(existing, requirePositiveDuration: true);
        bool amountsChanged = false;
        for (int i = 0; i < SpellConstants.MaxEffects; i++)
        {
            if (existing.Auras[i] is not { } aura || fresh.Auras[i] is not { } source)
            {
                continue;
            }

            // Aura::Refresh runs CalculatePeriodic again (SpellAuras.cpp:319): the interval follows the caster's current
            // ACTIVATION_TIME mods. Documented deviation: vmangos applies them to the already modified period, so a flat mod would
            // compound on every refresh; the fresh holder's amplitude (modified once, from the data) is taken instead.
            aura.Amplitude = source.Amplitude;
            aura.PeriodicTimer = PeriodicTiming.InitialTimer(existing.Spell, aura);
            aura.TickCount = 0;
            aura.ElapsedWithoutTickMs = 0;
            if (source.Amount != aura.Amount)
            {
                ApplyAuraModifier(existing, aura, false);
                aura.Amount = source.Amount;
                aura.UnitAmount = source.UnitAmount;
                ApplyAuraModifier(existing, aura, true);
                amountsChanged = true;
            }
        }

        WriteAuraApplications(existing);
        SendAuraDuration(existing);
        if (amountsChanged)
        {
            RaiseHolderAmountsChanged(existing);
        }
    }

    /// <summary>
    /// vmangos SpellAuraHolder::ModStackAmount / SetStackAmount (SpellAuras.cpp:6942-6999): the stack count moves by
    /// <paramref name="num"/> within the spell's limit; every aura amount becomes stacks times its one-stack value, un-applied
    /// with the old amount and applied with the new one when it changes; an increase refreshes the duration. Returns true when
    /// the stacks ran out (the caller removes the holder). <paramref name="fresh"/> is the new cast's holder (its one-stack
    /// amounts and duration); a dispel passes none.
    /// </summary>
    private bool ModStackAmount(SpellAuraHolder holder, int num, SpellAuraHolder? fresh = null)
    {
        int proto = (int)holder.Spell.StackAmount;
        if (proto == 0)
        {
            return true;
        }

        int stacks = holder.StackAmount + num;
        if (stacks > proto)
        {
            stacks = proto;
        }
        else if (stacks <= 0)
        {
            return true;
        }

        bool refresh = stacks >= holder.StackAmount;
        bool amountsChanged = false;
        if (stacks != holder.StackAmount)
        {
            holder.StackAmount = (byte)stacks;
            WriteAuraApplications(holder);
            for (int i = 0; i < SpellConstants.MaxEffects; i++)
            {
                if (holder.Auras[i] is not { } aura)
                {
                    continue;
                }

                if (fresh?.Auras[i] is { } source)
                {
                    aura.UnitAmount = source.UnitAmount;
                }

                int amount = aura.UnitAmount * stacks;
                if (amount != aura.Amount)
                {
                    ApplyAuraModifier(holder, aura, false);
                    aura.Amount = amount;
                    ApplyAuraModifier(holder, aura, true);
                    amountsChanged = true;
                }
            }
        }

        if (amountsChanged)
        {
            RaiseHolderAmountsChanged(holder);
        }

        if (refresh)
        {
            holder.Duration = holder.MaxDuration = fresh?.MaxDuration ?? holder.MaxDuration;
            SendAuraDuration(holder);
        }

        return false;
    }

    /// <summary>
    /// vmangos SpellAuraHolder::_AddSpellAuraHolder (SpellAuras.cpp:6803-6806): an aura that STANDING_CANCELS (food, drink) sits
    /// its target down. Standing up removes it again (<see cref="UpdateAuras"/>; vmangos does it inside SetStandState).
    /// </summary>
    private static void SitDownForStandingCancelsAura(SpellAuraHolder holder)
    {
        if ((holder.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) == 0 || IsSittingDown(holder.Target))
        {
            return;
        }

        if (holder.Target is Player player)
        {
            player.SetStandState(StandState.Sit);
        }
        else
        {
            holder.Target.StandState = StandState.Sit;
        }
    }

    /// <summary>vmangos Unit::IsSittingDown: sitting on the ground or in any chair.</summary>
    internal static bool IsSittingDown(Unit unit) => unit.StandState is StandState.Sit or StandState.SitChair or StandState.SitLowChair
        or StandState.SitMediumChair or StandState.SitHighChair;

    private void RemoveHolder(UnitSpellState state, SpellAuraHolder holder, AuraRemoveMode mode = AuraRemoveMode.Default)
    {
        if (holder.IsRemoved)
        {
            return;
        }

        holder.RemoveMode = mode;
        holder.IsRemoved = true;
        state.Auras.Remove(holder);
        if (_stalkedHolders.Count > 0)
        {
            _stalkedHolders.Remove(holder);
        }

        if (holder.Slot != SpellAuraHolder.NoSlot)
        {
            WriteAuraFields(holder, add: false);
            VisibleAuraSlotChanged?.Invoke(holder.Target, holder.Slot);
        }

        foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
        {
            ApplyAuraModifier(holder, aura, false);
        }

        HandleSpellSpecificBoosts(holder, apply: false);
        RaiseHolderRemoved(holder);
        StartCooldownOnAuraEnd(holder, mode);

        if (holder.AreaParent is { } parent && parent.AreaChildren.TryGetValue(holder.Target.Guid, out SpellAuraHolder? child)
            && ReferenceEquals(child, holder))
        {
            parent.AreaChildren.Remove(holder.Target.Guid);
        }

        foreach (SpellAuraHolder areaChild in holder.AreaChildren.Values.ToArray())
        {
            if (!IsQuestSettlementPending(areaChild.Target)
                && GetState(areaChild.Target.Guid) is { } childState && ReferenceEquals(childState.Unit, areaChild.Target))
            {
                RemoveHolder(childState, areaChild);
            }
        }

        // Held children still reference this removed parent. Their first update after the
        // settlement ends removes them through UpdateAuras without mutating the held player.
        holder.AreaChildren.Clear();
    }

    /// <summary>
    /// SPELL_ATTR_COOLDOWN_ON_EVENT (Spell.dbc Attributes 0x02000000; Stealth, Shadowmeld, Prowl and the other "cooldown starts when the aura
    /// fades" spells): the cast started no cooldown (<see cref="AddCooldown"/>) and the client keeps the button disabled while the aura lives,
    /// so the removal of the aura is the event. The caster's normal recovery starts now and SMSG_COOLDOWN_EVENT lets the client start its own
    /// timer (vmangos Player::AddCooldown "haveToSendEvent"). Every removal cause counts (cancel, damage or movement break, dispel, expiry,
    /// death) except a replacement by the same spell, whose own removal is the event; an area child follows its parent's holder.
    /// </summary>
    private void StartCooldownOnAuraEnd(SpellAuraHolder holder, AuraRemoveMode mode)
    {
        if (mode == AuraRemoveMode.Stack || holder.AreaParent is not null || !holder.Spell.HasAttribute(SpellAttributes.CooldownOnEvent))
        {
            return;
        }

        Unit? owner = holder.CasterGuid == holder.Target.Guid ? holder.Target : ResolveAuraCaster(holder);
        if (owner is not { IsInWorld: true })
        {
            return;
        }

        UnitSpellState ownerState = GetOrCreateState(owner);
        AddCooldown(ownerState, holder.Spell, triggered: false, onEvent: true);
        if (owner is Player player)
        {
            // vmangos Player::AddCooldown (Player.cpp:22220-22230): the client keeps a category cooldown under the spell that started it, so when
            // another spell of the category holds it (another Prowl or Stealth rank) that spell is told as well.
            uint category = holder.Spell.Category;
            if (category != 0 && ownerState.CategoryOwners.TryGetValue(category, out uint categoryOwner) && categoryOwner != holder.Spell.Id
                && ownerState.CategoryCooldowns.ContainsKey(category))
            {
                player.Session.Send(WorldOpcode.SmsgCooldownEvent, SpellPackets.BuildCooldownEvent(categoryOwner, player.Guid));
            }

            player.Session.Send(WorldOpcode.SmsgCooldownEvent, SpellPackets.BuildCooldownEvent(holder.Spell.Id, player.Guid));
        }
    }

    // --- update fields (vmangos SpellAuraHolder::SetAura / SetAuraFlag / SetAuraLevel / UpdateAuraApplication)

    private static byte FindFreeSlot(Unit target, bool positive)
    {
        int start = positive ? 0 : MaxPositiveAuras;
        int end = positive ? MaxPositiveAuras : MaxAuras;
        for (int i = start; i < end; i++)
        {
            if (target.GetUInt32(UpdateFields.UnitFieldAura + i) == 0)
            {
                return (byte)i;
            }
        }

        return SpellAuraHolder.NoSlot;
    }

    private static void WriteAuraFields(SpellAuraHolder holder, bool add)
    {
        Unit target = holder.Target;
        int slot = holder.Slot;
        target.SetUInt32(UpdateFields.UnitFieldAura + slot, add ? holder.Spell.Id : 0);

        // AURAFLAGS: 4 bits per slot, 8 slots per field.
        int flagIndex = UpdateFields.UnitFieldAuraflags + (slot >> 3);
        int flagShift = (slot & 7) << 2;
        uint flags = target.GetUInt32(flagIndex) & ~(0xFu << flagShift);
        if (add)
        {
            flags |= holder.AuraFlags << flagShift;
        }

        target.SetUInt32(flagIndex, flags);

        // AURALEVELS: one byte per slot.
        SetSlotByte(target, UpdateFields.UnitFieldAuralevels, slot, add ? holder.CasterLevel : (byte)0);
        if (add)
        {
            WriteAuraApplications(holder);
        }
        else
        {
            SetSlotByte(target, UpdateFields.UnitFieldAuraapplications, slot, 0);
        }
    }

    /// <summary>AURAAPPLICATIONS holds charges * stacks - 1, clamped (vmangos UpdateAuraApplication, <see cref="SpellAuraHolder.UpdateAuraApplication"/>).</summary>
    private static void WriteAuraApplications(SpellAuraHolder holder) => holder.UpdateAuraApplication();

    private static void SetSlotByte(Unit target, int baseIndex, int slot, byte value)
    {
        int index = baseIndex + (slot / 4);
        int shift = (slot % 4) * 8;
        uint field = target.GetUInt32(index) & ~(0xFFu << shift);
        target.SetUInt32(index, field | ((uint)value << shift));
    }

    /// <summary>SMSG_UPDATE_AURA_DURATION to the target's client for visible, non-permanent auras (vmangos SpellAuraHolder::UpdateAuraDuration).</summary>
    private static void SendAuraDuration(SpellAuraHolder holder)
    {
        if (holder.Slot == SpellAuraHolder.NoSlot || holder.IsPermanent || holder.Target is not Player player)
        {
            return;
        }

        player.Session.Send(WorldOpcode.SmsgUpdateAuraDuration, SpellPackets.BuildUpdateAuraDuration(holder.Slot, (uint)holder.Duration));
    }

    // --- aura type handlers ---------------------------------------------------------------

    private Unit? ResolveAuraCaster(SpellAuraHolder holder)
    {
        Unit? caster = holder.CasterOwner.Caster;
        return caster is { IsInWorld: true }
            && ReferenceEquals(caster.Map, holder.Target.Map)
            && ReferenceEquals(Units.Find(holder.Target, holder.CasterGuid), caster)
                ? caster
                : null;
    }

    private void RevokeAuraCaster(Unit unit)
    {
        if (_auraCasterOwners.TryGetValue(unit, out AuraCasterOwner? owner))
        {
            owner.Revoke();
            _auraCasterOwners.Remove(unit);
        }
    }

    /// <summary>
    /// vmangos Aura::PeriodicTick SPELL_AURA_PERIODIC_DAMAGE / _PERCENT: the amount (percent of the
    /// target's maximum health for _PERCENT), less a partial resist, goes to <see cref="IDamageSink"/>;
    /// channels on the target may be delayed; SMSG_PERIODICAURALOG to the target's set.
    /// </summary>
    private void TickPeriodicDamage(SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (!target.IsAlive || aura.Amount <= 0)
        {
            return;
        }

        uint amount = aura.Type == AuraType.PeriodicDamagePercent
            ? (uint)((ulong)target.MaxHealth * (uint)aura.Amount / 100)
            : (uint)aura.Amount;
        Unit? resolvedCaster = ResolveAuraCaster(holder);
        Unit caster = resolvedCaster ?? target;
        // Class scripts (Consecration, Curse of Doom): Periodic/SpellSystem.PeriodicDamageScripts.cs, vmangos SpellAuras.cpp:5869-5874.
        IPeriodicDamageScript? script = aura.Type == AuraType.PeriodicDamage ? FindPeriodicDamageScript(holder.Spell.Id) : null;
        float? scripted = script?.CalculateTick(this, holder, aura, caster, amount);
        float ramp = aura.Type == AuraType.PeriodicDamage ? PeriodicDamageRamp(holder.Spell, aura.TickCount) : 0f;
        amount = scripted is { } custom
            ? ModifyTick(SpellAmountStage.DamageOverTimeTick, holder, aura, caster, custom + ramp)
            : ramp == 0f
            ? ModifyTick(SpellAmountStage.DamageOverTimeTick, holder, aura, caster, amount)
            : ModifyTick(SpellAmountStage.DamageOverTimeTick, holder, aura, caster, amount + ramp);
        if (ImmunityRules.IsImmuneToDamage(this, target, holder.Spell.SchoolMask(), holder.Spell))
        {
            // vmangos Aura::PeriodicTick: an immune target takes nothing and the client is told (SpellAuras.cpp:5839-5841).
            SendToSet(caster, WorldOpcode.SmsgSpellordamageImmune, SpellRulePackets.BuildSpellOrDamageImmune(caster.Guid, target.Guid, holder.Spell.Id), includeSelf: true);
            return;
        }

        uint resisted = ApplyResist(caster, target, holder.Spell, ref amount, periodic: true);
        uint original = amount + resisted;
        uint absorbed = AbsorbDamage(caster, target, holder.Spell.SchoolMask(), amount, holder.Spell, periodic: true);
        amount -= absorbed;
        if (ResolveAuraCaster(holder) is { } procCaster)
        {
            FirePeriodicDamageProcs(procCaster, target, holder.Spell, amount, original); // SpellAuras.cpp:5902-5917, before the damage
        }

        // A DOT tick links no combat unless the spell is a channel (vmangos Unit::DealDamage ShouldEnterCombat, Unit.cpp:696-714): a creature
        // whose poison keeps ticking on a player after it evaded must not be pulled back into combat with nothing on its threat list.
        uint dealt = Damage.DealSpellDamage(caster, target, holder.Spell, amount, periodic: true, startsCombat: holder.Spell.IsChanneled, critical: false, durabilityLoss: true,
            reflected: holder.IsReflected && ReferenceEquals(caster, target));
        if (dealt > 0 && !target.IsAlive && resolvedCaster is null && target is Player player)
        {
            player.LastEnvironmentalDeath = null; // this death has one source
            player.LastMissingCasterPeriodicDeath = true;
        }
        OnDamageTaken(target, caster, dealt, periodic: true, absorbed, holder.Spell.Id);
        SendToSet(target, WorldOpcode.SmsgPeriodicauralog, SpellPackets.BuildPeriodicAuraLog(
            target.Guid, holder.CasterGuid, holder.Spell.Id, new PeriodicLogEntry(aura.Type, dealt, (uint)holder.Spell.School, Absorbed: absorbed, Resisted: resisted)), includeSelf: true);
        if (script is not null && ResolveAuraCaster(holder) is { } scriptCaster)
        {
            script.AfterTick(this, holder, aura, scriptCaster, dealt);
        }
    }

    /// <summary>
    /// The tick-index ramp vmangos adds to the snapshotted amount before the target side (Aura::PeriodicTick, SpellAuras.cpp:5867-5872):
    /// Curse of Agony (warlock family bit 10) <c>(-1 + (tick - 1) / 4) * SimpleValue(0) / 2</c> and Starshards (priest family bit 21)
    /// <c>(-1 + (tick - 1) / 2) * SimpleValue(0) / 3</c>, with integer division on the tick term; 0 for every other spell.
    /// </summary>
    internal static float PeriodicDamageRamp(SpellInfo spell, int tick)
    {
        if (spell.IsFitToFamily(CurseOfAgonyFamily, CurseOfAgonyFlagBit))
        {
            return (-1 + ((tick - 1) / 4)) * (spell.SimpleValue(0) / 2.0f);
        }

        if (spell.IsFitToFamily(StarshardsFamily, StarshardsFlagBit))
        {
            return (-1 + ((tick - 1) / 2)) * (spell.SimpleValue(0) / 3.0f);
        }

        return 0f;
    }

    private const uint CurseOfAgonyFamily = 5;   // SPELLFAMILY_WARLOCK
    private const int CurseOfAgonyFlagBit = 10;  // CF_WARLOCK_CURSE_OF_AGONY
    private const uint StarshardsFamily = 6;     // SPELLFAMILY_PRIEST
    private const int StarshardsFlagBit = 21;    // CF_PRIEST_STARSHARDS

    /// <summary>
    /// vmangos Aura::PeriodicTick SPELL_AURA_PERIODIC_HEAL / OBS_MOD_HEALTH (the latter a percent
    /// of maximum health): healing through <see cref="IDamageSink.Heal"/>, logged with SMSG_PERIODICAURALOG; a target immune to the
    /// spell's school (<see cref="ImmunityRules.IsImmuneToSchool"/>) is not healed.
    /// </summary>
    private void TickPeriodicHeal(SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (!target.IsAlive || aura.Amount <= 0)
        {
            return;
        }

        uint amount = aura.Type == AuraType.ObsModHealth
            ? (uint)((ulong)target.MaxHealth * (uint)aura.Amount / 100)
            : (uint)aura.Amount;
        Unit caster = ResolveAuraCaster(holder) ?? target;
        if (ImmunityRules.IsImmuneToSchool(this, target, holder.Spell, aura.IsPositive))
        {
            // vmangos Aura::PeriodicTick: IsImmuneToSchool(spell, 1 << effect) heals nothing and tells the client (SpellAuras.cpp:6031-6035).
            SendToSet(caster, WorldOpcode.SmsgSpellordamageImmune, SpellRulePackets.BuildSpellOrDamageImmune(caster.Guid, target.Guid, holder.Spell.Id), includeSelf: true);
            return;
        }

        amount = ModifyTick(SpellAmountStage.HealOverTimeTick, holder, aura, caster, amount);
        bool wasFull = target.Health >= target.MaxHealth;
        uint healed = Damage.Heal(caster, target, holder.Spell, amount, periodic: true);
        if (ResolveAuraCaster(holder) is { } procCaster)
        {
            // SpellAuras.cpp:6060-6085: a tick on a full target still procs (amount 1).
            FirePeriodicHealProcs(procCaster, target, holder.Spell, wasFull ? 1 : healed, amount);
        }

        SendToSet(target, WorldOpcode.SmsgPeriodicauralog, SpellPackets.BuildPeriodicAuraLog(
            target.Guid, holder.CasterGuid, holder.Spell.Id, new PeriodicLogEntry(aura.Type, healed, 0)), includeSelf: true);
    }

    /// <summary>
    /// vmangos Aura::PeriodicTick SPELL_AURA_PERIODIC_ENERGIZE / OBS_MOD_MANA (MiscValue = power;
    /// OBS_MOD_MANA is a percent of the maximum), logged with SMSG_PERIODICAURALOG.
    /// </summary>
    private void TickPeriodicEnergize(SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        int powerType = aura.Type == AuraType.ObsModMana ? (int)PowerType.Mana : aura.MiscValue;
        if (!target.IsAlive || aura.Amount <= 0 || powerType is < 0 or > (int)PowerType.Happiness)
        {
            return;
        }

        // vmangos Aura::PeriodicTick (SpellAuras.cpp:6217-6226, 6266-6275): with a caster, IsImmuneToSchool stops the tick and tells the client.
        if (ResolveAuraCaster(holder) is { } immuneTo && ImmunityRules.IsImmuneToSchool(this, target, holder.Spell, aura.IsPositive))
        {
            SendToSet(immuneTo, WorldOpcode.SmsgSpellordamageImmune, SpellRulePackets.BuildSpellOrDamageImmune(immuneTo.Guid, target.Guid, holder.Spell.Id), includeSelf: true);
            return;
        }

        var power = (PowerType)powerType;
        uint amount = aura.Type == AuraType.ObsModMana
            ? (uint)((ulong)target.GetUInt32(UpdateFields.UnitFieldMaxpower1 + powerType) * (uint)aura.Amount / 100)
            : (uint)aura.Amount;
        uint before = GetPower(target, power);
        SetPower(target, power, before + amount);
        uint after = GetPower(target, power);
        uint effectiveGain = after > before ? after - before : 0;
        SendToSet(target, WorldOpcode.SmsgPeriodicauralog, SpellPackets.BuildPeriodicAuraLog(
            target.Guid, holder.CasterGuid, holder.Spell.Id, new PeriodicLogEntry(aura.Type, amount, (uint)powerType)), includeSelf: true);
        if (effectiveGain != 0 && power is not PowerType.Mana and not PowerType.Happiness
            && ResolveAuraCaster(holder) is { } caster)
        {
            Damage.AssistPeriodicEnergizeThreat(caster, target, holder.Spell, effectiveGain, power);
        }
    }

    /// <summary>vmangos Aura::PeriodicTick SPELL_AURA_PERIODIC_TRIGGER_SPELL: the caster casts EffectTriggerSpell at the target, triggered.</summary>
    private void TickTriggerSpell(SpellAuraHolder holder, SpellAura aura)
    {
        // A spell with its own tick script (vmangos Aura::TriggerSpell's switch on the aura id, e.g. Frenzied Regeneration).
        if (_periodicTriggerScripts.TryGetValue(holder.Spell.Id, out PeriodicTriggerScript? script))
        {
            script(this, holder, aura);
            return;
        }

        uint triggerSpell = holder.Spell.Effects[aura.EffectIndex].TriggerSpell;
        Unit caster = ResolveAuraCaster(holder) ?? holder.Target;
        Unit triggerTarget = holder.Target;

        // vmangos Aura::TriggerSpell (SpellAuras.cpp:1519-1536): a channelled spell whose trigger aura sits on its
        // own caster (Arcane Missiles: TARGET_UNIT_CASTER) casts the triggered spell at the CHANNEL TARGET. A trigger
        // aura on the channel target casts from the caster at that target, which is the default below.
        if (holder.Spell.IsChanneled && ReferenceEquals(holder.Target, caster)
            && holder.ChannelTarget.Value != 0 && Units.Find(caster, holder.ChannelTarget) is { } channelTarget)
        {
            triggerTarget = channelTarget;
        }

        if (Store.Get(triggerSpell) is not null)
        {
            CastSpellTriggeredByAura(caster, triggerSpell, SpellCastTargets.ForUnit(triggerTarget.Guid), holder.Spell);
        }
    }
}
