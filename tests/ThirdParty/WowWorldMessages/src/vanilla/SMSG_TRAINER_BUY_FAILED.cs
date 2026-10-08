using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TRAINER_BUY_FAILED: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint Id { get; set; }
    public required Vanilla.TrainingFailureReason Error { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Error, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 18, 436, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 18, 436, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TRAINER_BUY_FAILED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var error = (Vanilla.TrainingFailureReason)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_TRAINER_BUY_FAILED {
            Guid = guid,
            Id = id,
            Error = error,
        };
    }

}

