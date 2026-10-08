using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_ACCEPT_TRADE: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// Skipped in vmangos and set to 1 for bots
    /// </summary>
    public required uint Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 8, 282, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 8, 282, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_ACCEPT_TRADE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_ACCEPT_TRADE {
            Unknown1 = unknown1,
        };
    }

}

