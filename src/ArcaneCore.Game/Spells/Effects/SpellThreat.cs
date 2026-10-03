using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

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

    /// <summary>
    /// Only a living creature has a threat list and both units must be alive and in one map. The amount is
    /// scaled by the caster's SPELL_AURA_MOD_THREAT auras that cover the spell's school
    /// (Unit::ApplyTotalThreatModifier, Unit.cpp:7409: the product of (100 + amount) / 100 per school bit).
    /// EX4_NO_HARMFUL_THREAT spells add nothing and EX_NO_THREAT spells only raise an existing entry
    /// (ThreatManager.cpp:424).
    /// Limit: pets and totems cannot be told apart from creatures here (no pet or totem model yet), and the
    /// SPELLMOD_THREAT talent modifier (CalcThreat :43) needs the spell-mod system.
    /// </summary>
    public static void Add(SpellSystem system, Unit caster, Unit victim, SpellInfo spell, float threat)
    {
        if (victim is not Creature target || !target.IsAlive || !caster.IsAlive
            || target.Map is null || !ReferenceEquals(target.Map, caster.Map)
            || (spell.AttributesEx4 & AttributeEx4NoHarmfulThreat) != 0)
        {
            return;
        }

        if (threat != 0f)
        {
            uint schoolBit = 1u << (int)spell.School;
            foreach (SpellAura aura in system.AurasOfType(caster, AuraType.ModThreat))
            {
                if (((uint)aura.MiscValue & schoolBit) != 0)
                {
                    threat *= (100.0f + aura.Amount) / 100.0f;
                }
            }
        }

        bool known = target.Combat.Threat.Entries.Any(entry => ReferenceEquals(entry.Target, caster));
        if (!known && ((uint)spell.AttributesEx & AttributeExNoThreat) != 0)
        {
            return;
        }

        target.Combat.Threat.AddThreat(caster, threat);
    }
}
