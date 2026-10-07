using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells.Procs;

/// <summary>Spell attribute bits the proc engine reads (vmangos SpellDefines.h:830-975).</summary>
public static class ProcAttributes
{
    /// <summary>SPELL_ATTR_PROC_FAILURE_BURNS_CHARGE (Attributes bit 0).</summary>
    public const uint ProcFailureBurnsCharge = 0x00000001;

    /// <summary>SPELL_ATTR_EX2_AUTO_REPEAT (AttributesEx2 bit 5).</summary>
    public const uint Ex2AutoRepeat = 0x00000020;

    /// <summary>SPELL_ATTR_EX2_PROC_COOLDOWN_ON_FAILURE (AttributesEx2 bit 23).</summary>
    public const uint Ex2ProcCooldownOnFailure = 0x00800000;

    /// <summary>SPELL_ATTR_EX3_NO_PROC_EQUIP_REQUIREMENT (AttributesEx3 bit 1).</summary>
    public const uint Ex3NoProcEquipRequirement = 0x00000002;

    /// <summary>SPELL_ATTR_EX3_NOT_A_PROC (AttributesEx3 bit 9): an aura-triggered cast that still counts as an ordinary one.</summary>
    public const uint Ex3NotAProc = 0x00000200;

    /// <summary>SPELL_ATTR_EX3_SUPPRESS_CASTER_PROCS (AttributesEx3 bit 16).</summary>
    public const uint Ex3SuppressCasterProcs = 0x00010000;

    /// <summary>SPELL_ATTR_EX3_SUPPRESS_TARGET_PROCS (AttributesEx3 bit 17).</summary>
    public const uint Ex3SuppressTargetProcs = 0x00020000;

    /// <summary>SPELL_ATTR_EX3_ONLY_PROC_OUTDOORS (AttributesEx3 bit 21).</summary>
    public const uint Ex3OnlyProcOutdoors = 0x00200000;

    /// <summary>SPELL_ATTR_EX3_TREAT_AS_PERIODIC (AttributesEx3 bit 25).</summary>
    public const uint Ex3TreatAsPeriodic = 0x02000000;

    /// <summary>SPELL_ATTR_EX3_CAN_PROC_FROM_PROCS (AttributesEx3 bit 26).</summary>
    public const uint Ex3CanProcFromProcs = 0x04000000;

    /// <summary>SPELL_ATTR_EX3_ONLY_PROC_ON_CASTER (AttributesEx3 bit 27).</summary>
    public const uint Ex3OnlyProcOnCaster = 0x08000000;

    /// <summary>ITEM_CLASS_WEAPON / ITEM_CLASS_ARMOR for the equipment requirement of a proc aura.</summary>
    public const int ItemClassWeapon = 2;

    public const int ItemClassArmor = 4;
}

/// <summary>
/// The proc flags and outcomes the hit paths hand the engine: vmangos <c>Unit::CalculateMeleeDamage</c> (Unit.cpp:1320-1560) for white swings,
/// <c>Spell::PrepareMasksForProcSystem</c> (Spell.cpp:627-815) and <c>CreateProcExtendMask</c> (Unit.cpp:8776-8832) for spells.
/// </summary>
public static class ProcFlagRules
{
    /// <summary>NEGATIVE_TRIGGER_MASK (SpellDefines.h:1093-1097).</summary>
    public const ProcFlags NegativeTriggerMask = ProcFlags.DealMeleeSwing | ProcFlags.TakeMeleeSwing | ProcFlags.DealMeleeAbility | ProcFlags.TakeMeleeAbility
        | ProcFlags.DealRangedAttack | ProcFlags.TakeRangedAttack | ProcFlags.DealRangedAbility | ProcFlags.TakeRangedAbility
        | ProcFlags.DealHarmfulAbility | ProcFlags.TakeHarmfulAbility | ProcFlags.DealHarmfulSpell | ProcFlags.TakeHarmfulSpell;

    /// <summary>PROX_EX_NO_DAMAGE_MASK: the outcomes that carry no damage (miss, resist, dodge, parry, block, evade, immune, deflect, absorb, reflect).</summary>
    public const ProcFlagsEx NoDamageMask = ProcFlagsEx.Miss | ProcFlagsEx.Resist | ProcFlagsEx.Dodge | ProcFlagsEx.Parry | ProcFlagsEx.Block
        | ProcFlagsEx.Evade | ProcFlagsEx.Immune | ProcFlagsEx.Deflect | ProcFlagsEx.Absorb | ProcFlagsEx.Reflect;

    /// <summary>
    /// A white swing's event (vmangos CalculateMeleeDamage): DEAL_MELEE_SWING plus the hand bit for the attacker, TAKE_MELEE_SWING for the victim (+
    /// TAKEN_ANY_DAMAGE when damage reaches it, :1514-1516), the outcome bit and ABSORB when a shield took some (:1550-1551). A partial block also
    /// counts as a hit (:1472-1475). The amount is the damage that landed, the original amount adds back the absorb (Unit.cpp:2263).
    /// </summary>
    public static ProcEvent ForMeleeSwing(MeleeDamageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ProcFlags attacker = info.AttackType switch
        {
            WeaponAttackType.OffAttack => ProcFlags.DealMeleeSwing | ProcFlags.OffHandWeaponSwing,
            WeaponAttackType.RangedAttack => ProcFlags.DealRangedAttack,
            _ => ProcFlags.DealMeleeSwing | ProcFlags.MainHandWeaponSwing,
        };
        ProcFlags victim = info.AttackType == WeaponAttackType.RangedAttack ? ProcFlags.TakeRangedAttack : ProcFlags.TakeMeleeSwing;
        ProcFlagsEx extra = info.Outcome switch
        {
            MeleeHitOutcome.Evade => ProcFlagsEx.Evade,
            MeleeHitOutcome.Miss => ProcFlagsEx.Miss,
            MeleeHitOutcome.Crit => ProcFlagsEx.CriticalHit,
            MeleeHitOutcome.Parry => ProcFlagsEx.Parry,
            MeleeHitOutcome.Dodge => ProcFlagsEx.Dodge,
            MeleeHitOutcome.Block => info.TargetState == VictimState.Blocks ? ProcFlagsEx.Block : ProcFlagsEx.Block | ProcFlagsEx.NormalHit,
            MeleeHitOutcome.Resist => ProcFlagsEx.Resist,
            _ => ProcFlagsEx.NormalHit, // normal, glancing, crushing
        };

        if (info.TotalDamage + info.Absorbed > 0)
        {
            victim |= ProcFlags.TakenAnyDamage;
        }

        if (info.Absorbed > 0)
        {
            extra |= ProcFlagsEx.Absorb;
        }

        return new ProcEvent
        {
            Victim = info.Target,
            AttackerFlags = attacker,
            VictimFlags = victim,
            Extra = extra,
            Amount = info.TotalDamage,
            OriginalAmount = info.TotalDamage + info.Absorbed,
            AttackType = info.AttackType,
        };
    }

    /// <summary>
    /// vmangos <c>Spell::m_canTrigger</c> (Spell.cpp:627-697): a cast from an item procs only when it is negative; a plain cast or one from a
    /// TRIGGER_SPELL effect procs; a cast made by an aura procs only with NOT_A_PROC, or for the periodic-trigger families listed below.
    /// </summary>
    public static bool CanTrigger(SpellCast cast)
    {
        ArgumentNullException.ThrowIfNull(cast);
        SpellInfo spell = cast.Spell;
        if ((spell.AttributesEx3 & ProcAttributes.Ex3SuppressCasterProcs) != 0 && (spell.AttributesEx3 & ProcAttributes.Ex3SuppressTargetProcs) != 0)
        {
            return false;
        }

        bool canTrigger;
        if (cast.CastItem is not null)
        {
            canTrigger = !spell.IsPositive;
        }
        else if (!cast.IsTriggered || !cast.IsTriggeredByAura)
        {
            canTrigger = true;
        }
        else
        {
            canTrigger = (spell.AttributesEx3 & ProcAttributes.Ex3NotAProc) != 0;
        }

        return canTrigger || IsPeriodicTriggerThatProcs(spell);
    }

    // Spell.cpp:661-697 (build 5875 branches); bit numbers from SpellClassMask.h.
    private static bool IsPeriodicTriggerThatProcs(SpellInfo spell) => spell.SpellFamilyName switch
    {
        3 => (spell.SpellFamilyFlags & 0x0000000000200080UL) != 0,      // SPELLFAMILY_MAGE: Arcane Missiles / Blizzard triggers
        5 => (spell.SpellFamilyFlags & ((1UL << 5) | (1UL << 6))) != 0,  // SPELLFAMILY_WARLOCK: CF_WARLOCK_RAIN_OF_FIRE (5), CF_WARLOCK_HELLFIRE (6)
        9 => (spell.SpellFamilyFlags & ((1UL << 2) | (1UL << 4))) != 0,  // SPELLFAMILY_HUNTER: CF_HUNTER_FIRE_TRAP_EFFECTS (2), CF_HUNTER_FROST_TRAP_AURA (4)
        10 => spell.Id == 20424                                          // SPELLFAMILY_PALADIN: Seal of Command, Seal of Righteousness, Holy Shock (21), Eye for an Eye
            || (spell.SpellFamilyFlags == 0 && spell.SpellIconId == 25) || (spell.SpellFamilyFlags & (1UL << 21)) != 0 || spell.Id == 25997,
        6 => (spell.SpellFamilyFlags & ((1UL << 19) | (1UL << 25))) != 0, // SPELLFAMILY_PRIEST: CF_PRIEST_TOUCH_OF_WEAKNESS (19), CF_PRIEST_DEVOURING_PLAGUE (25)
        _ => false,
    };

    /// <summary>
    /// vmangos <c>Spell::PrepareMasksForProcSystem</c> base flags (Spell.cpp:699-797): melee abilities (+ the hand bit, + the swing bits for an
    /// on-next-swing spell), auto-repeat ranged attacks, ranged abilities (none for Blind and Expose Weakness), and for every other damage class
    /// helpful ability/spell (heals) or harmful periodic/spell/ability; hunter trap effects add ON_TRAP_ACTIVATION.
    /// </summary>
    public static (ProcFlags Attacker, ProcFlags Victim) SpellFlags(SpellInfo spell, WeaponAttackType attackType)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ProcFlags attacker;
        ProcFlags victim;
        switch (spell.DamageClass)
        {
            case SpellDamageClass.Melee:
                attacker = ProcFlags.DealMeleeAbility | (attackType == WeaponAttackType.OffAttack ? ProcFlags.OffHandWeaponSwing : ProcFlags.MainHandWeaponSwing);
                victim = ProcFlags.TakeMeleeAbility;
                if (spell.IsNextMeleeSwing)
                {
                    attacker |= ProcFlags.DealMeleeSwing;
                    victim |= ProcFlags.TakeMeleeSwing;
                }

                break;
            case SpellDamageClass.Ranged:
                if (((uint)spell.AttributesEx2 & ProcAttributes.Ex2AutoRepeat) != 0)
                {
                    attacker = ProcFlags.DealRangedAttack;
                    victim = ProcFlags.TakeRangedAttack;
                }
                else if (spell.Id is 2094 or 23577)
                {
                    attacker = ProcFlags.None; // Blind, Expose Weakness
                    victim = ProcFlags.None;
                }
                else
                {
                    attacker = ProcFlags.DealRangedAbility;
                    victim = ProcFlags.TakeRangedAbility;
                }

                break;
            default:
                if (spell.IsPositive)
                {
                    bool heal = IsHealSpell(spell);
                    attacker = heal ? ProcFlags.DealHelpfulSpell : ProcFlags.DealHelpfulAbility;
                    victim = heal ? ProcFlags.TakeHelpfulSpell : ProcFlags.TakeHelpfulAbility;
                }
                else if (((uint)spell.AttributesEx2 & ProcAttributes.Ex2AutoRepeat) != 0)
                {
                    attacker = ProcFlags.DealRangedAttack; // wands
                    victim = ProcFlags.TakeRangedAttack;
                }
                else if ((spell.AttributesEx3 & ProcAttributes.Ex3TreatAsPeriodic) != 0)
                {
                    attacker = ProcFlags.DealHarmfulPeriodic;
                    victim = ProcFlags.TakeHarmfulPeriodic;
                }
                else if (spell.DamageClass == SpellDamageClass.Magic)
                {
                    attacker = ProcFlags.DealHarmfulSpell;
                    victim = ProcFlags.TakeHarmfulSpell;
                }
                else
                {
                    attacker = ProcFlags.DealHarmfulAbility;
                    victim = ProcFlags.TakeHarmfulAbility;
                }

                // Hunter trap spells (Entrapment): CF_HUNTER_FIRE_TRAP_EFFECTS, CF_HUNTER_FREEZING_TRAP_EFFECT, CF_HUNTER_FROST_TRAP_AURA.
                if (spell.SpellFamilyName == 9 && (spell.SpellFamilyFlags & ((1UL << 2) | (1UL << 3) | (1UL << 4))) != 0)
                {
                    attacker |= ProcFlags.OnTrapActivation;
                }

                break;
        }

        return (attacker, victim);
    }

    /// <summary>
    /// The effects that count as negative for the proc masks (vmangos <c>m_negativeEffectMask</c>, Spell.cpp:801-812): the non-positive effects plus
    /// a SCHOOL_DAMAGE effect aimed at the caster.
    /// </summary>
    public static int NegativeEffectMask(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        int mask = 0;
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (effect.Effect == SpellEffectName.None)
            {
                continue;
            }

            if (!spell.IsPositiveEffect(i) || (effect.Effect == SpellEffectName.SchoolDamage && effect.TargetA == SpellImplicitTarget.UnitCaster))
            {
                mask |= 1 << i;
            }
        }

        return mask;
    }

    /// <summary>vmangos <c>CreateProcExtendMask</c> (Unit.cpp:8776-8832): the miss reason, or on a hit block/absorb and crit or normal hit.</summary>
    public static ProcFlagsEx ExtendMask(SpellMissInfo miss, bool hasDamageInfo, bool critical = false, uint absorbed = 0, uint blocked = 0)
    {
        ProcFlagsEx extra = miss switch
        {
            SpellMissInfo.Miss => ProcFlagsEx.Miss,
            SpellMissInfo.Resist => ProcFlagsEx.Resist,
            SpellMissInfo.Dodge => ProcFlagsEx.Dodge,
            SpellMissInfo.Parry => ProcFlagsEx.Parry,
            SpellMissInfo.Block => ProcFlagsEx.Block,
            SpellMissInfo.Evade => ProcFlagsEx.Evade,
            SpellMissInfo.Immune or SpellMissInfo.Immune2 => ProcFlagsEx.Immune,
            SpellMissInfo.Deflect => ProcFlagsEx.Deflect,
            SpellMissInfo.Absorb => ProcFlagsEx.Absorb,
            SpellMissInfo.Reflect => ProcFlagsEx.Reflect,
            _ => ProcFlagsEx.None,
        };

        if (miss is SpellMissInfo.None or SpellMissInfo.Reflect && hasDamageInfo)
        {
            if (blocked > 0)
            {
                extra |= ProcFlagsEx.Block;
            }

            if (absorbed > 0)
            {
                extra |= ProcFlagsEx.Absorb;
            }

            extra |= critical ? ProcFlagsEx.CriticalHit : ProcFlagsEx.NormalHit;
        }

        return extra;
    }

    /// <summary>vmangos <c>SpellInternal::IsHealSpell</c> (SpellMgr.cpp:3197-3231): a heal effect or a periodic heal aura (Holy Light / Flash of Light's family bits too).</summary>
    public static bool IsHealSpell(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.SpellFamilyName == 10 && (spell.SpellFamilyFlags & ((1UL << 30) | (1UL << 31))) != 0)
        {
            return true; // CF_PALADIN_FLASH_OF_LIGHT2, CF_PALADIN_HOLY_LIGHT2
        }

        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect is SpellEffectName.Heal or SpellEffectName.HealMaxHealth)
            {
                return true;
            }

            if (effect.Effect is SpellEffectName.ApplyAura or SpellEffectName.ApplyAreaAuraParty or SpellEffectName.ApplyAreaAuraFriend
                    or SpellEffectName.ApplyAreaAuraRaid or SpellEffectName.ApplyAreaAuraPet
                && effect.AuraType is AuraType.PeriodicHeal or AuraType.ObsModHealth)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos <c>SpellInternal::IsDirectDamageSpell</c> (SpellMgr.cpp:3233-3242).</summary>
    public static bool IsDirectDamageSpell(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.Effects.Any(e => e.Effect is SpellEffectName.SchoolDamage or SpellEffectName.EnvironmentalDamage or SpellEffectName.HealthLeech
            or SpellEffectName.WeaponDamage or SpellEffectName.WeaponDamageNoschool or SpellEffectName.NormalizedWeaponDmg or SpellEffectName.WeaponPercentDamage
            or SpellEffectName.PowerBurn);
    }

    /// <summary>The aura types whose initial cast already counts as active for the procs (Spell.cpp:1460-1478).</summary>
    public static bool AppliesDamageOrHealAura(SpellInfo spell, int effectMask)
    {
        ArgumentNullException.ThrowIfNull(spell);
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            if ((effectMask & (1 << i)) == 0)
            {
                continue;
            }

            if (spell.Effects[i].AuraType is AuraType.PeriodicDamage or AuraType.PeriodicDamagePercent or AuraType.PeriodicLeech
                or AuraType.PeriodicHealthFunnel or AuraType.PeriodicHeal or AuraType.ObsModHealth or AuraType.PowerBurnMana or AuraType.SchoolAbsorb)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The hand a spell swings for its procs (vmangos <c>SpellEntry::GetWeaponAttackType</c>).</summary>
    public static WeaponAttackType AttackTypeOf(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (RangedSpellFacts.UsesRangedWeapon(spell))
        {
            return WeaponAttackType.RangedAttack;
        }

        return (spell.AttributesEx3 & (uint)SpellAttributesEx3Combat.RequiresOffhandWeapon) != 0 ? WeaponAttackType.OffAttack : WeaponAttackType.BaseAttack;
    }

    /// <summary>The school mask a white swing counts as (vmangos SPELL_SCHOOL_MASK_NORMAL).</summary>
    public static uint MeleeSchoolMask => SpellSchoolMasks.Of(SpellSchool.Normal);

    /// <summary>Whether <paramref name="unit"/> stands (vmangos Unit::IsStandingUp): sitting victims are crit without a crit proc (Unit.cpp:8988).</summary>
    internal static bool IsStandingUp(Unit unit) => unit.StandState is StandState.Stand or StandState.Dead;
}
