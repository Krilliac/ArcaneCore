using ArcaneCore.Kernel.Crafting;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// The enchanting engine's catalog (SpellItemEnchantment.dbc rows) for the trade enchant scenarios: the engine refuses enchant casts while it has no
/// catalog, and the trade enchant planner reads the same rows (EnchantCatalogDefinitions).
/// </summary>
internal static class TradeEnchantCatalog
{
    /// <summary>An enchantment with no effect.</summary>
    public static SpellItemEnchantment Plain(uint id) => new(id, [0, 0, 0], [0, 0, 0], [0, 0, 0], "Synthetic enchantment", 0, 0);

    /// <summary>An enchantment that adds <paramref name="amount"/> to item stat <paramref name="stat"/> (ITEM_ENCHANTMENT_TYPE_STAT).</summary>
    public static SpellItemEnchantment Stat(uint id, uint stat, int amount) => new(id, [5, 0, 0], [amount, 0, 0], [stat, 0, 0], "Synthetic stat enchantment", 0, 0);

    /// <summary>Register a catalog of <paramref name="rows"/> on the synthetic server.</summary>
    public static void Register(IServiceCollection services, params SpellItemEnchantment[] rows) => services.AddSingleton(new EnchantCatalog(rows));

    /// <summary>A service hook that registers a catalog of <paramref name="rows"/>.</summary>
    public static Action<IServiceCollection> With(params SpellItemEnchantment[] rows) => services => Register(services, rows);
}
