namespace ArcaneCore.Kernel.WorldData.Items;

public sealed partial record ItemEnchantmentDefinition
{
    public ItemEnchantmentDefinition(uint entry, IEnumerable<ItemEnchantmentEffect> effects,
        IEnumerable<uint>? amountMax, uint nameFlags, uint itemVisualId, uint slotFlags)
    {
        Entry = entry;
        Effects = Array.AsReadOnly(effects?.Take(3).ToArray() ?? throw new ArgumentNullException(nameof(effects)));
        AmountMax = Array.AsReadOnly((amountMax ?? []).Take(3).ToArray());
        NameFlags = nameFlags;
        ItemVisualId = itemVisualId;
        SlotFlags = slotFlags;
    }

    public IReadOnlyList<uint> AmountMax { get; } = Array.AsReadOnly(Array.Empty<uint>());
    public uint NameFlags { get; }
    public uint ItemVisualId { get; }
    public uint SlotFlags { get; }

    public ItemEnchantmentDefinition WithEffects(IEnumerable<ItemEnchantmentEffect> effects)
        => new(Entry, effects, AmountMax, NameFlags, ItemVisualId, SlotFlags);
}
