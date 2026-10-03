using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat.Threat;

/// <summary>
/// Threat from healing and other positive effects (vmangos Spell::DoAllEffectOnTarget heal branch, Spells/Spell.cpp:1362-1366,
/// Aura periodic heal handlers, SpellAuras.cpp:6013, and HostileRefManager::threatAssist, Threat/HostileRefManager.cpp:62-76).
/// </summary>
public static class HealThreat
{
    /// <summary>
    /// The threat of a heal that restored <paramref name="healed"/> health: half of it (a paladin's direct heal a quarter), times the
    /// spell's spell_threat multiplier, assisted to everyone who fights the target. A heal over time is half for every class
    /// (SpellAuras.cpp:6013).
    /// </summary>
    public static Unit[] ForHeal(MapCombat combat, Unit caster, Unit target, SpellInfo spell, uint healed, bool periodic)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (healed == 0)
        {
            return [];
        }

        float classModifier = !periodic && caster.Class == Class.Paladin ? 0.25f : 0.5f;
        float multiplier = combat.SpellThreatCatalog?.Find(spell.Id)?.Multiplier ?? 1f;
        return HostileRefs.ThreatAssist(target, caster, healed * classModifier * multiplier, spell, combat.ThreatModifiers);
    }
}
