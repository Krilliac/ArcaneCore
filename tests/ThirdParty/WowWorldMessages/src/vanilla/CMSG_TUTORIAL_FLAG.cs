using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_TUTORIAL_FLAG: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// arcemu indexes into the tutorials by dividing by 32 and modulo 32.
    /// </summary>
    public required uint TutorialFlag { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(TutorialFlag, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 8, 254, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 8, 254, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_TUTORIAL_FLAG> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var tutorialFlag = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_TUTORIAL_FLAG {
            TutorialFlag = tutorialFlag,
        };
    }

}

