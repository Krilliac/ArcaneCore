namespace ArcaneCore.Kernel.Items;

/// <summary>
/// One row of ItemRandomProperties.dbc (build 5875; vmangos DBCStructure.h ItemRandomPropertiesEntry, DBCfmt.h "nsiiixxssssssssx"): the
/// id written to ITEM_FIELD_RANDOM_PROPERTIES_ID, the internal name and the three enchantments that go into the item's property
/// enchantment slots (PROP_ENCHANTMENT_SLOT_0..2). The "of the Bear" suffix text lives in the client's own copy of the file.
/// </summary>
public sealed record ItemRandomPropertyRecord(uint Id, string Name, IReadOnlyList<uint> EnchantIds)
{
    /// <summary>Enchantment ids per property (vmangos <c>enchant_id[3]</c>; fields 5-6 of the file are unused).</summary>
    public const int EnchantSlots = 3;
}

/// <summary>
/// One row of <c>item_enchantment_template</c> (vmangos ItemEnchantmentMgr.cpp LoadRandomEnchantmentsTable: entry, ench, chance): item templates
/// with <c>random_property</c> = <see cref="Entry"/> roll among the rows of that entry, each with its percent <see cref="Chance"/>.
/// </summary>
public sealed record ItemEnchantmentChance(uint Entry, uint EnchantId, float Chance);

/// <summary>
/// The random property content (immutable): the ItemRandomProperties.dbc rows and the <c>item_enchantment_template</c> groups. Rows whose chance
/// is outside (0.000001, 100] are dropped at build, as vmangos does.
/// </summary>
public sealed class ItemRandomPropertyCatalog
{
    private readonly Dictionary<uint, ItemRandomPropertyRecord> _properties;
    private readonly Dictionary<uint, ItemEnchantmentChance[]> _groups;

    public ItemRandomPropertyCatalog(IEnumerable<ItemRandomPropertyRecord> properties, IEnumerable<ItemEnchantmentChance> chances)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(chances);
        _properties = [];
        foreach (ItemRandomPropertyRecord property in properties)
        {
            _properties[property.Id] = property;
        }

        _groups = chances.Where(c => c.Chance > 0.000001f && c.Chance <= 100f).GroupBy(c => c.Entry).ToDictionary(g => g.Key, g => g.ToArray());
    }

    public static ItemRandomPropertyCatalog Empty { get; } = new([], []);

    public int PropertyCount => _properties.Count;

    public int GroupCount => _groups.Count;

    public ItemRandomPropertyRecord? FindProperty(uint id) => _properties.GetValueOrDefault(id);

    /// <summary>The rows of one <c>item_enchantment_template</c> entry (empty when the entry has none).</summary>
    public IReadOnlyList<ItemEnchantmentChance> Group(uint entry) => _groups.TryGetValue(entry, out ItemEnchantmentChance[]? rows) ? rows : [];
}
