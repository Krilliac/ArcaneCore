using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>The result of one direct spell damage application.</summary>
public readonly record struct SpellDamageResult(uint Dealt, uint Resisted, bool Critical, uint Absorbed = 0);

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

            foreach (SpellAura? aura in holder.AuraSpan)
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
        // Capture this before the sink: a lethal queued spell can stop combat and clear Victim.
        bool queuedMeleeSpell = spell.IsNextMeleeSwing && ReferenceEquals(caster.Combat.Victim, target);
        uint amount = CombatRules.ApplyArmor(caster, target, spell, damage);
        // A game object never crits (SpellCaster::IsSpellCrit, SpellCaster.h:320): neither does the unit standing in for one.
        bool crit = allowCrit && amount > 0 && !IsGameObjectStandIn(caster) && CombatRules.RollCrit(this, caster, target, spell);
        if (crit)
        {
            // Exact vmangos amount (talent bonus, creature-type multiplier) when the rules offer it; else the plain multiplier.
            amount = CombatRules is Rules.ISpellCritAmounts exact
                ? exact.CriticalDamage(this, caster, target, spell, amount)
                : (uint)(amount * CombatRules.CritMultiplier(spell));
        }

        uint resisted = ApplyResist(caster, target, spell, ref amount, periodic: false);
        uint original = amount;
        uint absorbed = AbsorbDamage(caster, target, spell.SchoolMask(), amount, spell); // shields, mana shield, split (Unit.cpp:1920-2200)
        amount -= absorbed;
        SpellCast? packetCast = _outcome?.Cast;
        // vmangos Spell::DoAllEffectOnTarget (Spell.cpp:1444-1456): "Damage is done after procs so it can trigger auras on the victim that affect
        // the caster in case of killing blow". The cast's own hit on this target procs once, here, before the sink.
        bool reflected = false;
        if (_outcome is { } hit && ReferenceEquals(hit.Target, target) && ReferenceEquals(hit.Cast.Caster, caster) && hit.Cast.Spell.Id == spell.Id)
        {
            reflected = hit.Reflected && ReferenceEquals(caster, target);
            if (!hit.ProcsDone)
            {
                hit.ProcsDone = true;
                // A reflected hit procs with PROC_EX_REFLECT plus the hit bits (CreateProcExtendMask falls through from REFLECT, Unit.cpp:8811-8828).
                FireSpellHitProcs(hit.Cast, target, hit.Reflected ? SpellMissInfo.Reflect : SpellMissInfo.None, amount, original + resisted, crit, absorbed,
                    hit.EffectMask, hit.Reflected);
            }
        }

        uint dealt = Damage.DealSpellDamage(caster, target, spell, amount, periodic: false, startsCombat: StartsCombat(caster, target), critical: crit,
            durabilityLoss: true, reflected: reflected);
        OnDamageTaken(target, caster, dealt, periodic: false, absorbed, spell.Id);
        RecordDamage(caster, target, spell, dealt, crit);
        uint spellHitInfo = crit ? SpellHitTypeCrit : 0;
        if (packetCast is not null && _outcome is { } outcome && outcome.MeleeSpellPacketEligible
            && ReferenceEquals(outcome.Cast, packetCast) && ReferenceEquals(outcome.Target, target)
            && ReferenceEquals(packetCast.Caster, caster) && packetCast.Spell.Id == spell.Id)
        {
            outcome.MeleeSpellDamage.Add(new MeleeSpellDamageComponent(spell.SchoolMask(), dealt, absorbed, resisted, crit));
            outcome.DeferredNonMeleeLogs.Add(new DeferredNonMeleeDamageLog(target.Guid, caster.Guid, spell.Id, dealt, spell.School, absorbed, resisted, spellHitInfo));
        }
        else
        {
            SendNextMeleeSpellAttackerStateUpdate(caster, target, spell, queuedMeleeSpell, dealt, absorbed, resisted, crit, packetCast);
            SendToSet(caster, WorldOpcode.SmsgSpellnonmeleedamagelog, SpellPackets.BuildSpellNonMeleeDamageLog(
                target.Guid, caster.Guid, spell.Id, dealt, spell.School, absorbed: absorbed, resisted: resisted, hitInfo: spellHitInfo), includeSelf: true);
        }
        return new SpellDamageResult(dealt, resisted, crit, absorbed);
    }

    /// <summary>
    /// The resist step of <see cref="DealDirectDamage"/> and damage over time: a partial resist comes off
    /// <paramref name="amount"/> and is returned; a vulnerability (negative resist) adds its extra damage to
    /// <paramref name="amount"/> before absorbs see it (Unit.cpp:1948-1953, 2229-2232) and returns 0.
    /// </summary>
    internal uint ApplyResist(Unit caster, Unit target, SpellInfo spell, ref uint amount, bool periodic)
    {
        int roll = CombatRules is ISpellResistRoll signed
            ? signed.RollResist(this, caster, target, spell, amount, periodic)
            : (int)Math.Min(CombatRules.RollPartialResist(this, caster, target, spell, amount), int.MaxValue);
        if (roll < 0)
        {
            amount += (uint)-roll;
            return 0;
        }

        uint resisted = Math.Min(amount, (uint)roll);
        amount -= resisted;
        return resisted;
    }

    /// <summary>
    /// A unit took damage (spells call this themselves; the world daemon forwards melee damage
    /// from map combat). vmangos Unit::DealDamage, re-implemented: auras with
    /// AURA_INTERRUPT_FLAG_DAMAGE (and NON_PERIODIC_DAMAGE for direct hits) break; a cast in
    /// progress is interrupted (SPELL_INTERRUPT_FLAG_ABORT_ON_DMG) or pushed back
    /// (SPELL_INTERRUPT_FLAG_PUSH_BACK) by direct damage only ("DoTs can't interrupt or delay");
    /// a channel is delayed (CHANNEL_FLAG_DELAY) or interrupted (CHANNEL_FLAG_DAMAGE). Self damage breaks auras (build 5875, vmangos Unit.cpp:660-670, SKIP_STEALTH is false above 1.6.1) but never pushes back or interrupts a cast.
    /// When nothing got through but <paramref name="absorbed"/> is positive, the damage == 0 branch applies
    /// (Unit.cpp:733-746): damage-cancels auras still break and a player's damage-cancels cast is interrupted
    /// (not by damage over time), but nothing is pushed back or delayed.
    /// </summary>
    public void OnDamageTaken(Unit victim, Unit? attacker, uint damage, bool periodic, uint absorbed = 0, uint sourceSpellId = 0)
    {
        ArgumentNullException.ThrowIfNull(victim);
        if ((damage == 0 && absorbed == 0) || !victim.IsAlive || GetState(victim.Guid) is not { } state
            || !ReferenceEquals(state.Unit, victim))
        {
            return;
        }

        // vmangos Unit.cpp:735-745 / 895-906: RemoveAurasWithInterruptFlags(DAMAGE_CANCELS, damaging spell, checkProcFlags). The aura
        // of the spell that did the damage stays (a DoT with the flag does not cancel itself) and so does any aura whose spell
        // has procFlags (Wyvern Sting): the proc engine ends those.
        if (damage == 0)
        {
            BreakAurasOnDamage(victim, sourceSpellId);

            if (!periodic && victim is Player && state.CurrentCast is { State: SpellCastState.Preparing } preparing
                && preparing.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.DamageCancels))
            {
                Cancel(preparing); // "interrupt spells like trying to mount even through absorb shields"
            }

            return;
        }

        BreakAurasOnDamage(victim, sourceSpellId);

        // The cast or channel in progress: pushback, delay and damage cancels (retail rules in SpellSystem.Pushback.cs).
        // Self damage never pushes back or interrupts.
        if (!ReferenceEquals(victim, attacker))
        {
            ApplyDamageToCurrentCast(victim, state, periodic);
        }
    }

    /// <summary>
    /// The damage break of <see cref="OnDamageTaken"/> (vmangos Unit.cpp:735-745, 895-906): every procFlags aura is skipped
    /// (<see cref="AuraOptions.ProcEngineBreaksDamageAuras"/>, default on) because the proc engine ends those (charges, break chances,
    /// <see cref="AuraOptions.DamageProcCancelsAura"/>). "Prevent item procs from breaking the CC that caused them" (Unit.cpp:896-905): when the
    /// damage comes from a spell a PROC_TRIGGER_SPELL aura is casting, the spell whose hit made that aura proc is the exception instead.
    /// </summary>
    private void BreakAurasOnDamage(Unit victim, uint sourceSpellId)
    {
        uint except = sourceSpellId != 0 && _procCastParent is { } parent && parent.SpellId == sourceSpellId ? parent.ParentSpellId : sourceSpellId;
        RemoveAurasWithInterruptFlags(victim, (uint)SpellAuraInterruptFlags.Damage, except, checkProcFlags: AuraOptions.ProcEngineBreaksDamageAuras);
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
        // SPELLMOD_MULTIPLE_VALUE on the leech multiple (vmangos EffectHealthLeech, SpellEffects.cpp:1866-1869).
        float multiple = ModFloat(context.Caster, context.Spell, SpellModOp.MultipleValue, context.Effect.MultipleValue > 0 ? context.Effect.MultipleValue : 1.0f);
        uint gain = (uint)(result.Dealt * multiple);
        if (gain > 0 && context.Caster.IsAlive)
        {
            uint healed = Damage.Heal(context.Caster, context.Caster, context.Spell, gain, IDamageSink.HealingOrigin.NoThreat);
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

            int value = i == context.EffectIndex ? context.Value : context.Spell.CalculateEffectValue(i, CasterLevelOf(context.Caster), Random);
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

        // ranged (autorepeat lane): SpellEntry::GetWeaponAttackType (SpellEntry.cpp:434-455): a ranged class spell, and any other class that
        // carries the auto-repeat attribute (wand Shoot, damage class magic), swings the RANGED weapon.
        WeaponAttackType attack = RangedSpellFacts.UsesRangedWeapon(context.Spell) ? WeaponAttackType.RangedAttack : WeaponAttackType.BaseAttack;
        float weapon = WeaponDamageRoll(context.Caster, attack, normalized);
        float total = Math.Max(0f, (weapon + bonus) * percent);

        // vmangos MeleeDamageBonusDone (SpellCaster.cpp:1295-1455) for a weapon-based spell: creature type and attack power versus (auras 59,
        // 102/131, 165/127 on the victim) and damage done versus (168); then the DAMAGE spell mod on the done amount, before armor and crit
        // (:1446); then MeleeDamageBonusTaken (Unit.cpp:5676-5749: auras 125/113, 14, 87, 126/114).
        SpellInfo damageSpell = WithWandSchool(context.Spell, context.Caster, attack);
        total = MeleeDamageBonus.Done(this, context.Caster, context.Target, total, attack, normalized, context.Spell);
        total = ModFloat(context.Caster, context.Spell, SpellModOp.Damage, total);
        total = MeleeDamageBonus.Taken(this, context.Caster, context.Target, total, attack, damageSpell.SchoolMask(), context.Spell);
        if (total < 1f)
        {
            return;
        }

        DealDirectDamage(context.Caster, context.Target, damageSpell, (uint)total, allowCrit: true);
    }

    /// <summary>
    /// A ranged attack of a priest, mage or warlock deals the school of the wielded ranged weapon's first damage entry (a fire wand
    /// burns, an arcane wand is arcane): vmangos Spell::Spell "wand case" (Spell.cpp:68-71) overrides the spell's school mask with
    /// <c>GetWeaponDamageSchool(RANGED_ATTACK)</c> for those classes. Done here for the damage only (absorb, resist and the damage log);
    /// the hit roll keeps the spell's own school.
    /// </summary>
    private static SpellInfo WithWandSchool(SpellInfo spell, Unit caster, WeaponAttackType attack)
    {
        if (attack != WeaponAttackType.RangedAttack || EquippedWandDamageSchool(caster) is not { } school)
        {
            return spell;
        }

        return school == spell.School ? spell : spell with { School = school };
    }

    /// <summary>The item school used for wand Shoot damage and Viscidus frost-hit counting.</summary>
    internal static SpellSchool? EquippedWandDamageSchool(Unit caster)
    {
        if (caster is not Player player || !RangedSpellFacts.IsWandUser(player.Class)
            || PlayerAmmo.RangedWeapon(player, nonBroken: true) is not { } weapon || weapon.Template.Damages.Count == 0)
            return null;

        return (SpellSchool)weapon.Template.Damages[0].School;
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

        // vmangos Unit::GetTotalAttackPowerValue (Unit.cpp:8037): AP + positive mods + negative mods (the halves of *_MODS are int16, the negative one is <= 0).
        float attackPower = Math.Max(0, unit.GetInt32(apIndex) + (short)(unit.GetUInt32(apIndex + 1) & 0xFFFF) + (short)(unit.GetUInt32(apIndex + 1) >> 16));
        // ranged (autorepeat lane): the UNHASTED speed (Unit::GetAttackTime), so haste does not change damage per hit (SpellCaster.cpp:1826-1833).
        float speed = unit.Combat.GetUnhastedTime(attack) / 1000.0f;
        float normalizedSpeed = NormalizedWeaponSpeed(unit, attack);
        return Math.Max(0f, roll + ((normalizedSpeed - speed) * attackPower / 14.0f));
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
        if (sink.Summon(context.Caster, new SpellSummonRequest(context.Spell.Id, entry, x, y, z, context.Caster.Orientation, context.Spell.GetDuration())) is null)
        {
            ReportUnsupported("summon failed", entry, context.Spell.Id);
        }
    }
}
