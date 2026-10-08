using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_SET_ACTIONBAR_TOGGLES: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// Emulators set PLAYER_FIELD_BYTES[2] to this unless it's 0.
    /// </summary>
    public required byte ActionBar { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(ActionBar, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 5, 703, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 5, 703, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_SET_ACTIONBAR_TOGGLES> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var actionBar = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_SET_ACTIONBAR_TOGGLES {
            ActionBar = actionBar,
        };
    }

}

