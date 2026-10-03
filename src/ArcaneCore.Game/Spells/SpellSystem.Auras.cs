using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
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
    /// OBS_MOD_HEALTH/MANA, PERIODIC_TRIGGER_SPELL, DUMMY, MOD_ROOT and MOD_STUN.
    /// </summary>
    private static Dictionary<AuraType, AuraHandler> CreateAuraHandlers() => new()
    {
        [AuraType.Dummy] = new AuraHandler(null, null),
        [AuraType.PeriodicDamage] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicDamage(h, a)),
        [AuraType.PeriodicDamagePercent] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicDamage(h, a)),
        [AuraType.PeriodicHeal] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicHeal(h, a)),
        [AuraType.ObsModHealth] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicHeal(h, a)),
        [AuraType.PeriodicEnergize] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicEnergize(h, a)),
        [AuraType.ObsModMana] = new AuraHandler(null, static (s, h, a) => s.TickPeriodicEnergize(h, a)),
        [AuraType.PeriodicTriggerSpell] = new AuraHandler(null, static (s, h, a) => s.TickTriggerSpell(h, a)),
        [AuraType.ModRoot] = new AuraHandler(static (s, h, a, apply) => s.ApplyRoot(h, apply), null),
        [AuraType.ModStun] = new AuraHandler(static (s, h, a, apply) => s.ApplyStun(h, apply), null),
    };

    /// <summary>vmangos Spell::EffectApplyAura: add this effect's aura to the target's pending holder.</summary>
    private void EffectApplyAura(SpellEffectContext context)
    {
        if (!context.Target.IsAlive && !context.Spell.IsPassive)
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
            context.Cast.State == SpellCastState.Casting ? context.Cast.Timer : context.Spell.GetDuration());
        context.PendingHolder.SetAura(new SpellAura(context.EffectIndex, effect.AuraType, context.Value, effect.Amplitude, effect.MiscValue));
    }

    /// <summary>
    /// Put a holder on its target (vmangos Unit::AddSpellAuraHolder + SpellAuraHolder::_AddSpellAuraHolder):
    /// the same spell from the same caster stacks up to StackAmount and refreshes, otherwise
    /// replaces; a positive spell from another caster replaces too (simplified vmangos
    /// IsSingleFromSpell / stacking rules — docs/areas/spells.md). A slot is searched in the
    /// positive (0-31) or negative (32-47) range; the update fields and SMSG_UPDATE_AURA_DURATION follow.
    /// </summary>
    internal void AddAuraHolder(SpellAuraHolder holder)
    {
        UnitSpellState state = GetOrCreateState(holder.Target);
        SpellAuraHolder? existing = state.Auras.FirstOrDefault(h => h.Spell.Id == holder.Spell.Id
            && (h.CasterGuid == holder.CasterGuid || holder.IsPositive));
        if (existing is not null)
        {
            if (existing.CasterGuid == holder.CasterGuid
                && ReferenceEquals(existing.CasterOwner, holder.CasterOwner) && holder.Spell.StackAmount > 1)
            {
                existing.StackAmount = (byte)Math.Min(existing.StackAmount + 1, holder.Spell.StackAmount);
                existing.Duration = existing.MaxDuration = holder.MaxDuration;
                for (int i = 0; i < SpellConstants.MaxEffects; i++)
                {
                    if (existing.Auras[i] is { } aura && holder.Auras[i] is { } fresh)
                    {
                        aura.Amount = fresh.Amount * existing.StackAmount;
                    }
                }

                WriteAuraApplications(existing);
                SendAuraDuration(existing);
                return;
            }

            RemoveHolder(state, existing);
        }

        holder.Slot = holder.NeedsVisibleSlot ? FindFreeSlot(holder.Target, holder.IsPositive) : SpellAuraHolder.NoSlot;
        state.Auras.Add(holder);
        if (holder.Slot != SpellAuraHolder.NoSlot)
        {
            WriteAuraFields(holder, add: true);
            SendAuraDuration(holder);
        }

        foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
        {
            AuraHandlers.GetValueOrDefault(aura.Type)?.Apply?.Invoke(this, holder, aura, true);
        }
    }

    /// <summary>Remove every aura of <paramref name="spellId"/> from <paramref name="target"/> (vmangos Unit::RemoveAurasDueToSpell).</summary>
    public void RemoveAuras(Unit target, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (GetState(target.Guid) is { } state)
        {
            foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Spell.Id == spellId).ToArray())
            {
                RemoveHolder(state, holder);
            }
        }
    }

    /// <summary>vmangos Unit::RemoveAurasByCasterSpell.</summary>
    public void RemoveAurasByCaster(Unit target, uint spellId, ObjectGuid caster)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (GetState(target.Guid) is { } state)
        {
            foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Spell.Id == spellId && h.CasterGuid == caster).ToArray())
            {
                RemoveHolder(state, holder);
            }
        }
    }

    /// <summary>The aura holders on a unit (world thread).</summary>
    public IReadOnlyList<SpellAuraHolder> GetAuras(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return GetState(unit.Guid)?.AuraHolders ?? [];
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

            if (IsQuestSettlementPending(holder.Target)
                || IsQuestSettlementPending(ResolveAuraCaster(holder)))
            {
                continue;
            }

            if (!holder.IsPermanent)
            {
                holder.Duration = Math.Max(0, holder.Duration - (int)diffMs);
            }

            foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
            {
                if (!aura.IsPeriodic)
                {
                    continue;
                }

                aura.PeriodicTimer -= (int)diffMs;
                while (aura.PeriodicTimer <= 0 && !holder.IsRemoved)
                {
                    aura.PeriodicTimer += (int)aura.Amplitude;
                    aura.TickCount++;
                    AuraHandlers.GetValueOrDefault(aura.Type)?.Tick?.Invoke(this, holder, aura);
                }
            }

            if (!holder.IsRemoved && !holder.IsPermanent && holder.Duration == 0)
            {
                RemoveHolder(state, holder);
            }
        }
    }

    private void RemoveHolder(UnitSpellState state, SpellAuraHolder holder)
    {
        if (holder.IsRemoved)
        {
            return;
        }

        holder.IsRemoved = true;
        state.Auras.Remove(holder);
        if (holder.Slot != SpellAuraHolder.NoSlot)
        {
            WriteAuraFields(holder, add: false);
        }

        foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
        {
            AuraHandlers.GetValueOrDefault(aura.Type)?.Apply?.Invoke(this, holder, aura, false);
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
    /// target's maximum health for _PERCENT) goes to <see cref="IDamageSink"/>; SMSG_PERIODICAURALOG to the target's set.
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
        uint dealt = Damage.DealSpellDamage(caster, target, holder.Spell, amount, periodic: true);
        SendToSet(target, WorldOpcode.SmsgPeriodicauralog, SpellPackets.BuildPeriodicAuraLog(
            target.Guid, holder.CasterGuid, holder.Spell.Id, new PeriodicLogEntry(aura.Type, dealt, (uint)holder.Spell.School)), includeSelf: true);
    }

    /// <summary>
    /// vmangos Aura::PeriodicTick SPELL_AURA_PERIODIC_HEAL / OBS_MOD_HEALTH (the latter a percent
    /// of maximum health): healing through <see cref="IDamageSink.Heal"/>, logged with SMSG_PERIODICAURALOG.
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
        uint healed = Damage.Heal(caster, target, holder.Spell, amount);
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

        var power = (PowerType)powerType;
        uint amount = aura.Type == AuraType.ObsModMana
            ? (uint)((ulong)target.GetUInt32(UpdateFields.UnitFieldMaxpower1 + powerType) * (uint)aura.Amount / 100)
            : (uint)aura.Amount;
        SetPower(target, power, GetPower(target, power) + amount);
        SendToSet(target, WorldOpcode.SmsgPeriodicauralog, SpellPackets.BuildPeriodicAuraLog(
            target.Guid, holder.CasterGuid, holder.Spell.Id, new PeriodicLogEntry(aura.Type, amount, (uint)powerType)), includeSelf: true);
    }

    /// <summary>vmangos Aura::PeriodicTick SPELL_AURA_PERIODIC_TRIGGER_SPELL: the caster casts EffectTriggerSpell at the target, triggered.</summary>
    private void TickTriggerSpell(SpellAuraHolder holder, SpellAura aura)
    {
        uint triggerSpell = holder.Spell.Effects[aura.EffectIndex].TriggerSpell;
        Unit caster = ResolveAuraCaster(holder) ?? holder.Target;
        if (Store.Get(triggerSpell) is not null)
        {
            CastSpell(caster, triggerSpell, SpellCastTargets.ForUnit(holder.Target.Guid), triggered: true);
        }
    }

    /// <summary>
    /// vmangos Aura::HandleAuraModRoot → Unit::SetRooted: players get SMSG_FORCE_MOVE_ROOT/UNROOT
    /// through <see cref="Player.SetRooted"/>; the root lifts when the last root/stun aura goes.
    /// </summary>
    private void ApplyRoot(SpellAuraHolder holder, bool apply)
    {
        if (holder.Target is not Player player)
        {
            return;
        }

        if (apply)
        {
            player.SetRooted(true);
        }
        else if (!GetAuras(player).Any(h => !h.IsRemoved && (h.HasAura(AuraType.ModRoot) || h.HasAura(AuraType.ModStun))))
        {
            player.SetRooted(false);
        }
    }

    /// <summary>
    /// vmangos Aura::HandleAuraModStun → Unit::SetStunned (subset): UNIT_FLAG_STUNNED, rooted,
    /// and the cast in progress is interrupted; lifted with the last stun aura.
    /// </summary>
    private void ApplyStun(SpellAuraHolder holder, bool apply)
    {
        Unit target = holder.Target;
        if (apply)
        {
            target.UnitFlags |= UnitFlags.Stunned;
            if (GetState(target.Guid)?.CurrentCast is { } cast)
            {
                Cancel(cast);
            }
        }
        else if (!GetAuras(target).Any(h => !h.IsRemoved && h.HasAura(AuraType.ModStun)))
        {
            target.UnitFlags &= ~UnitFlags.Stunned;
        }

        ApplyRoot(holder, apply);
    }
}
