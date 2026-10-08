using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_SAVE_GUILD_EMBLEM_Client: VanillaClientMessage, IWorldMessage {
    public required ulong Vendor { get; set; }
    public required uint EmblemStyle { get; set; }
    public required uint EmblemColor { get; set; }
    public required uint BorderStyle { get; set; }
    public required uint BorderColor { get; set; }
    public required uint BackgroundColor { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Vendor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(EmblemStyle, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(EmblemColor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(BorderStyle, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(BorderColor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(BackgroundColor, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 32, 497, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 32, 497, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_SAVE_GUILD_EMBLEM_Client> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var vendor = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var emblemStyle = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emblemColor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var borderStyle = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var borderColor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var backgroundColor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new MSG_SAVE_GUILD_EMBLEM_Client {
            Vendor = vendor,
            EmblemStyle = emblemStyle,
            EmblemColor = emblemColor,
            BorderStyle = borderStyle,
            BorderColor = borderColor,
            BackgroundColor = backgroundColor,
        };
    }

}

