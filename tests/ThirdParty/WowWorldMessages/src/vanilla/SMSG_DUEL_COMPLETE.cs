using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_DUEL_COMPLETE: VanillaServerMessage, IWorldMessage {
    public required bool EndedWithoutInterruption { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteBool8(EndedWithoutInterruption, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 3, 362, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 3, 362, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_DUEL_COMPLETE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var endedWithoutInterruption = await r.ReadBool8(cancellationToken).ConfigureAwait(false);

        return new SMSG_DUEL_COMPLETE {
            EndedWithoutInterruption = endedWithoutInterruption,
        };
    }

}

