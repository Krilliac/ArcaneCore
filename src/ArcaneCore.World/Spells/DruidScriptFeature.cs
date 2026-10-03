using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Installs the druid finisher scripts (<see cref="DruidFinisherScripts"/>: Ferocious Bite and Rip attack power terms, Ferocious
/// Bite energy) on the spell system after the combo point feature (discovered <see cref="IWorldFeature"/>; features attach in
/// full-name order and <c>ArcaneCore.World.Combat</c> comes first).
/// </summary>
public sealed class DruidScriptFeature(IServiceProvider services) : IWorldFeature
{
    public DruidFinisherScripts? Scripts { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ComboFeature comboFeature = services.GetRequiredService<ComboFeature>();
        ArcaneCore.Game.Combat.ComboPointService combos = comboFeature.Service
            ?? throw new InvalidOperationException("the druid scripts need the combo point feature, which attaches first");
        Scripts = DruidFinisherScripts.Install(services.GetRequiredService<SpellFeature>().System, combos);
    }
}
