using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class ListInventoryItem {
    public required uint ItemStackCount { get; set; }
    public required uint Item { get; set; }
    public required uint ItemDisplayId { get; set; }
    /// <summary>
    /// cmangos: 0 for infinity item amount, although they send 0xFFFFFFFF in that case
    /// </summary>
    public required uint MaxItems { get; set; }
    public required uint Price { get; set; }
    public required uint MaxDurability { get; set; }
    public required uint Durability { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(ItemStackCount, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemDisplayId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MaxItems, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Price, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MaxDurability, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Durability, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<ListInventoryItem> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var itemStackCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemDisplayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var maxItems = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var price = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var maxDurability = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var durability = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new ListInventoryItem {
            ItemStackCount = itemStackCount,
            Item = item,
            ItemDisplayId = itemDisplayId,
            MaxItems = maxItems,
            Price = price,
            MaxDurability = maxDurability,
            Durability = durability,
        };
    }

}

