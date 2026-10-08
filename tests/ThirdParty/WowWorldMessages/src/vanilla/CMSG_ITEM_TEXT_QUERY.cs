using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_ITEM_TEXT_QUERY: VanillaClientMessage, IWorldMessage {
    public required uint ItemTextId { get; set; }
    /// <summary>
    /// vmangos/cmangos/mangoszero: this value can be item id in bag, but it is also mail id
    /// </summary>
    public required uint MailId { get; set; }
    /// <summary>
    /// vmangos/cmangos/mangoszero: maybe something like state - 0x70000000
    /// </summary>
    public required uint Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(ItemTextId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MailId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 16, 579, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 16, 579, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_ITEM_TEXT_QUERY> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var itemTextId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var mailId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_ITEM_TEXT_QUERY {
            ItemTextId = itemTextId,
            MailId = mailId,
            Unknown1 = unknown1,
        };
    }

}

