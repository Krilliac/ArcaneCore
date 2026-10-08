using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PAGE_TEXT_QUERY_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required uint PageId { get; set; }
    public required string Text { get; set; }
    public required uint NextPageId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(PageId, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Text, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(NextPageId, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 91, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 91, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PAGE_TEXT_QUERY_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var pageId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var text = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var nextPageId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_PAGE_TEXT_QUERY_RESPONSE {
            PageId = pageId,
            Text = text,
            NextPageId = nextPageId,
        };
    }

    internal int Size() {
        var size = 0;

        // page_id: Generator.Generated.DataTypeInteger
        size += 4;

        // text: Generator.Generated.DataTypeCstring
        size += Text.Length + 1;

        // next_page_id: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

