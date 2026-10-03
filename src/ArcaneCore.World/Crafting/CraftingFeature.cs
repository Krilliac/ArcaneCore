using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Crafting;

/// <summary>
/// Crafting in the world daemon (discovered <see cref="IWorldFeature"/>, docs/areas/crafting.md): installs the reagent and tool check
/// and cost taker (<see cref="ReagentRules.Install"/>) and the CREATE_ITEM effect (<see cref="CreateItemSpells.Install"/>) on the shared spell system. Configuration <c>Crafting:Enabled</c> (default
/// true, retail); false leaves crafting unregistered.
/// </summary>
public sealed class CraftingFeature(IServiceProvider services) : IWorldFeature
{
    /// <summary>The master switch of crafting (reagents, tools and the CREATE_ITEM effect).</summary>
    public const string EnabledKey = "Crafting:Enabled";

    /// <summary>Whether <c>Crafting:Enabled</c> is on: true when unset or unparsable (retail default).</summary>
    public static bool IsEnabled(IConfiguration? configuration)
        => configuration is null || !bool.TryParse(configuration[EnabledKey], out bool enabled) || enabled;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!IsEnabled(services.GetService<IConfiguration>()))
        {
            return;
        }

        SpellSystem system = services.GetRequiredService<Spells.SpellFeature>().System;
        ReagentRules.Install(system);
        CreateItemSpells.Install(system);   // after the reagent pair: a craft must never be free
        FirstAidObserver.Install(system);
    }
}
