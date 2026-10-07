using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Totems;

/// <summary>
/// Intrinsic summoned-totem immunity, independently reimplemented from vmangos Totem.cpp:180-217
/// (0e3ff01e76d4758e8a7c3108b2717cc785ed56fa). Unlike ordinary creature immunity, the self-cast
/// and Shaman regeneration-family exceptions precede all per-effect checks, and intrinsic
/// foreign-cast immunities precede Creature's ignore-restrictions handling.
/// </summary>
internal static class TotemImmunity
{
    private const uint ShamanFamily = 11;
    private const ulong RegenerationFamilies = 0x4006000;

    /// <summary>
    /// Returns true when the totem override has a definitive result; false falls through to
    /// ordinary creature and live-aura immunity. A definitive false result bypasses that fallback.
    /// </summary>
    internal static bool TryGetEffectImmunity(Unit target, SpellInfo spell, int index, bool castOnSelf, out bool immune)
    {
        immune = false;
        if (!TotemQuery.IsTotem(target))
        {
            return false;
        }

        if (castOnSelf || (spell.SpellFamilyName == ShamanFamily && (spell.SpellFamilyFlags & RegenerationFamilies) != 0))
        {
            return true;
        }

        SpellEffectInfo effect = spell.Effects[index];
        if (effect.Effect is SpellEffectName.AttackMe or SpellEffectName.Heal or SpellEffectName.HealMaxHealth
            or SpellEffectName.HealMechanical or SpellEffectName.Energize)
        {
            immune = true;
        }
        else if (!spell.IsPositive)
        {
            // SpellEntry::IsSpellAppliesAura(mask) excludes aura type 0 and uses these six shapes.
            immune = effect.AuraType != AuraType.None && effect.Effect is SpellEffectName.ApplyAura
                or SpellEffectName.ApplyAreaAuraParty or SpellEffectName.ApplyAreaAuraPet or SpellEffectName.ApplyAreaAuraRaid
                or SpellEffectName.ApplyAreaAuraFriend or SpellEffectName.ApplyAreaAuraEnemy;
        }
        else
        {
            // SpellEntry::IsPeriodicRegenerateEffect reads the aura column, not the effect opcode.
            immune = effect.AuraType is AuraType.PeriodicHeal or AuraType.PeriodicEnergize or AuraType.PeriodicHealthFunnel;
        }

        return immune;
    }
}
