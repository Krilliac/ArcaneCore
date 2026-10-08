using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_AUTOSTORE_LOOT_ITEM: VanillaClientMessage, IWorldMessage {
    public required byte ItemSlot { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(ItemSlot, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 5, 264, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 5, 264, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_AUTOSTORE_LOOT_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var itemSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_AUTOSTORE_LOOT_ITEM {
            ItemSlot = itemSlot,
        };
    }

}

