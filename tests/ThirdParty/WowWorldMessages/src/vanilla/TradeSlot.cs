using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class TradeSlot {
    /// <summary>
    /// cmangos/vmangos/mangoszero: sets to index of array
    /// </summary>
    public required byte TradeSlotNumber { get; set; }
    public required uint Item { get; set; }
    public required uint DisplayId { get; set; }
    public required uint StackCount { get; set; }
    public required bool Wrapped { get; set; }
    public required ulong GiftWrapper { get; set; }
    public required uint Enchantment { get; set; }
    public required ulong ItemCreator { get; set; }
    public required uint SpellCharges { get; set; }
    public required uint ItemSuffixFactor { get; set; }
    public required uint ItemRandomPropertiesId { get; set; }
    public required uint LockId { get; set; }
    public required uint MaxDurability { get; set; }
    public required uint Durability { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(TradeSlotNumber, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(DisplayId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(StackCount, cancellationToken).ConfigureAwait(false);

        await w.WriteBool32(Wrapped, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(GiftWrapper, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Enchantment, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(ItemCreator, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SpellCharges, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemSuffixFactor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomPropertiesId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(LockId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MaxDurability, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Durability, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<TradeSlot> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var tradeSlotNumber = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var displayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var stackCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var wrapped = await r.ReadBool32(cancellationToken).ConfigureAwait(false);

        var giftWrapper = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var enchantment = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemCreator = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var spellCharges = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemSuffixFactor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomPropertiesId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var lockId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var maxDurability = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var durability = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new TradeSlot {
            TradeSlotNumber = tradeSlotNumber,
            Item = item,
            DisplayId = displayId,
            StackCount = stackCount,
            Wrapped = wrapped,
            GiftWrapper = giftWrapper,
            Enchantment = enchantment,
            ItemCreator = itemCreator,
            SpellCharges = spellCharges,
            ItemSuffixFactor = itemSuffixFactor,
            ItemRandomPropertiesId = itemRandomPropertiesId,
            LockId = lockId,
            MaxDurability = maxDurability,
            Durability = durability,
        };
    }

}

