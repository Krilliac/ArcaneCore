using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TURN_IN_PETITION_RESULTS: VanillaServerMessage, IWorldMessage {
    public required Vanilla.PetitionResult Result { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Result, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 6, 453, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 6, 453, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TURN_IN_PETITION_RESULTS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var result = (Vanilla.PetitionResult)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_TURN_IN_PETITION_RESULTS {
            Result = result,
        };
    }

}

