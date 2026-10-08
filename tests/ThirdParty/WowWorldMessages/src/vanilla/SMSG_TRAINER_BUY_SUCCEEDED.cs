using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TRAINER_BUY_SUCCEEDED: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint Id { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 14, 435, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 14, 435, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TRAINER_BUY_SUCCEEDED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_TRAINER_BUY_SUCCEEDED {
            Guid = guid,
            Id = id,
        };
    }

}

