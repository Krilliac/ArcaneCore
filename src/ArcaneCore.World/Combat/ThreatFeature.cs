using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Combat;

/// <summary>
/// Binds the threat formula to the world (discovered <see cref="IWorldFeature"/>): every map's <see cref="MapCombat"/> gets the spell
/// system's <see cref="SpellThreatModifiers"/> (MOD_THREAT and MOD_CRITICAL_THREAT auras, SPELLMOD_THREAT talents) so damage, healing and
/// spell threat follow vmangos' ThreatCalcHelper::CalcThreat, and the spell_threat table (a registered <see cref="ISpellThreatCatalog"/>,
/// else the <see cref="SpellThreatFeature"/> table) so spell threat multipliers and flat spell threat apply. Without data only the
/// multipliers of the auras apply.
/// </summary>
public sealed class ThreatFeature(IServiceProvider services) : IWorldFeature
{
    public IThreatModifierSource? Modifiers { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        Modifiers = new SpellThreatModifiers(spells.System);

        world.MapCreated += Install;
        foreach (Map map in world.Maps)
        {
            Install(map);
        }
    }

    private void Install(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat)
        {
            combat.ThreatModifiers = Modifiers;
            combat.SpellThreatCatalog = services.GetService<ISpellThreatCatalog>() ?? services.GetService<SpellThreatFeature>()?.Table;
        }
    }
}
