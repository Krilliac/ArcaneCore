using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_SWAP_ITEM: VanillaClientMessage, IWorldMessage {
    public required byte DestinationBag { get; set; }
    public required byte DestionationSlot { get; set; }
    public required byte SourceBag { get; set; }
    public required byte SourceSlot { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(DestinationBag, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(DestionationSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(SourceBag, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(SourceSlot, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 8, 268, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 8, 268, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_SWAP_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var destinationBag = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var destionationSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var sourceBag = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var sourceSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_SWAP_ITEM {
            DestinationBag = destinationBag,
            DestionationSlot = destionationSlot,
            SourceBag = sourceBag,
            SourceSlot = sourceSlot,
        };
    }

}

