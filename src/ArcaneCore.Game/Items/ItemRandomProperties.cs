using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>What a new item needs to get a random property ("of the Bear"): the roll and the property's enchantments.</summary>
public interface IItemRandomPropertySource
{
    /// <summary>vmangos Item::GenerateItemRandomPropertyId: a property id for a new item of <paramref name="template"/>, or 0.</summary>
    int Generate(ItemTemplate template);

    /// <summary>The ItemRandomProperties.dbc row of <paramref name="id"/>, or null.</summary>
    ItemRandomPropertyRecord? Find(int id);
}

/// <summary>
/// Random item properties (vmangos Item::GenerateItemRandomPropertyId and SetItemRandomProperties, Item.cpp:792-834, with GetItemEnchantMod,
/// ItemEnchantmentMgr.cpp): a template whose <c>random_property</c> names an <c>item_enchantment_template</c> entry rolls one of that entry's
/// rows by their percent chances, and the ItemRandomProperties.dbc row it names writes its id to ITEM_FIELD_RANDOM_PROPERTIES_ID and its three
/// enchantments to the property enchantment slots (3-5), where the enchantment engine applies them while the item is worn. Build 5875 has
/// only these positive ids: random suffixes (negative ids, ITEM_FIELD_PROPERTY_SEED) came later, so the suffix factor stays 0.
/// </summary>
public sealed class ItemRandomProperties : IItemRandomPropertySource
{
    private readonly ItemRandomPropertyCatalog _catalog;
    private readonly Func<float> _rollPercent;
    private readonly Action<string>? _report;
    private readonly HashSet<uint> _reported = [];
    private readonly Lock _reportLock = new();

    /// <param name="catalog">The content.</param>
    /// <param name="rollPercent">vmangos rand_chance_f: a value in [0, 100). Default: <see cref="Random.Shared"/>.</param>
    /// <param name="report">Content errors (an entry without rows, an id the DBC does not have), once per id.</param>
    public ItemRandomProperties(ItemRandomPropertyCatalog catalog, Func<float>? rollPercent = null, Action<string>? report = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _rollPercent = rollPercent ?? (() => Random.Shared.NextSingle() * 100f);
        _report = report;
    }

    public ItemRandomPropertyCatalog Catalog => _catalog;

    public int Generate(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.RandomProperty == 0)
        {
            return 0;
        }

        IReadOnlyList<ItemEnchantmentChance> rows = _catalog.Group(template.RandomProperty);
        if (rows.Count == 0)
        {
            Report(template.RandomProperty, $"Item RandomProperty id #{template.RandomProperty} used in item_template but it doesn't have records in item_enchantment_template");
            return 0;
        }

        // GetItemEnchantMod: roll = rand_chance_f() * (total / 100), then the first row whose running sum reaches it.
        float total = 0f;
        foreach (ItemEnchantmentChance row in rows)
        {
            total += row.Chance;
        }

        float roll = _rollPercent() * (total / 100f);
        float sum = 0f;
        uint picked = 0;
        foreach (ItemEnchantmentChance row in rows)
        {
            sum += row.Chance;
            if (sum >= roll)
            {
                picked = row.EnchantId;
                break;
            }
        }

        if (picked == 0 || _catalog.FindProperty(picked) is null)
        {
            Report(picked | 0x8000_0000, $"Enchantment id #{picked} used but it doesn't have records in 'ItemRandomProperties.dbc'");
            return 0;
        }

        return (int)picked;
    }

    public ItemRandomPropertyRecord? Find(int id) => id > 0 ? _catalog.FindProperty((uint)id) : null;

    /// <summary>
    /// vmangos Item::SetItemRandomProperties for a positive id: ITEM_FIELD_RANDOM_PROPERTIES_ID, then the property's three enchantments into
    /// PROP_ENCHANTMENT_SLOT_0..2 with no duration and no charges.
    /// </summary>
    public static void Apply(Item item, ItemRandomPropertyRecord property)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(property);
        item.SetInt32(UpdateFields.ItemFieldRandomPropertiesId, (int)property.Id);
        for (int i = 0; i < ItemRandomPropertyRecord.EnchantSlots; i++)
        {
            ItemEnchantments.Set(item, EnchantSlots.Property0 + i, i < property.EnchantIds.Count ? property.EnchantIds[i] : 0, 0, 0);
        }
    }

    private void Report(uint key, string message)
    {
        if (_report is null)
        {
            return;
        }

        lock (_reportLock)
        {
            if (!_reported.Add(key))
            {
                return;
            }
        }

        _report(message);
    }
}
