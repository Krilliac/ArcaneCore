using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell side of the proc engine: what a cast's hit, miss, cast end and finish hand <see cref="ProcDamageAndSpell"/>
/// (vmangos Spell::DoAllEffectOnTarget, Spell.cpp:1160-1540; Spell::cast, Spell.cpp:3724-3749; Spell::HandleAddTargetTriggerAuras, Spell.cpp:4271-4315).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>The spell a proc is casting right now and the spell whose hit made it proc (vmangos m_triggeredByParentSpellInfo).</summary>
    private (uint SpellId, uint ParentSpellId)? _procCastParent;

    /// <summary>
    /// One target's procs (vmangos Spell::DoAllEffectOnTarget proc block): the cast's attacker/victim flags (secondary targets turn a melee ability
    /// into a harmful spell for the attacker), dropped when the target took none of a negative spell's negative effects; then the heal, damage or
    /// no-damage branch builds the outcome and the amount, and the suppress attributes clear a side.
    /// </summary>
    internal void FireSpellHitProcs(SpellCast cast, Unit target, SpellMissInfo miss, uint amount, uint originalAmount, bool critical, uint absorbed,
        int effectMask, bool reflected, uint healing = 0)
    {
        if (!ProcFlagRules.CanTrigger(cast) || _objectCastDepth > 0)
        {
            return; // Spell.cpp:645-646: nothing a game object casts triggers.
        }

        SpellInfo spell = cast.Spell;
        WeaponAttackType attackType = ProcFlagRules.AttackTypeOf(spell);
        (ProcFlags attacker, ProcFlags victim) = ProcFlagRules.SpellFlags(spell, attackType);
        cast.ProcTargetCount++;
        if (cast.ProcTargetCount > 1 && (attacker & ProcFlags.DealMeleeAbility) != 0)
        {
            attacker = (attacker & ~ProcFlags.DealMeleeAbility) | ProcFlags.DealHarmfulSpell;
        }

        if (((attacker | victim) & ProcFlagRules.NegativeTriggerMask) != 0 && (effectMask & ProcFlagRules.NegativeEffectMask(spell)) == 0
            && miss is SpellMissInfo.None or SpellMissInfo.Reflect)
        {
            attacker = ProcFlags.None;
            victim = ProcFlags.None;
        }

        Unit unitTarget = reflected ? cast.Caster : target;
        ProcFlagsEx extra;
        uint eventAmount;
        uint eventOriginal;
        if (healing > 0 && unitTarget.IsAlive)
        {
            extra = critical ? ProcFlagsEx.CriticalHit : ProcFlagsEx.NormalHit;
            eventAmount = healing;
            eventOriginal = healing;
        }
        else if (amount + absorbed > 0 && miss is SpellMissInfo.None or SpellMissInfo.Reflect)
        {
            extra = ProcFlagRules.ExtendMask(miss, hasDamageInfo: true, critical, absorbed);
            victim |= ProcFlags.TakenAnyDamage;
            // JoR and JoC: paladin melee spells trigger melee procs instead of magic ones (Spell.cpp:1430-1441; CF_PALADIN_JUDGEMENT_OF_RIGHTEOUSNESS is bit 10).
            if ((spell.IsFitToFamily(10, 10) && spell.SpellIconId == 25) || (spell.SpellFamilyName == 10 && spell.SpellIconId == 561 && spell.SpellVisual == 0))
            {
                attacker = ProcFlags.DealMeleeAbility;
                victim = ProcFlags.TakeMeleeAbility | ProcFlags.TakenAnyDamage;
            }

            eventAmount = amount;
            eventOriginal = originalAmount;
        }
        else
        {
            if (attacker == ProcFlags.None && victim == ProcFlags.None)
            {
                return;
            }

            extra = ProcFlagRules.ExtendMask(miss, hasDamageInfo: true);
            uint active = 0;
            // "arcane projectile triggers a spell that deals damage" (mage family flags 0x40800), the initial cast of a damage or heal aura, and dispels.
            if ((spell.SpellFamilyName == 3 && spell.SpellFamilyFlags == 0x40800) || ProcFlagRules.AppliesDamageOrHealAura(spell, effectMask))
            {
                active = 1;
            }

            if (spell.HasEffect(SpellEffectName.Dispel))
            {
                active = 1;
                if (Relations.IsHostile(cast.Caster, unitTarget))
                {
                    attacker = (attacker & ~ProcFlags.DealHelpfulAbility) | ProcFlags.DealHarmfulSpell;
                    victim = (victim & ~ProcFlags.TakeHelpfulAbility) | ProcFlags.TakeHarmfulSpell;
                }
            }

            eventAmount = active;
            eventOriginal = active;
        }

        if ((spell.AttributesEx3 & ProcAttributes.Ex3SuppressCasterProcs) != 0)
        {
            attacker = ProcFlags.None;
        }

        if ((spell.AttributesEx3 & ProcAttributes.Ex3SuppressTargetProcs) != 0)
        {
            victim = ProcFlags.None;
        }

        if (attacker == ProcFlags.None && victim == ProcFlags.None)
        {
            return;
        }

        ProcDamageAndSpell(cast.Caster, new ProcEvent
        {
            Victim = unitTarget,
            AttackerFlags = attacker,
            VictimFlags = victim,
            Extra = extra,
            Amount = eventAmount,
            OriginalAmount = eventOriginal,
            AttackType = attackType,
            ProcSpell = spell,
            SpellTriggeredByAuraOrItem = cast.IsTriggeredByAura || (cast.IsTriggered && cast.CastItem is not null),
            Reflected = reflected,
        });
    }

    /// <summary>
    /// vmangos Spell::cast (Spell.cpp:3724-3749): "Trigger procs on cast end for caster": the attacker flags with PROC_EX_CAST_END and, when the
    /// main target is one of the spell's targets, its outcome (<paramref name="mainMiss"/> null: it is not, and the event is CAST_END alone); only
    /// auras whose spell_proc_event asks for CAST_END react. A cast with no unit target procs as a normal hit. ArcaneCore rolls crits per effect, after
    /// this point, so a hit is always NORMAL_HIT here (vmangos knows target.isCrit already).
    /// </summary>
    private void FireCastEndProcs(SpellCast cast, Unit? mainTarget, SpellMissInfo? mainMiss, bool targetsEmpty)
    {
        if (!ProcFlagRules.CanTrigger(cast) || _objectCastDepth > 0 || (cast.Spell.AttributesEx3 & ProcAttributes.Ex3SuppressCasterProcs) != 0)
        {
            return;
        }

        WeaponAttackType attackType = ProcFlagRules.AttackTypeOf(cast.Spell);
        (ProcFlags attacker, _) = ProcFlagRules.SpellFlags(cast.Spell, attackType);
        if (attacker == ProcFlags.None)
        {
            return;
        }

        ProcFlagsEx extra = ProcFlagsEx.CastEnd | mainMiss switch
        {
            null => ProcFlagsEx.None,
            SpellMissInfo.None => ProcFlagsEx.NormalHit,
            SpellMissInfo miss => ProcFlagRules.ExtendMask(miss, hasDamageInfo: false),
        };
        bool triggeredByAuraOrItem = cast.IsTriggeredByAura || (cast.IsTriggered && cast.CastItem is not null);
        ProcDamageAndSpell(cast.Caster, new ProcEvent
        {
            Victim = mainTarget ?? cast.Caster,
            AttackerFlags = attacker,
            Extra = extra,
            Amount = 1,
            OriginalAmount = 1,
            AttackType = attackType,
            ProcSpell = cast.Spell,
            SpellTriggeredByAuraOrItem = triggeredByAuraOrItem,
        });
        if (targetsEmpty)
        {
            ProcDamageAndSpell(cast.Caster, new ProcEvent
            {
                AttackerFlags = attacker,
                Extra = ProcFlagsEx.NormalHit,
                Amount = 1,
                OriginalAmount = 1,
                AttackType = attackType,
                ProcSpell = cast.Spell,
                SpellTriggeredByAuraOrItem = triggeredByAuraOrItem,
            });
        }
    }

    /// <summary>
    /// vmangos Spell::HandleAddTargetTriggerAuras (Spell.cpp:4271-4315): every ADD_TARGET_TRIGGER aura of the caster whose class mask covers the
    /// cast rolls its effect value as a percent chance for each hit target (the caster itself for a reflected hit) and casts its trigger spell on
    /// it, triggered by the aura (Frostbite, Improved Wing Clip, Relentless Strikes, Wolfshead Helm). Dead targets are skipped unless the trigger
    /// spell targets its own caster (Relentless Strikes after a killing finisher).
    /// </summary>
    private void HandleAddTargetTriggerAuras(SpellCast cast, IReadOnlyList<(Unit Target, SpellMissInfo Miss)> outcomes)
    {
        Unit caster = cast.Caster;
        SpellInfo spell = cast.Spell;
        foreach (SpellAuraHolder holder in GetAuras(caster).ToArray())
        {
            if (holder.IsRemoved || holder.Spell.SpellFamilyName != spell.SpellFamilyName)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is not { Type: AuraType.AddTargetTrigger } || (AffectMask(holder.Spell, aura.EffectIndex) & spell.SpellFamilyFlags) == 0)
                {
                    continue;
                }

                if (Store.Get(holder.Spell.Effects[aura.EffectIndex].TriggerSpell) is not { } trigger)
                {
                    continue;
                }

                foreach ((Unit hitTarget, SpellMissInfo miss) in outcomes)
                {
                    Unit? target = miss switch
                    {
                        SpellMissInfo.None => hitTarget,
                        SpellMissInfo.Reflect => caster,
                        _ => null,
                    };
                    if (target is null || holder.IsRemoved)
                    {
                        continue;
                    }

                    bool selfCast = trigger.Effects.Count > 0 && trigger.Effects[0].TargetA == SpellImplicitTarget.UnitCaster;
                    if (!target.IsAlive && (ReferenceEquals(target, caster) || !selfCast))
                    {
                        continue;
                    }

                    int chance = holder.Spell.CalculateEffectValue(aura.EffectIndex, caster.Level, Random);
                    if (Random.Next(0, 100) < chance)
                    {
                        CastProcSpell(caster, trigger, SpellCastTargets.ForUnit(target.Guid), holder.Spell);
                    }
                }
            }
        }
    }
}
