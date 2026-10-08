using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_TALENT_WIPE_CONFIRM_Server: VanillaServerMessage, IWorldMessage {
    public required ulong WipingNpc { get; set; }
    public required uint CostInCopper { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(WipingNpc, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CostInCopper, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 14, 682, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 14, 682, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_TALENT_WIPE_CONFIRM_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var wipingNpc = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var costInCopper = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new MSG_TALENT_WIPE_CONFIRM_Server {
            WipingNpc = wipingNpc,
            CostInCopper = costInCopper,
        };
    }

}

