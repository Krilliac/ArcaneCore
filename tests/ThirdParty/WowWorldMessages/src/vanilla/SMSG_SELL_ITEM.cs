using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SELL_ITEM: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required ulong Item { get; set; }
    public required Vanilla.SellItemResult Result { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Result, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 19, 417, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 19, 417, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SELL_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var result = (Vanilla.SellItemResult)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_SELL_ITEM {
            Guid = guid,
            Item = item,
            Result = result,
        };
    }

}

