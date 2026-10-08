using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
using ArcaneCore.Game.Spells.Rules.Immunity;
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
        [AuraType.Dummy] = new AuraHandler(null, null),
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
        if (!AuraHandlers.ContainsKey(effect.AuraType))
        {
            ReportUnsupported("aura", (uint)effect.AuraType, context.Spell.Id);
        }

        context.PendingHolder ??= new SpellAuraHolder(
            context.Spell, context.Target, context.Caster,
            _auraCasterOwners.GetValue(context.Caster, static caster => new AuraCasterOwner(caster)),
            context.Cast.State == SpellCastState.Casting ? context.Cast.Timer : context.Cast.Duration)
        {
            IsItemEquipAura = context.Cast.IsItemEquipCast,
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
    internal void AddAuraHolder(SpellAuraHolder holder)
    {
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

        holder.AppliedAtUnixSeconds = UnixSecondsClock();
        holder.AppliedInProcEvent = CurrentProcEvent;
        holder.Slot = holder.NeedsVisibleSlot ? FindFreeSlot(holder.Target, holder.IsPositive) : SpellAuraHolder.NoSlot;
        state.Auras.Add(holder);
        SitDownForStandingCancelsAura(holder);
        if (holder.Slot != SpellAuraHolder.NoSlot)
        {
            WriteAuraFields(holder, add: true);
            VisibleAuraSlotChanged?.Invoke(holder.Target, holder.Slot);
            SendAuraDuration(holder);
        }

        foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
        {
            AuraHandlers.GetValueOrDefault(aura.Type)?.Apply?.Invoke(this, holder, aura, true);
        }

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
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsQuestSettlementPending(target) && GetState(target.Guid) is { } state && ReferenceEquals(state.Unit, target))
        {
            foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Spell.Id == spellId && h.CasterGuid == caster).ToArray())
            {
                RemoveHolder(state, holder);
            }
        }
    }

    /// <summary>The <c>Auras</c> options (docs/areas/aura-engine.md); retail defaults until the world feature binds the configuration.</summary>
    public AuraOptions AuraOptions { get; set; } = new();

    /// <summary>The aura holders on a unit (world thread).</summary>
    public IReadOnlyList<SpellAuraHolder> GetAuras(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return GetState(unit.Guid) is { } state && ReferenceEquals(state.Unit, unit) ? state.AuraHolders : [];
    }

    public bool HasAura(Unit unit, uint spellId) => GetAuras(unit).Any(h => h.Spell.Id == spellId);

    /// <summary>
    /// Durations and periodic ticks (vmangos SpellAuraHolder::UpdateHolder → Aura::UpdateAura):
    /// each periodic aura ticks every amplitude ms; an expired holder is removed after its
    /// last tick.
    /// </summary>
    private void UpdateAuras(UnitSpellState state, uint diffMs)
    {
        if (state.Auras.Count == 0)
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.ToArray())
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            if (holder.AreaParent is { } parent && (parent.IsRemoved || !ReferenceEquals(parent.Target.Map, holder.Target.Map)))
            {
                // vmangos AreaAura::Update: the aura goes with its source or when the owner left the map.
                RemoveHolder(state, holder);
                continue;
            }

            if (IsQuestSettlementPending(holder.Target)
                || IsQuestSettlementPending(ResolveAuraCaster(holder)))
            {
                continue;
            }

            // vmangos Unit::SetStandState (Unit.cpp:9302-9310): standing up removes STANDING_CANCELS auras (food, drink).
            if ((holder.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0
                && holder.Target.StandState is StandState.Stand or StandState.Dead)
            {
                RemoveHolder(state, holder);
                continue;
            }

            bool runs = !holder.IsPermanent && holder.Duration > 0;
            if (!holder.IsPermanent)
            {
                holder.Duration = Math.Max(0, holder.Duration - (int)diffMs);
            }

            // vmangos SpellAuraHolder::Update: the per-second power cost (Health Funnel) comes right after the duration step.
            if (runs)
            {
                ChargePerSecondCost(holder, diffMs);
                if (holder.IsRemoved)
                {
                    continue;
                }
            }

            foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
            {
                if (!aura.IsPeriodic)
                {
                    continue;
                }

                // vmangos Aura::Update: at most one tick per update (Auras:PeriodicCatchUp restores the burst).
                int due = PeriodicTiming.Advance(aura, diffMs, AuraOptions.PeriodicCatchUp);
                for (int tick = 0; tick < due && !holder.IsRemoved; tick++)
                {
                    aura.TickCount++;
                    using ProcEventScope tickEvent = BeginProcEvent(); // one tick is one event: its procs, its damage and the kill it causes
                    AuraHandlers.GetValueOrDefault(aura.Type)?.Tick?.Invoke(this, holder, aura);
                }
            }

            if (!holder.IsRemoved && !holder.IsPermanent && holder.Duration == 0)
            {
                RemoveHolder(state, holder, AuraRemoveMode.Expire);
            }
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
            if (source.Amount != aura.Amount)
            {
                AuraHandler? handler = AuraHandlers.GetValueOrDefault(aura.Type);
                handler?.Apply?.Invoke(this, existing, aura, false);
                aura.Amount = source.Amount;
                aura.UnitAmount = source.UnitAmount;
                handler?.Apply?.Invoke(this, existing, aura, true);
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
                    AuraHandler? handler = AuraHandlers.GetValueOrDefault(aura.Type);
                    handler?.Apply?.Invoke(this, holder, aura, false);
                    aura.Amount = amount;
                    handler?.Apply?.Invoke(this, holder, aura, true);
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
        if (holder.Slot != SpellAuraHolder.NoSlot)
        {
            WriteAuraFields(holder, add: false);
            VisibleAuraSlotChanged?.Invoke(holder.Target, holder.Slot);
        }

        foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
        {
            AuraHandlers.GetValueOrDefault(aura.Type)?.Apply?.Invoke(this, holder, aura, false);
        }

        RaiseHolderRemoved(holder);

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

    /// <summary>AURAAPPLICATIONS holds stack count - 1 (vmangos UpdateAuraApplication).</summary>
    private static void WriteAuraApplications(SpellAuraHolder holder)
    {
        if (holder.Slot != SpellAuraHolder.NoSlot)
        {
            SetSlotByte(holder.Target, UpdateFields.UnitFieldAuraapplications, holder.Slot, (byte)(Math.Max(holder.StackAmount, (byte)1) - 1));
        }
    }

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
        Unit caster = ResolveAuraCaster(holder) ?? target;
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
        uint absorbed = AbsorbDamage(caster, target, holder.Spell.SchoolMask(), amount, holder.Spell);
        amount -= absorbed;
        if (ResolveAuraCaster(holder) is { } procCaster)
        {
            FirePeriodicDamageProcs(procCaster, target, holder.Spell, amount, original); // SpellAuras.cpp:5902-5917, before the damage
        }

        uint dealt = Damage.DealSpellDamage(caster, target, holder.Spell, amount, periodic: true, startsCombat: true, critical: false, durabilityLoss: true,
            reflected: holder.IsReflected && ReferenceEquals(caster, target));
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
