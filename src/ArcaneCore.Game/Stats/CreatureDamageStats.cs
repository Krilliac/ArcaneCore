using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Stats;

/// <summary>
/// A creature's melee damage fields recomputed from its template and its auras, after vmangos <c>Creature::UpdateDamagePhysical</c>
/// (StatSystem.cpp:880-956) for the main hand:
/// <c>min = ((weaponMin * apFactor * BASE_PCT) + UNIT_MOD_DAMAGE_PHYSICAL) * TOTAL_PCT</c>, where
/// <list type="bullet">
/// <item><c>apFactor = 0.7 + 0.3 * currentAttackPower / templateAttackPower</c> when the template has an attack power ("AP for units is 30% of base
/// damage"): Demoralizing Shout and the other attack power auras move a creature's damage;</item>
/// <item>TOTAL_PCT is the main-hand group of the creature's <see cref="UnitModLedger"/> (generic physical MOD_DAMAGE_PERCENT_DONE), times 0.4 while
/// a creature that holds a weapon (virtual item class weapon, Creature::HasWeapon) is disarmed;</item>
/// <item>UNIT_MOD_DAMAGE_PHYSICAL is the creature's physical MOD_DAMAGE_DONE (any item class: HandleModDamageDone applies a non-player's aura
/// generically).</item>
/// </list>
/// With no aura the result is the template range the creature spawned with (<c>Creature.InitializeFields</c>), so a creature nobody touched keeps
/// its fields. Summoned creatures (pets, guardians, totems) are left to their own stat code. Run by <see cref="CombatStatAuras"/> when such an aura
/// comes or goes, or changes its amount in place (a refresh or a stack change); world thread only.
/// </summary>
public static class CreatureDamageStats
{
    private const byte ItemClassWeapon = 2;

    /// <summary>The share of its damage a disarmed creature with a weapon keeps (StatSystem.cpp:914-919).</summary>
    public const float DisarmedDamageFactor = 0.4f;

    /// <summary>Recompute the main-hand damage fields of <paramref name="creature"/>.</summary>
    public static void UpdateMelee(Creature creature, SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(spells);
        if (creature.Summon is not null)
        {
            return;
        }

        CreatureTemplate template = creature.Template;
        UnitModLedger? ledger = UnitModLedger.Find(creature);
        float basePct = ledger?.BasePct(UnitMods.DamageMainHand) ?? 1f;
        float totalPct = ledger?.TotalPct(UnitMods.DamageMainHand) ?? 1f;
        float totalPhysical = spells.GetTotalAuraModifier(creature, AuraType.ModDamageDone, static a => PlayerStatAuras.HasPhysical(a.MiscValue));
        if (HasWeapon(creature) && (creature.UnitFlags & UnitFlags.Disarmed) != 0)
        {
            totalPct *= DisarmedDamageFactor;
        }

        float min = template.MinMeleeDamage;
        float max = template.MaxMeleeDamage;
        if (template.MeleeAttackPower > 0)
        {
            uint mods = creature.GetUInt32(UpdateFields.UnitFieldAttackPowerMods);
            float now = StatFormulas.TotalAttackPower((int)template.MeleeAttackPower, unchecked((short)(mods & 0xFFFF)), unchecked((short)(mods >> 16)),
                creature.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier));
            float factor = 0.7f + (0.3f * now / template.MeleeAttackPower);
            min *= factor;
            max *= factor;
        }

        creature.SetFloat(UpdateFields.UnitFieldMindamage, Math.Max(0f, ((min * basePct) + totalPhysical) * totalPct));
        creature.SetFloat(UpdateFields.UnitFieldMaxdamage, Math.Max(0f, ((max * basePct) + totalPhysical) * totalPct));
    }

    /// <summary>vmangos Creature::HasWeapon: the main-hand virtual item is of class weapon.</summary>
    public static bool HasWeapon(Unit creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return creature.GetByte(UpdateFields.UnitVirtualItemInfo, 0) == ItemClassWeapon;
    }
}
