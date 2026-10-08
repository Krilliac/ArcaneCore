using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Rogue;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Installs the rogue bleed scripts (<see cref="RogueBleedScripts"/>: the Rupture and Garrote attack power terms) on the spell system after
/// the combo point feature (discovered <see cref="IWorldFeature"/>; features attach in full-name order and <c>ArcaneCore.World.Combat</c>
/// comes first).
/// </summary>
public sealed class RogueScriptFeature(IServiceProvider services) : IWorldFeature
{
    public RogueBleedScripts? Scripts { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArcaneCore.Game.Combat.ComboPointService combos = services.GetRequiredService<ComboFeature>().Service
            ?? throw new InvalidOperationException("the rogue scripts need the combo point feature, which attaches first");
        Scripts = RogueBleedScripts.Install(services.GetRequiredService<SpellFeature>().System, combos);
    }
}
