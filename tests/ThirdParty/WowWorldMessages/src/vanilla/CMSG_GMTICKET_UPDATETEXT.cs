using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GMTICKET_UPDATETEXT: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// cmangos does not have this field, vmangos does.
    /// </summary>
    public required Vanilla.GmTicketType TicketType { get; set; }
    public required string Message { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)TicketType, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Message, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 519, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 519, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GMTICKET_UPDATETEXT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var ticketType = (Vanilla.GmTicketType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var message = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GMTICKET_UPDATETEXT {
            TicketType = ticketType,
            Message = message,
        };
    }

    internal int Size() {
        var size = 0;

        // ticket_type: Generator.Generated.DataTypeEnum
        size += 1;

        // message: Generator.Generated.DataTypeCstring
        size += Message.Length + 1;

        return size;
    }

}

