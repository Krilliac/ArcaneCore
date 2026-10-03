using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Casters;

/// <summary>
/// Aura types 72 (MOD_POWER_COST_SCHOOL_PCT) and 73 (MOD_POWER_COST_SCHOOL): Clearcasting-style cost
/// reductions and the mage/warlock cost talents write the per-school unit fields that
/// <see cref="SpellSystem.CalculatePowerCost"/> reads. vmangos SpellAuras.cpp:5391-5412
/// (HandleModPowerCostPCT, HandleModPowerCost): the percent field is recomputed from every aura of the type whose
/// misc value mask contains the school bit (amount / 100), the flat field is incremented and decremented by the amount.
/// </summary>
public static class PowerCostAuras
{
    private const int SchoolCount = 7;

    /// <summary>Install the two handlers on <paramref name="spells"/>.</summary>
    public static void Register(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        spells.RegisterAura(AuraType.ModPowerCostSchoolPct, new AuraHandler(
            static (system, holder, aura, _) => ApplyPercent(system, holder.Target, aura), null));
        spells.RegisterAura(AuraType.ModPowerCostSchool, new AuraHandler(
            static (_, holder, aura, apply) => ApplyFlat(holder.Target, aura, apply), null));
    }

    private static void ApplyPercent(SpellSystem system, Unit target, SpellAura aura)
    {
        for (int school = 0; school < SchoolCount; school++)
        {
            int bit = 1 << school;
            if ((aura.MiscValue & bit) != 0)
            {
                int total = system.GetTotalAuraModifier(target, AuraType.ModPowerCostSchoolPct, a => (a.MiscValue & bit) != 0);
                target.SetFloat(UpdateFields.UnitFieldPowerCostMultiplier + school, total / 100.0f);
            }
        }
    }

    private static void ApplyFlat(Unit target, SpellAura aura, bool apply)
    {
        for (int school = 0; school < SchoolCount; school++)
        {
            if ((aura.MiscValue & (1 << school)) != 0)
            {
                int field = UpdateFields.UnitFieldPowerCostModifier + school;
                target.SetInt32(field, target.GetInt32(field) + (apply ? aura.Amount : -aura.Amount));
            }
        }
    }
}
