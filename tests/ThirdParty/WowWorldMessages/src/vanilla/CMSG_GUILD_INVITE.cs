using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GUILD_INVITE: VanillaClientMessage, IWorldMessage {
    public required string InvitedPlayer { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(InvitedPlayer, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 130, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 130, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GUILD_INVITE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var invitedPlayer = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GUILD_INVITE {
            InvitedPlayer = invitedPlayer,
        };
    }

    internal int Size() {
        var size = 0;

        // invited_player: Generator.Generated.DataTypeCstring
        size += InvitedPlayer.Length + 1;

        return size;
    }

}

