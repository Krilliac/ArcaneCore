using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GM_TICKET_STATUS_UPDATE: VanillaServerMessage, IWorldMessage {
    public required Vanilla.GmTicketStatusResponse Response { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Response, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 6, 808, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 6, 808, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GM_TICKET_STATUS_UPDATE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var response = (Vanilla.GmTicketStatusResponse)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_GM_TICKET_STATUS_UPDATE {
            Response = response,
        };
    }

}

