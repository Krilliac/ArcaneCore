using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_WRAP_ITEM: VanillaClientMessage, IWorldMessage {
    public required byte GiftBagIndex { get; set; }
    public required byte GiftSlot { get; set; }
    public required byte ItemBagIndex { get; set; }
    public required byte ItemSlot { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(GiftBagIndex, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(GiftSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(ItemBagIndex, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(ItemSlot, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 8, 467, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 8, 467, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_WRAP_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var giftBagIndex = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var giftSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var itemBagIndex = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var itemSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_WRAP_ITEM {
            GiftBagIndex = giftBagIndex,
            GiftSlot = giftSlot,
            ItemBagIndex = itemBagIndex,
            ItemSlot = itemSlot,
        };
    }

}

