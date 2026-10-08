using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_LEARN_TALENT: VanillaClientMessage, IWorldMessage {
    public required Vanilla.Talent Talent { get; set; }
    public required uint RequestedRank { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Talent, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RequestedRank, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 12, 593, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 12, 593, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_LEARN_TALENT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var talent = (Vanilla.Talent)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var requestedRank = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_LEARN_TALENT {
            Talent = talent,
            RequestedRank = requestedRank,
        };
    }

}

