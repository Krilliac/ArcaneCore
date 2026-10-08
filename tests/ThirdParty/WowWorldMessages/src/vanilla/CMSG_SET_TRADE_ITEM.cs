using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_SET_TRADE_ITEM: VanillaClientMessage, IWorldMessage {
    public required byte TradeSlot { get; set; }
    public required byte Bag { get; set; }
    public required byte Slot { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(TradeSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Bag, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Slot, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 7, 285, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 7, 285, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_SET_TRADE_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var tradeSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var bag = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var slot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_SET_TRADE_ITEM {
            TradeSlot = tradeSlot,
            Bag = bag,
            Slot = slot,
        };
    }

}

