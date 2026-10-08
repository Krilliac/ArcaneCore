using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GMTICKET_SYSTEMSTATUS: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// vmangos: This only disables the ticket UI at client side and is not fully reliable are we sure this is a uint32? Should ask Zor
    /// </summary>
    public required Vanilla.GmTicketQueueStatus WillAcceptTickets { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)WillAcceptTickets, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 6, 539, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 6, 539, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GMTICKET_SYSTEMSTATUS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var willAcceptTickets = (Vanilla.GmTicketQueueStatus)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_GMTICKET_SYSTEMSTATUS {
            WillAcceptTickets = willAcceptTickets,
        };
    }

}

