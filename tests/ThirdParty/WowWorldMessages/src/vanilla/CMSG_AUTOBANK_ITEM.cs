using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_AUTOBANK_ITEM: VanillaClientMessage, IWorldMessage {
    public required byte BagIndex { get; set; }
    public required byte SlotIndex { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(BagIndex, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(SlotIndex, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 6, 643, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 6, 643, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_AUTOBANK_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var bagIndex = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var slotIndex = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_AUTOBANK_ITEM {
            BagIndex = bagIndex,
            SlotIndex = slotIndex,
        };
    }

}

