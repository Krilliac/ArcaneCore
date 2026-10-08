using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SERVER_MESSAGE: VanillaServerMessage, IWorldMessage {
    public required Vanilla.ServerMessageType MessageType { get; set; }
    public required string Message { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)MessageType, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Message, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 657, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 657, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SERVER_MESSAGE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var messageType = (Vanilla.ServerMessageType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var message = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_SERVER_MESSAGE {
            MessageType = messageType,
            Message = message,
        };
    }

    internal int Size() {
        var size = 0;

        // message_type: Generator.Generated.DataTypeEnum
        size += 4;

        // message: Generator.Generated.DataTypeCstring
        size += Message.Length + 1;

        return size;
    }

}

