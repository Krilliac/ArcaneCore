using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Read-side aura queries for effect and aura handlers (vmangos Unit::GetAurasByType,
/// GetMaxPositiveAuraModifier / GetMaxNegativeAuraModifier, Unit.cpp:3019-3041). They read the holders
/// of <see cref="SpellSystem.GetAuras"/>, so an aura type whose own handler is not registered is still
/// visible to the formulas that consume it.
/// </summary>
internal static class SpellAuraQueries
{
    /// <summary>Every live aura of <paramref name="type"/> on <paramref name="unit"/>.</summary>
    public static IEnumerable<SpellAura> AurasOfType(this SpellSystem system, Unit unit, AuraType type)
    {
        foreach (SpellAuraHolder holder in system.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == type)
                {
                    yield return aura;
                }
            }
        }
    }

    /// <summary>The largest positive amount among the auras (0 when none is positive).</summary>
    public static int MaxPositiveAuraModifier(this SpellSystem system, Unit unit, AuraType type)
    {
        int modifier = 0;
        foreach (SpellAura aura in system.AurasOfType(unit, type))
        {
            modifier = Math.Max(modifier, aura.Amount);
        }

        return modifier;
    }

    /// <summary>The most negative amount among the auras (0 when none is negative).</summary>
    public static int MaxNegativeAuraModifier(this SpellSystem system, Unit unit, AuraType type)
    {
        int modifier = 0;
        foreach (SpellAura aura in system.AurasOfType(unit, type))
        {
            modifier = Math.Min(modifier, aura.Amount);
        }

        return modifier;
    }
}
