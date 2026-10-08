using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ITEM_PUSH_RESULT: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required Vanilla.NewItemSource Source { get; set; }
    public required Vanilla.NewItemCreationType CreationType { get; set; }
    public required Vanilla.NewItemChatAlert AlertChat { get; set; }
    public required byte BagSlot { get; set; }
    /// <summary>
    /// mangoszero: item slot, but when added to stack: 0xFFFFFFFF
    /// </summary>
    public required uint ItemSlot { get; set; }
    public required uint Item { get; set; }
    /// <summary>
    /// mangoszero: SuffixFactor
    /// </summary>
    public required uint ItemSuffixFactor { get; set; }
    /// <summary>
    /// mangoszero: random item property id
    /// </summary>
    public required uint ItemRandomPropertyId { get; set; }
    public required uint ItemCount { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Source, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)CreationType, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)AlertChat, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(BagSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemSuffixFactor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomPropertyId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemCount, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 43, 358, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 43, 358, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ITEM_PUSH_RESULT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var source = (Vanilla.NewItemSource)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var creationType = (Vanilla.NewItemCreationType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var alertChat = (Vanilla.NewItemChatAlert)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var bagSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var itemSlot = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemSuffixFactor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomPropertyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_ITEM_PUSH_RESULT {
            Guid = guid,
            Source = source,
            CreationType = creationType,
            AlertChat = alertChat,
            BagSlot = bagSlot,
            ItemSlot = itemSlot,
            Item = item,
            ItemSuffixFactor = itemSuffixFactor,
            ItemRandomPropertyId = itemRandomPropertyId,
            ItemCount = itemCount,
        };
    }

}

