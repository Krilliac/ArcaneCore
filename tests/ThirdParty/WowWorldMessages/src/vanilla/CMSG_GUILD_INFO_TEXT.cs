using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GUILD_INFO_TEXT: VanillaClientMessage, IWorldMessage {
    public required string GuildInfo { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(GuildInfo, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 764, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 764, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GUILD_INFO_TEXT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guildInfo = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GUILD_INFO_TEXT {
            GuildInfo = guildInfo,
        };
    }

    internal int Size() {
        var size = 0;

        // guild_info: Generator.Generated.DataTypeCstring
        size += GuildInfo.Length + 1;

        return size;
    }

}

