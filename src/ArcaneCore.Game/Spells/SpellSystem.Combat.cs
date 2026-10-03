using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>The result of one direct spell damage application.</summary>
public readonly record struct SpellDamageResult(uint Dealt, uint Resisted, bool Critical);

public sealed partial class SpellSystem
{
    /// <summary>SMSG_SPELLNONMELEEDAMAGELOG hit info SPELL_HIT_TYPE_CRIT (vmangos SpellHitType).</summary>
    public const uint SpellHitTypeCrit = 0x2;

    /// <summary>Hit, crit, resist and armor rules for spell landing (default <see cref="SpellCombatRules.Neutral"/>; the world daemon installs <see cref="VanillaSpellCombatRules"/>).</summary>
    public ISpellCombatRules CombatRules { get; set; } = SpellCombatRules.Neutral;

    /// <summary>Where SPELL_EFFECT_SUMMON lands (null until the creatures area installs one: the effect is a reported stub).</summary>
    public ISpellSummonSink? Summons { get; set; }

    /// <summary>
    /// The normalized weapon speed in seconds of a unit's hand for NORMALIZED_WEAPON_DMG (vmangos
    /// Unit::GetAPMultiplier: 2.4 one-hand, 3.3 two-hand, 1.7 dagger, 2.8 ranged). The item area
    /// knows the weapon; the default assumes a one-hand weapon (2.8 ranged).
    /// </summary>
    public Func<Unit, WeaponAttackType, float> NormalizedWeaponSpeed { get; set; }
        = static (_, attack) => attack == WeaponAttackType.RangedAttack ? 2.8f : 2.4f;

    /// <summary>Sum of the amounts of <paramref name="type"/> auras on <paramref name="unit"/> (vmangos Unit::GetTotalAuraModifier).</summary>
    public int GetTotalAuraModifier(Unit unit, AuraType type, Func<SpellAura, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        int total = 0;
        foreach (SpellAuraHolder holder in GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == type && (filter is null || filter(aura)))
                {
                    total += aura.Amount;
                }
            }
        }

        return total;
    }

    /// <summary>
    /// Direct spell damage (vmangos Spell::EffectSchoolDMG → SpellCaster::CalculateSpellDamage →
    /// Unit::DealSpellDamage): armor for physical spells, crit bonus, partial resist, the
    /// <see cref="IDamageSink"/>, pushback/interrupts on the victim, and the damage log.
    /// </summary>
    public SpellDamageResult DealDirectDamage(Unit caster, Unit target, SpellInfo spell, uint damage, bool allowCrit)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        uint amount = CombatRules.ApplyArmor(caster, target, spell, damage);
        bool crit = allowCrit && amount > 0 && CombatRules.RollCrit(this, caster, target, spell);
        if (crit)
        {
            amount = (uint)(amount * CombatRules.CritMultiplier(spell));
        }

        uint resisted = Math.Min(amount, CombatRules.RollPartialResist(this, caster, target, spell, amount));
        amount -= resisted;
        uint dealt = Damage.DealSpellDamage(caster, target, spell, amount, periodic: false);
        OnDamageTaken(target, caster, dealt, periodic: false);
        SendToSet(caster, WorldOpcode.SmsgSpellnonmeleedamagelog, SpellPackets.BuildSpellNonMeleeDamageLog(
            target.Guid, caster.Guid, spell.Id, dealt, spell.School, resisted: resisted, hitInfo: crit ? SpellHitTypeCrit : 0), includeSelf: true);
        return new SpellDamageResult(dealt, resisted, crit);
    }

    /// <summary>
    /// A unit took damage (spells call this themselves; the world daemon forwards melee damage
    /// from map combat). vmangos Unit::DealDamage, re-implemented: auras with
    /// AURA_INTERRUPT_FLAG_DAMAGE (and NON_PERIODIC_DAMAGE for direct hits) break; a cast in
    /// progress is interrupted (SPELL_INTERRUPT_FLAG_ABORT_ON_DMG) or pushed back
    /// (SPELL_INTERRUPT_FLAG_PUSH_BACK) by direct damage only ("DoTs can't interrupt or delay");
    /// a channel is delayed (CHANNEL_FLAG_DELAY) or interrupted (CHANNEL_FLAG_DAMAGE). Self damage is ignored.
    /// </summary>
    public void OnDamageTaken(Unit victim, Unit? attacker, uint damage, bool periodic)
    {
        ArgumentNullException.ThrowIfNull(victim);
        if (damage == 0 || ReferenceEquals(victim, attacker) || !victim.IsAlive || GetState(victim.Guid) is not { } state
            || !ReferenceEquals(state.Unit, victim))
        {
            return;
        }

        SpellAuraInterruptFlags breaking = SpellAuraInterruptFlags.Damage | (periodic ? 0 : SpellAuraInterruptFlags.NonPeriodicDamage);
        foreach (SpellAuraHolder holder in state.Auras.Where(h => (h.Spell.AuraInterruptFlags & breaking) != 0).ToArray())
        {
            RemoveHolder(state, holder);
        }

        if (state.CurrentCast is not { } cast)
        {
            return;
        }

        if (cast.State == SpellCastState.Preparing)
        {
            if (periodic || cast.IsTriggered || cast.Timer <= 0)
            {
                return;
            }

            if (cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.DamageCancels))
            {
                Cancel(cast);
            }
            else if (cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.DamagePushback))
            {
                Delay(cast);
            }
        }
        else if (cast.State == SpellCastState.Casting)
        {
            uint flags = (uint)cast.Spell.ChannelInterruptFlags;
            if ((flags & SpellChannelInterruptFlags.Delay) != 0)
            {
                DelayChannel(cast);
            }
            else if ((flags & (SpellChannelInterruptFlags.Damage | SpellChannelInterruptFlags.Damage2)) != 0)
            {
                Cancel(cast);
            }
        }
    }

    /// <summary>
    /// vmangos/cmangos-classic Spell::Delayed: push the cast bar back 500 ms, never beyond the full
    /// cast time; SMSG_SPELL_DELAYED to the caster's set.
    /// </summary>
    private void Delay(SpellCast cast)
    {
        int delay = SpellConstants.PushbackMs;
        if (cast.Timer + delay > cast.CastTime)
        {
            delay = cast.CastTime - cast.Timer;
            cast.Timer = cast.CastTime;
        }
        else
        {
            cast.Timer += delay;
        }

        cast.PushbackCount++;
        if (delay > 0)
        {
            SendToSet(cast.Caster, WorldOpcode.SmsgSpellDelayed, SpellPackets.BuildSpellDelayed(cast.Caster.Guid, (uint)delay), includeSelf: true);
        }
    }

    /// <summary>
    /// vmangos Spell::DelayedChannel: shorten the channel by 25% of its duration (at most what is
    /// left), shorten its auras on the caster and target by the same amount, MSG_CHANNEL_UPDATE.
    /// </summary>
    private void DelayChannel(SpellCast cast)
    {
        int delay = Math.Max(0, cast.Spell.GetDuration()) * SpellConstants.ChannelPushbackPercent / 100;
        if (cast.Timer <= delay)
        {
            delay = cast.Timer;
            cast.Timer = 0;
        }
        else
        {
            cast.Timer -= delay;
        }

        cast.PushbackCount++;
        Unit? target = ResolveUnitTarget(cast.Caster, cast.Targets);
        foreach (Unit unit in target is null || ReferenceEquals(target, cast.Caster) ? [cast.Caster] : new[] { cast.Caster, target })
        {
            if (IsQuestSettlementPending(unit) || GetState(unit.Guid) is not { } state
                || !ReferenceEquals(state.Unit, unit))
            {
                continue;
            }

            foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Spell.Id == cast.Spell.Id
                && h.CasterGuid == cast.Caster.Guid && ReferenceEquals(ResolveAuraCaster(h), cast.Caster) && !h.IsPermanent))
            {
                holder.Duration = Math.Max(0, holder.Duration - delay);
                SendAuraDuration(holder);
            }
        }

        if (cast.Caster is Player player)
        {
            player.Session.Send(WorldOpcode.MsgChannelUpdate, SpellPackets.BuildChannelUpdate((uint)cast.Timer));
        }
    }

    /// <summary>
    /// SPELL_EFFECT_ENVIRONMENTAL_DAMAGE (vmangos Spell::EffectEnvironmentalDMG): school damage
    /// that cannot crit.
    /// </summary>
    private void EffectEnvironmentalDamage(SpellEffectContext context)
    {
        if (context.Target.IsAlive && context.Value > 0)
        {
            DealDirectDamage(context.Caster, context.Target, context.Spell, (uint)context.Value, allowCrit: false);
        }
    }

    /// <summary>
    /// SPELL_EFFECT_HEALTH_LEECH (vmangos Spell::EffectHealthLeech): school damage, then the caster
    /// heals the damage dealt × EffectMultipleValue (1 when unset).
    /// </summary>
    private void EffectHealthLeech(SpellEffectContext context)
    {
        if (!context.Target.IsAlive || context.Value <= 0)
        {
            return;
        }

        SpellDamageResult result = DealDirectDamage(context.Caster, context.Target, context.Spell, ModifyDirect(SpellAmountStage.DirectDamage, context, (uint)context.Value), allowCrit: true);
        float multiple = context.Effect.MultipleValue > 0 ? context.Effect.MultipleValue : 1.0f;
        uint gain = (uint)(result.Dealt * multiple);
        if (gain > 0 && context.Caster.IsAlive)
        {
            uint healed = Damage.Heal(context.Caster, context.Caster, context.Spell, gain);
            SendToSet(context.Caster, WorldOpcode.SmsgSpellheallog,
                SpellPackets.BuildSpellHealLog(context.Caster.Guid, context.Caster.Guid, context.Spell.Id, healed), includeSelf: true);
        }
    }

    private static bool IsWeaponEffect(SpellEffectName effect)
        => effect is SpellEffectName.WeaponDamage or SpellEffectName.WeaponDamageNoschool
            or SpellEffectName.NormalizedWeaponDmg or SpellEffectName.WeaponPercentDamage;

    /// <summary>
    /// The weapon damage family (vmangos Spell::EffectWeaponDmg, re-implemented): all weapon
    /// effects selected for the target combine once, handled by the first selected one. Flat bonuses of
    /// WEAPON_DAMAGE / WEAPON_DAMAGE_NOSCHOOL / NORMALIZED_WEAPON_DMG add to the weapon roll, then
    /// WEAPON_PERCENT_DAMAGE scales the total; any normalized effect normalizes the roll's attack
    /// power part to <see cref="NormalizedWeaponSpeed"/>. Armor, crit (×2) and the damage log follow.
    /// </summary>
    private void EffectWeaponDamage(SpellEffectContext context)
    {
        IReadOnlyList<SpellEffectInfo> effects = context.Spell.Effects;
        for (int i = 0; i < context.EffectIndex; i++)
        {
            if ((context.EffectMask & (1 << i)) != 0 && IsWeaponEffect(effects[i].Effect))
            {
                return; // the first selected weapon effect handled this target's family
            }
        }

        if (!context.Target.IsAlive)
        {
            return;
        }

        int bonus = 0;
        float percent = 1.0f;
        bool normalized = false;
        for (int i = context.EffectIndex; i < effects.Count; i++)
        {
            SpellEffectInfo effect = effects[i];
            if ((context.EffectMask & (1 << i)) == 0 || !IsWeaponEffect(effect.Effect))
            {
                continue;
            }

            int value = i == context.EffectIndex ? context.Value : context.Spell.CalculateEffectValue(i, context.Caster.Level, Random);
            switch (effect.Effect)
            {
                case SpellEffectName.WeaponPercentDamage:
                    percent *= value / 100.0f;
                    break;
                case SpellEffectName.NormalizedWeaponDmg:
                    normalized = true;
                    bonus += value;
                    break;
                default:
                    bonus += value;
                    break;
            }
        }

        WeaponAttackType attack = context.Spell.DamageClass == SpellDamageClass.Ranged ? WeaponAttackType.RangedAttack : WeaponAttackType.BaseAttack;
        float weapon = WeaponDamageRoll(context.Caster, attack, normalized);
        float total = Math.Max(0f, (weapon + bonus) * percent);
        if (total < 1f)
        {
            return;
        }

        DealDirectDamage(context.Caster, context.Target, context.Spell, (uint)total, allowCrit: true);
    }

    /// <summary>
    /// A weapon roll (vmangos Unit::CalculateDamage): frand(min, max) of the hand's damage fields
    /// (max 0 → 5); normalized replaces the attack power contribution at the weapon's speed with
    /// the same attack power at the normalized speed (AP / 14 per second).
    /// </summary>
    public float WeaponDamageRoll(Unit unit, WeaponAttackType attack, bool normalized)
    {
        ArgumentNullException.ThrowIfNull(unit);
        (int minIndex, int maxIndex, int timeIndex, int apIndex) = attack switch
        {
            WeaponAttackType.OffAttack => (UpdateFields.UnitFieldMinoffhanddamage, UpdateFields.UnitFieldMaxoffhanddamage, UpdateFields.UnitFieldBaseattacktime + 1, UpdateFields.UnitFieldAttackPower),
            WeaponAttackType.RangedAttack => (UpdateFields.UnitFieldMinrangeddamage, UpdateFields.UnitFieldMaxrangeddamage, UpdateFields.UnitFieldRangedattacktime, UpdateFields.UnitFieldRangedAttackPower),
            _ => (UpdateFields.UnitFieldMindamage, UpdateFields.UnitFieldMaxdamage, UpdateFields.UnitFieldBaseattacktime, UpdateFields.UnitFieldAttackPower),
        };
        float min = Math.Max(0f, unit.GetFloat(minIndex));
        float max = Math.Max(0f, unit.GetFloat(maxIndex));
        if (min > max)
        {
            (min, max) = (max, min);
        }

        if (max == 0f)
        {
            max = CombatConstants.FallbackMaxDamage;
        }

        float roll = min + (float)(Random.NextDouble() * (max - min));
        if (!normalized)
        {
            return roll;
        }

        float attackPower = Math.Max(0, unit.GetInt32(apIndex) + (short)(unit.GetUInt32(apIndex + 1) & 0xFFFF) - (short)(unit.GetUInt32(apIndex + 1) >> 16));
        float speed = unit.GetUInt32(timeIndex) / 1000.0f;
        float normalizedSpeed = NormalizedWeaponSpeed(unit, attack);
        return Math.Max(0f, roll + ((normalizedSpeed - speed) * attackPower / 14.0f));
    }

    // SPELL_EFFECT_DISPEL (EffectDispel) and DispellableAuras live in Casters/Dispel/SpellSystem.Dispel.cs (casters lane, dispel-fidelity).

    /// <summary>
    /// SPELL_EFFECT_INTERRUPT_CAST (vmangos Spell::EffectInterruptCast): a cast with a cast bar or a
    /// channel whose PreventionType is SILENCE is interrupted, and the target cannot cast spells of
    /// that school for this spell's duration (vmangos Unit::ProhibitSpellSchool); a player is told
    /// with SMSG_SPELL_COOLDOWN for the interrupted spell.
    /// </summary>
    private void EffectInterruptCast(SpellEffectContext context)
    {
        Unit target = context.Target;
        if (GetState(target.Guid) is not { CurrentCast: { } cast } state || !ReferenceEquals(state.Unit, target))
        {
            return;
        }

        bool interruptible = cast.State == SpellCastState.Casting || (cast.State == SpellCastState.Preparing && cast.CastTime > 0);
        if (!interruptible || cast.Spell.PreventionType != SpellConstants.PreventionTypeSilence)
        {
            return;
        }

        int lockout = context.Spell.GetDuration();
        if (lockout > 0)
        {
            state.SchoolLockouts[cast.Spell.School] = NowMs + (uint)lockout;
            if (target is Player player)
            {
                player.Session.Send(WorldOpcode.SmsgSpellCooldown, SpellPackets.BuildSpellCooldown(player.Guid, [(cast.Spell.Id, (uint)lockout)]));
            }
        }

        Cancel(cast);
    }

    /// <summary>Whether <paramref name="unit"/> is locked out of <paramref name="school"/> by an interrupt.</summary>
    public bool IsSchoolLocked(Unit unit, SpellSchool school)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return GetState(unit.Guid) is { } state && state.SchoolLockouts.TryGetValue(school, out uint until) && until > NowMs;
    }

    /// <summary>
    /// SPELL_EFFECT_SUMMON (vmangos Spell::EffectSummon): creature EffectMiscValue at the destination
    /// (or the caster) for the spell duration, through <see cref="Summons"/>; without a sink the
    /// effect is reported as not implemented.
    /// </summary>
    private void EffectSummon(SpellEffectContext context)
    {
        uint entry = (uint)context.Effect.MiscValue;
        if (Summons is not { } sink || entry == 0)
        {
            ReportUnsupported("summon", entry, context.Spell.Id);
            return;
        }

        SpellCastTargets targets = context.Cast.Targets;
        (float x, float y, float z) = targets.HasDest ? (targets.Dest.X, targets.Dest.Y, targets.Dest.Z) : (context.Caster.X, context.Caster.Y, context.Caster.Z);
        if (sink.Summon(context.Caster, entry, x, y, z, context.Caster.Orientation, context.Spell.GetDuration()) is null)
        {
            ReportUnsupported("summon failed", entry, context.Spell.Id);
        }
    }
}
