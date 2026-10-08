using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TUTORIAL_FLAGS: VanillaServerMessage, IWorldMessage {
    public const int TutorialDataLength = 8;
    public required uint[] TutorialData { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        foreach (var v in TutorialData) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 34, 253, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 34, 253, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TUTORIAL_FLAGS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var tutorialData = new uint[TutorialDataLength];
        for (var i = 0; i < TutorialDataLength; ++i) {
            tutorialData[i] = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        }

        return new SMSG_TUTORIAL_FLAGS {
            TutorialData = tutorialData,
        };
    }

}

