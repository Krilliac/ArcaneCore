using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Economy;
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
        ReagentRules.Install(system, IsInTrade);
        CreateItemSpells.Install(system);   // after the reagent pair: a craft must never be free
        FirstAidObserver.Install(system);
    }

    /// <summary>vmangos <c>Item::IsInTrade</c>: the item is offered in one of the six trade slots of the player's open trade.</summary>
    private bool IsInTrade(Player player, Item item)
        => services.GetService<EconomyFeature>()?.TradeOf(player) is { } trade && trade.SideOf(player).Items.Contains(item.Guid);
}
