using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_AUCTION_REMOVED_NOTIFICATION: VanillaServerMessage, IWorldMessage {
    public required uint Item { get; set; }
    public required uint ItemTemplate { get; set; }
    public required uint RandomPropertyId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemTemplate, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RandomPropertyId, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 14, 653, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 14, 653, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_AUCTION_REMOVED_NOTIFICATION> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemTemplate = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var randomPropertyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_AUCTION_REMOVED_NOTIFICATION {
            Item = item,
            ItemTemplate = itemTemplate,
            RandomPropertyId = randomPropertyId,
        };
    }

}

