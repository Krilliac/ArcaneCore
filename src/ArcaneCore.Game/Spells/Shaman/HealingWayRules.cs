using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Shaman;

/// <summary>
/// Healing Way (talent 29206/29205/29202, its ADD_TARGET_TRIGGER puts 29203 on the healed target; vmangos <c>Unit::SpellHealingBonusTaken</c>,
/// Unit.cpp:5347-5355): every DUMMY aura of 29203 on the target multiplies the healing it takes from Healing Wave (CF_SHAMAN_HEALING_WAVE, bit 6)
/// by (100 + amount) / 100, a part of the taken percent. The amount is the stacked one (6% a stack, up to 3).
/// </summary>
public static class HealingWayRules
{
    public const uint HealingWayAura = 29203;
    public const uint ShamanFamily = 11;
    public const int HealingWaveBit = 6;

    /// <summary>The taken-healing multiplier of the Healing Way auras on <paramref name="target"/> for a heal of <paramref name="spell"/> (1 when none applies).</summary>
    public static float TakenMultiplier(SpellSystem system, Unit target, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (!spell.IsFitToFamily(ShamanFamily, HealingWaveBit))
        {
            return 1f;
        }

        float multiplier = 1f;
        foreach (SpellAuraHolder holder in system.GetAuras(target))
        {
            if (holder.IsRemoved || holder.Spell.Id != HealingWayAura)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is { Type: AuraType.Dummy })
                {
                    multiplier *= (aura.Amount + 100f) / 100f;
                }
            }
        }

        return multiplier;
    }
}