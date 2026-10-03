namespace ArcaneCore.Kernel.Items;

/// <summary>One stack still in a container item's generated loot (vmangos <c>item_loot</c>: item_id, amount; the slot keeps the window numbering).</summary>
/// <param name="Slot">The slot the loot window shows it in (stable while the loot exists).</param>
/// <param name="ItemId">The item entry.</param>
/// <param name="Count">The stack size.</param>
/// <param name="IsQuest">A quest drop (negative chance): only the owner, and only while a quest needs it.</param>
public sealed record ItemLootEntry(byte Slot, uint ItemId, uint Count, bool IsQuest);

/// <summary>
/// The loot a container item (a lockbox, a clam) generated and the owner has not taken yet. Its presence on <see cref="ItemInstanceData.Loot"/> is
/// vmangos' <c>generated_loot</c> flag: the loot is rolled once and kept with the item until it is taken completely, so closing the window, logging
/// out or relogging never rerolls it.
/// </summary>
/// <param name="Gold">The money left in copper (vmangos stores it as the <c>item_loot</c> row with item_id 0).</param>
/// <param name="Items">The stacks left.</param>
public sealed record ItemLootData(uint Gold, IReadOnlyList<ItemLootEntry> Items)
{
    public bool Equals(ItemLootData? other)
        => other is not null && Gold == other.Gold && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Gold, Items.Count);
}