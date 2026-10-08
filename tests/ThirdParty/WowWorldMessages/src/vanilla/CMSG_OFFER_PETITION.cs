using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_OFFER_PETITION: VanillaClientMessage, IWorldMessage {
    public required ulong Petition { get; set; }
    public required ulong Target { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Petition, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Target, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 20, 451, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 20, 451, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_OFFER_PETITION> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var petition = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var target = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new CMSG_OFFER_PETITION {
            Petition = petition,
            Target = target,
        };
    }

}

