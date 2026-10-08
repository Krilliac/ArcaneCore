using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_PING: VanillaClientMessage, IWorldMessage {
    public required uint SequenceId { get; set; }
    public required uint RoundTimeInMs { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(SequenceId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RoundTimeInMs, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 12, 476, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 12, 476, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_PING> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var sequenceId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var roundTimeInMs = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_PING {
            SequenceId = sequenceId,
            RoundTimeInMs = roundTimeInMs,
        };
    }

}

