using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TRANSFER_ABORTED: VanillaServerMessage, IWorldMessage {
    public required Vanilla.Map Map { get; set; }
    public required Vanilla.TransferAbortReason Reason { get; set; }
    /// <summary>
    /// Possibly not needed.
    /// </summary>
    public required byte Argument { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Reason, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Argument, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 8, 64, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 8, 64, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TRANSFER_ABORTED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var reason = (Vanilla.TransferAbortReason)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var argument = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_TRANSFER_ABORTED {
            Map = map,
            Reason = reason,
            Argument = argument,
        };
    }

}

