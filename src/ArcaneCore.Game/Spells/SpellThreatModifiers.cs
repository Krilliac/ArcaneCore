using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell system's answers for the threat formula (<see cref="IThreatModifierSource"/>): talents through
/// <see cref="SpellSystem.SpellModifiers"/> (SPELLMOD_THREAT, identity until the spell-modifier lane installs the real storage) and
/// the live MOD_THREAT / MOD_CRITICAL_THREAT auras (vmangos Aura::HandleModThreat, SpellAuras.cpp:3887-3918, and
/// Unit::GetTotalAuraMultiplierByMiscMask). The multiplier of a school is read from the auras when asked, which equals vmangos'
/// stored m_threatModifier[school] (the product of (100 + amount) / 100 over the applied auras).
/// </summary>
public sealed class SpellThreatModifiers(SpellSystem system) : IThreatModifierSource
{
    /// <summary>Arcane Shroud (vmangos SpellAuras.cpp:3900): +2 threat percent per level above 60.</summary>
    public const uint ArcaneShroud = 26400;

    /// <summary>The Eye of Diminution (vmangos SpellAuras.cpp:3905): +1 threat percent per level above 60.</summary>
    public const uint EyeOfDiminution = 28862;

    public float ApplySpellMod(Unit hated, SpellInfo spell, float threat)
        => system.ModFloat(hated, spell, SpellModOp.Threat, threat);

    public float CriticalThreatMultiplier(Unit hated, uint schoolMask)
    {
        float multiplier = 1f;
        foreach (SpellAuraHolder holder in system.GetAuras(hated))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == AuraType.ModCriticalThreat && ((uint)aura.MiscValue & schoolMask) != 0)
                {
                    multiplier *= (100.0f + aura.Amount) / 100.0f;
                }
            }
        }

        return multiplier;
    }

    public float TotalThreatMultiplier(Unit hated, int school)
    {
        if (hated is not Player)
        {
            return 1f;
        }

        float multiplier = 1f;
        foreach (SpellAuraHolder holder in system.GetAuras(hated))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is null || aura.Type != AuraType.ModThreat || ((uint)aura.MiscValue & (1u << school)) == 0)
                {
                    continue;
                }

                int amount = aura.Amount;
                int perLevel = holder.Spell.Id switch
                {
                    ArcaneShroud => 2,
                    EyeOfDiminution => 1,
                    _ => 0,
                };
                if (perLevel != 0 && hated.Level > 60)
                {
                    amount += perLevel * (hated.Level - 60);
                }

                multiplier *= (100.0f + amount) / 100.0f;
            }
        }

        return multiplier;
    }
}
