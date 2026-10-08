using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_SPLIT_ITEM: VanillaClientMessage, IWorldMessage {
    public required byte SourceBag { get; set; }
    public required byte SourceSlot { get; set; }
    public required byte DestinationBag { get; set; }
    public required byte DestinationSlot { get; set; }
    public required byte Amount { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(SourceBag, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(SourceSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(DestinationBag, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(DestinationSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Amount, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 9, 270, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 9, 270, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_SPLIT_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var sourceBag = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var sourceSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var destinationBag = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var destinationSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var amount = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_SPLIT_ITEM {
            SourceBag = sourceBag,
            SourceSlot = sourceSlot,
            DestinationBag = destinationBag,
            DestinationSlot = destinationSlot,
            Amount = amount,
        };
    }

}

