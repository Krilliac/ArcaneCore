using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_QUESTUPDATE_ADD_ITEM: VanillaServerMessage, IWorldMessage {
    public required uint RequiredItemId { get; set; }
    public required uint ItemsRequired { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(RequiredItemId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemsRequired, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 10, 410, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 10, 410, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_QUESTUPDATE_ADD_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var requiredItemId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemsRequired = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_QUESTUPDATE_ADD_ITEM {
            RequiredItemId = requiredItemId,
            ItemsRequired = itemsRequired,
        };
    }

}

