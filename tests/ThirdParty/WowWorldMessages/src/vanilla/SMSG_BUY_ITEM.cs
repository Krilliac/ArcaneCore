using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_BUY_ITEM: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    /// <summary>
    /// Starts at index 1.
    /// arcemu has this field as milliseconds since something instead.
    /// </summary>
    public required uint VendorSlot { get; set; }
    public required uint AmountForSale { get; set; }
    public required uint AmountBought { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(VendorSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AmountForSale, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AmountBought, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 22, 420, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 22, 420, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_BUY_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var vendorSlot = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var amountForSale = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var amountBought = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_BUY_ITEM {
            Guid = guid,
            VendorSlot = vendorSlot,
            AmountForSale = amountForSale,
            AmountBought = amountBought,
        };
    }

}

