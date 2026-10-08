using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_AREA_TRIGGER_MESSAGE: VanillaServerMessage, IWorldMessage {
    public required string Message { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteSizedCString(Message, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 696, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 696, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_AREA_TRIGGER_MESSAGE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var message = await r.ReadSizedCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_AREA_TRIGGER_MESSAGE {
            Message = message,
        };
    }

    internal int Size() {
        var size = 0;

        // message: Generator.Generated.DataTypeSizedCstring
        size += Message.Length + 5;

        return size;
    }

}

