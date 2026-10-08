using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_PETITION_QUERY: VanillaClientMessage, IWorldMessage {
    public required uint GuildId { get; set; }
    public required ulong Petition { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(GuildId, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Petition, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 16, 454, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 16, 454, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_PETITION_QUERY> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guildId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var petition = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new CMSG_PETITION_QUERY {
            GuildId = guildId,
            Petition = petition,
        };
    }

}

