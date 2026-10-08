using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PET_ACTION_FEEDBACK: VanillaServerMessage, IWorldMessage {
    public required Vanilla.PetFeedback Feedback { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Feedback, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 3, 710, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 3, 710, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PET_ACTION_FEEDBACK> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var feedback = (Vanilla.PetFeedback)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_PET_ACTION_FEEDBACK {
            Feedback = feedback,
        };
    }

}

