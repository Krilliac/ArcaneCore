using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PETITION_SIGN_RESULTS: VanillaServerMessage, IWorldMessage {
    public required ulong Petition { get; set; }
    public required ulong Owner { get; set; }
    public required Vanilla.PetitionResult Result { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Petition, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Owner, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Result, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 22, 449, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 22, 449, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PETITION_SIGN_RESULTS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var petition = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var owner = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var result = (Vanilla.PetitionResult)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_PETITION_SIGN_RESULTS {
            Petition = petition,
            Owner = owner,
            Result = result,
        };
    }

}

