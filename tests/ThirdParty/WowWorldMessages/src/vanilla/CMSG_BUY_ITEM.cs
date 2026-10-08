using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_BUY_ITEM: VanillaClientMessage, IWorldMessage {
    public required ulong Vendor { get; set; }
    public required uint Item { get; set; }
    public required byte Amount { get; set; }
    /// <summary>
    /// cmangos says this is hardcoded to 1 in the TBC client.
    /// </summary>
    public required byte Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Vendor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Amount, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 18, 418, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 18, 418, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_BUY_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var vendor = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var amount = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_BUY_ITEM {
            Vendor = vendor,
            Item = item,
            Amount = amount,
            Unknown1 = unknown1,
        };
    }

}

