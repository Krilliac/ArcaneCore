using ArcaneCore.Game.Spells.Casters.Bonus;

namespace ArcaneCore.Game.Spells.Casters;

/// <summary>
/// The mage / priest / warlock rules that plug into a <see cref="SpellSystem"/> through its public registration
/// seams (<see cref="SpellSystem.RegisterEffect"/>, <see cref="SpellSystem.RegisterAura"/>,
/// <see cref="SpellSystem.AmountModifier"/>). The world daemon runs <see cref="Register"/> from its caster feature;
/// tests call it on a bare system. Each caster slice that adds handlers appends one line here.
/// </summary>
public static class CasterSpellModules
{
    public static void Register(SpellSystem spells, CasterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        options ??= new CasterOptions();
        PowerCostAuras.Register(spells);
        if (options.Bonus.Enabled)
        {
            // One amount modifier per spell system: never silently stack over another lane's modifier.
            if (spells.AmountModifier is not null and not SpellBonusModule)
            {
                throw new InvalidOperationException("the spell system already has an amount modifier; the caster spell power module cannot be installed");
            }

            spells.AmountModifier ??= new SpellBonusModule(spells);
        }
    }
}
