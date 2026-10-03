using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_ATTACK_ME (114, taunt: Taunt, Growl, Challenging Shout, Challenging Roar) and SPELL_EFFECT_MODIFY_THREAT_PERCENT (125),
/// after vmangos <c>Spell::EffectTaunt</c> (SpellEffects.cpp:3356-3395) and <c>Spell::EffectModifyThreatPercent</c> (:5638-5646).
/// <list type="bullet">
/// <item>Attack me: a creature that already attacks the caster is left alone (vmangos sends SPELL_FAILED_DONT_REPORT, which is
/// silent; the cast result packet is not sent here). Otherwise, when the target can hold a threat list and has a current victim,
/// the caster's threat is set to the current victim's (patch 1.11: threat equal to the previous aggro target, permanently), and the
/// caster becomes the current victim at once (builds above 1.10.2, the build ArcaneCore serves).</item>
/// <item>Modify threat percent: the caster's entry on the target's list changes by the effect value percent (below -100 it is
/// removed).</item>
/// </list>
/// The taunt aura (SPELL_AURA_MOD_TAUNT) is <see cref="ThreatAuras"/>. A creature's static taunt immunity
/// (CREATURE_IMMUNITY_TAUNT, Creature.cpp:444-448) has no provider on this base: <c>ICreatureImmunityProvider</c> carries mechanic and
/// school masks only, so only immunity auras on the unit stop the effect.
/// </summary>
public sealed class ThreatEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.AttackMe, EffectAttackMe);
        system.RegisterEffect(SpellEffectName.ModifyThreatPercent, EffectModifyThreatPercent);
    }

    private static void EffectAttackMe(SpellEffectContext context)
    {
        Unit target = context.Target;
        Unit caster = context.Caster;
        if (target is not Player && ReferenceEquals(target.Combat.Victim, caster))
        {
            return;
        }

        if (!ThreatRules.CanHaveThreatList(target) || target.Combat.Threat.CurrentVictim is not { } current)
        {
            return;
        }

        ThreatList list = target.Combat.Threat;
        list.AddThreat(caster, list.GetOnlineThreat(current) - list.GetOnlineThreat(caster));
        list.SetCurrentVictimIfCan(caster);
    }

    private static void EffectModifyThreatPercent(SpellEffectContext context)
    {
        if (context.Target.Combat.HasThreatList)
        {
            context.Target.Combat.Threat.ModifyThreatPercent(context.Caster, context.Value);
        }
    }
}
