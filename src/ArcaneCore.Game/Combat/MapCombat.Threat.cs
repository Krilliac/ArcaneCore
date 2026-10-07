using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Combat;

/// <summary>The threat of damage (vmangos Unit::DealDamage threat block, Objects/Unit.cpp:866-870, and Unit::AddThreat, :7424-7432).</summary>
public sealed partial class MapCombat
{
    /// <summary>vmangos SPELL_ATTR_EX4_NO_HARMFUL_THREAT (SpellDefines.h:990): Unit::AddThreat ignores the spell.</summary>
    internal const uint AttributeEx4NoHarmfulThreat = 0x00000010;

    /// <summary>vmangos SPELL_ATTR_EX_NO_THREAT (SpellDefines.h:880): the spell never creates a new threat reference.</summary>
    internal const uint AttributeExNoThreat = 0x00000400;

    /// <summary>
    /// Whether the damage of <paramref name="spell"/> creates no threat at all for <paramref name="attacker"/> on <paramref name="victim"/>:
    /// SPELL_ATTR_EX4_NO_HARMFUL_THREAT, or SPELL_ATTR_EX_NO_THREAT when the victim's list does not hold the attacker yet (the two cases
    /// <see cref="AddDamageThreat"/> adds nothing in). Such a hit does not give the victim's AI its AttackedBy call either: AttackStart
    /// would create the zero-threat reference the attribute forbids (vmangos Spell::DoSpellHitOnUnit skips AttackedBy for EX_NO_THREAT).
    /// </summary>
    internal static bool SuppressesSpellThreat(Unit attacker, Unit victim, SpellInfo? spell)
    {
        if (spell is null)
        {
            return false;
        }

        if ((spell.AttributesEx4 & AttributeEx4NoHarmfulThreat) != 0)
        {
            return true;
        }

        return ((uint)spell.AttributesEx & AttributeExNoThreat) != 0
            && !victim.Combat.Threat.Entries.Any(entry => ReferenceEquals(entry.Target, attacker));
    }

    /// <summary>The aura and talent side of the threat formula; bound by the world's threat feature. Null: threat is the plain damage.</summary>
    public IThreatModifierSource? ThreatModifiers { get; set; }

    /// <summary>The spell_threat table; bound by the world's threat feature. Null: every multiplier is 1 and there is no flat threat.</summary>
    public ISpellThreatCatalog? SpellThreatCatalog { get; set; }

    /// <summary>
    /// Add the threat of <paramref name="damage"/> to a creature's list: damage times the spell's spell_threat multiplier
    /// (:868), then the threat formula (<see cref="ThreatCalc.Calc"/>) with the attack's school (a melee swing is physical), whether
    /// it crit, and the spell. A spell with NO_HARMFUL_THREAT adds nothing (Unit::AddThreat :7426); one with EX_NO_THREAT only raises
    /// an existing entry (ThreatManager::addThreat :424).
    /// </summary>
    internal void AddDamageThreat(Unit attacker, Unit victim, uint damage, bool crit, SpellInfo? spell)
    {
        if (spell is not null && (spell.AttributesEx4 & AttributeEx4NoHarmfulThreat) != 0)
        {
            return;
        }

        float multiplier = spell is null ? 1f : SpellThreatCatalog?.Find(spell.Id)?.Multiplier ?? 1f;
        uint schoolMask = spell is null ? ThreatCalc.PhysicalMask : spell.SchoolMask();
        float threat = ThreatCalc.Calc(ThreatModifiers, attacker, damage * multiplier, crit, schoolMask, spell);
        bool noNewEntry = spell is not null && ((uint)spell.AttributesEx & AttributeExNoThreat) != 0;
        victim.Combat.Threat.AddThreat(attacker, threat, new ThreatContext(NoNewEntry: noNewEntry));
    }
}
