using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Threat a spell adds to a creature (vmangos <c>Spell::EffectThreat</c>, SpellEffects.cpp:3503-3514, which calls
/// <c>Unit::AddThreat</c> with the spell, Unit.cpp:7424, <c>ThreatManager::addThreat</c>, ThreatManager.cpp:391, and
/// <c>ThreatCalcHelper::CalcThreat</c>, :35).
/// </summary>
internal static class SpellThreat
{
    /// <summary>
    /// Only a living unit that can hold a threat list (<see cref="ThreatRules.CanHaveThreatList"/>) takes threat, and both units
    /// must be alive and in one map. The amount goes through the threat formula (<see cref="ThreatCalc.Calc"/>): the caster's
    /// SPELLMOD_THREAT talents, then the caster's MOD_THREAT multiplier of the spell's school (players only, the first school of the mask).
    /// EX4_NO_HARMFUL_THREAT spells add nothing and EX_NO_THREAT spells only raise an existing entry (ThreatManager.cpp:424).
    /// </summary>
    public static void Add(SpellSystem system, Unit caster, Unit victim, SpellInfo spell, float threat)
    {
        if (!victim.IsAlive || !caster.IsAlive || victim.Map is null || !ReferenceEquals(victim.Map, caster.Map)
            || (spell.AttributesEx4 & MapCombat.AttributeEx4NoHarmfulThreat) != 0)
        {
            return;
        }

        float total = ThreatCalc.Calc(new SpellThreatModifiers(system), caster, threat, false, spell.SchoolMask(), spell);
        bool noNewEntry = ((uint)spell.AttributesEx & MapCombat.AttributeExNoThreat) != 0;
        victim.Combat.Threat.AddThreat(caster, total, new ThreatContext(NoNewEntry: noNewEntry));
    }
}
