using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.WorldData.Items;

namespace ArcaneCore.Game.Crafting.Enchanting;

/// <summary>
/// The enchantments of the enchanting catalog (<see cref="EnchantCatalog"/>, the client's SpellItemEnchantment.dbc) as the item-enchantment
/// definitions that item combat procs and trade enchant planning read (<see cref="IItemEnchantmentCatalog"/>): one source for both.
/// </summary>
public static class EnchantCatalogDefinitions
{
    /// <summary>Every row of <paramref name="catalog"/>: per effect its type, argument (a spell id for the spell types) and amount.</summary>
    public static IReadOnlyList<ItemEnchantmentDefinition> From(EnchantCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. catalog.All.OrderBy(row => row.Id).Select(Convert)];
    }

    /// <summary>One row: the effect types, arguments and amounts in order; the visual id and the flags (m_flags) as metadata.</summary>
    public static ItemEnchantmentDefinition Convert(SpellItemEnchantment row)
    {
        ArgumentNullException.ThrowIfNull(row);
        int count = Math.Min(EnchantCatalog.EffectCount, Math.Min(row.Types.Count, Math.Min(row.Args.Count, row.Amounts.Count)));
        var effects = new ItemEnchantmentEffect[count];
        for (int i = 0; i < count; i++)
        {
            effects[i] = new ItemEnchantmentEffect(row.Types[i], row.Args[i], row.Amounts[i]);
        }

        return new ItemEnchantmentDefinition(row.Id, effects, amountMax: null, nameFlags: 0, itemVisualId: row.VisualId, slotFlags: row.Flags);
    }
}
