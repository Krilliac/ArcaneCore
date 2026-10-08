using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_TALENT_WIPE_CONFIRM_Client: VanillaClientMessage, IWorldMessage {
    public required ulong WipingNpc { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(WipingNpc, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 12, 682, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 12, 682, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_TALENT_WIPE_CONFIRM_Client> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var wipingNpc = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new MSG_TALENT_WIPE_CONFIRM_Client {
            WipingNpc = wipingNpc,
        };
    }

}

