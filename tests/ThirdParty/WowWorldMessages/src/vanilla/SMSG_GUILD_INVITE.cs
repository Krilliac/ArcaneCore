using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GUILD_INVITE: VanillaServerMessage, IWorldMessage {
    public required string PlayerName { get; set; }
    public required string GuildName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(PlayerName, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(GuildName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 131, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 131, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GUILD_INVITE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var playerName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var guildName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_GUILD_INVITE {
            PlayerName = playerName,
            GuildName = guildName,
        };
    }

    internal int Size() {
        var size = 0;

        // player_name: Generator.Generated.DataTypeCstring
        size += PlayerName.Length + 1;

        // guild_name: Generator.Generated.DataTypeCstring
        size += GuildName.Length + 1;

        return size;
    }

}

