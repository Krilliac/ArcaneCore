using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_ACTIVATETAXI: VanillaClientMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint SourceNode { get; set; }
    public required uint DestinationNode { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SourceNode, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(DestinationNode, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 20, 429, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 20, 429, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_ACTIVATETAXI> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var sourceNode = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var destinationNode = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_ACTIVATETAXI {
            Guid = guid,
            SourceNode = sourceNode,
            DestinationNode = destinationNode,
        };
    }

}

