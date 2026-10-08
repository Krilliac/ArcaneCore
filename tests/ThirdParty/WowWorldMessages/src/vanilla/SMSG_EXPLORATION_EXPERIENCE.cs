using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_EXPLORATION_EXPERIENCE: VanillaServerMessage, IWorldMessage {
    public required Vanilla.Area Area { get; set; }
    public required uint Experience { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Area, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Experience, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 10, 504, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 10, 504, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_EXPLORATION_EXPERIENCE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var experience = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_EXPLORATION_EXPERIENCE {
            Area = area,
            Experience = experience,
        };
    }

}

