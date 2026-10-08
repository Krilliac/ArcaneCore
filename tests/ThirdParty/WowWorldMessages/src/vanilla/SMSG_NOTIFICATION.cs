using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_NOTIFICATION: VanillaServerMessage, IWorldMessage {
    public required string Notification { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(Notification, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 459, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 459, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_NOTIFICATION> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var notification = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_NOTIFICATION {
            Notification = notification,
        };
    }

    internal int Size() {
        var size = 0;

        // notification: Generator.Generated.DataTypeCstring
        size += Notification.Length + 1;

        return size;
    }

}

