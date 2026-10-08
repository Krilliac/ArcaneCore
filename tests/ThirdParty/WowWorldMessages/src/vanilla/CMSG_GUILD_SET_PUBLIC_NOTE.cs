using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GUILD_SET_PUBLIC_NOTE: VanillaClientMessage, IWorldMessage {
    public required string PlayerName { get; set; }
    public required string Note { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(PlayerName, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Note, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 564, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 564, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GUILD_SET_PUBLIC_NOTE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var playerName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var note = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GUILD_SET_PUBLIC_NOTE {
            PlayerName = playerName,
            Note = note,
        };
    }

    internal int Size() {
        var size = 0;

        // player_name: Generator.Generated.DataTypeCstring
        size += PlayerName.Length + 1;

        // note: Generator.Generated.DataTypeCstring
        size += Note.Length + 1;

        return size;
    }

}

