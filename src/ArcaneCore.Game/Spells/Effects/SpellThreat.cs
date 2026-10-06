using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Threat a spell adds to a creature (vmangos <c>Unit::AddThreat</c> with a threat spell, Unit.cpp:7424, which
/// calls <c>ThreatManager::addThreat</c>, ThreatManager.cpp:391, and <c>ThreatCalcHelper::CalcThreat</c>, :35).
/// </summary>
internal static class SpellThreat
{
    /// <summary>vmangos SPELL_ATTR_EX_NO_THREAT (SpellDefines.h:880): the spell never creates a new threat reference.</summary>
    private const uint AttributeExNoThreat = 0x00000400;

    /// <summary>vmangos SPELL_ATTR_EX4_NO_HARMFUL_THREAT (SpellDefines.h:990): Unit::AddThreat ignores the spell.</summary>
    private const uint AttributeEx4NoHarmfulThreat = 0x00000010;

    /// <summary>vmangos SPELL_ATTR_EX4_NO_HELPFUL_THREAT (SpellDefines.h:991).</summary>
    private const uint AttributeEx4NoHelpfulThreat = 0x00000020;

    /// <summary>
    /// Only a living creature has a threat list and both units must be alive and in one map. The amount is
    /// scaled by the caster's SPELL_AURA_MOD_THREAT auras that cover the spell's school
    /// (Unit::ApplyTotalThreatModifier, Unit.cpp:7409: the product of (100 + amount) / 100 per school bit).
    /// EX4_NO_HARMFUL_THREAT spells add nothing and EX_NO_THREAT spells only raise an existing entry
    /// (ThreatManager.cpp:424).
    /// Limit: pets and totems cannot be told apart from creatures here (no pet or totem model yet), and the
    /// SPELLMOD_THREAT talent modifier (CalcThreat :43) needs the spell-mod system.
    /// </summary>
    public static float? Calculate(SpellSystem system, Unit caster, Unit victim, SpellInfo spell, float threat, bool helpful)
    {
        if (victim is not Creature target || !target.IsAlive || !caster.IsAlive
            || target.Map is null || !ReferenceEquals(target.Map, caster.Map)
            || (helpful
                ? (spell.AttributesEx4 & AttributeEx4NoHelpfulThreat) != 0
                : (spell.AttributesEx4 & AttributeEx4NoHarmfulThreat) != 0))
        {
            return null;
        }

        bool known = target.Combat.Threat.Entries.Any(entry => ReferenceEquals(entry.Target, caster));
        if (!known && ((uint)spell.AttributesEx & AttributeExNoThreat) != 0)
        {
            return null;
        }

        return ApplySchoolModifier(system, caster, spell, threat);
    }

    public static void Add(SpellSystem system, Unit caster, Unit victim, SpellInfo spell, float threat)
    {
        if (Calculate(system, caster, victim, spell, threat, helpful: false) is { } scaled)
        {
            victim.Combat.Threat.AddThreat(caster, scaled);
        }
    }

    public static float ApplySchoolModifier(SpellSystem system, Unit caster, SpellInfo spell, float threat)
        => ApplySchoolModifier(system, caster, spell.SchoolMask(), threat);

    public static float ApplySchoolModifier(SpellSystem system, Unit caster, uint schoolMask, float threat)
    {
        foreach (SpellAura aura in system.AurasOfType(caster, AuraType.ModThreat))
        {
            if (((uint)aura.MiscValue & schoolMask) != 0)
            {
                threat *= (100.0f + aura.Amount) / 100.0f;
            }
        }

        return threat;
    }
}
