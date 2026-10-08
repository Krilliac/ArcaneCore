using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ITEM_TEXT_QUERY_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required uint ItemTextId { get; set; }
    /// <summary>
    /// mangoszero: CString TODO max length 8000
    /// </summary>
    public required string Text { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(ItemTextId, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Text, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 580, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 580, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ITEM_TEXT_QUERY_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var itemTextId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var text = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_ITEM_TEXT_QUERY_RESPONSE {
            ItemTextId = itemTextId,
            Text = text,
        };
    }

    internal int Size() {
        var size = 0;

        // item_text_id: Generator.Generated.DataTypeInteger
        size += 4;

        // text: Generator.Generated.DataTypeCstring
        size += Text.Length + 1;

        return size;
    }

}

