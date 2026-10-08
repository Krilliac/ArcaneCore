using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_MINIMAP_PING_Client: VanillaClientMessage, IWorldMessage {
    public required float PositionX { get; set; }
    public required float PositionY { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteFloat(PositionX, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(PositionY, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 12, 469, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 12, 469, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_MINIMAP_PING_Client> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var positionX = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var positionY = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new MSG_MINIMAP_PING_Client {
            PositionX = positionX,
            PositionY = positionY,
        };
    }

}

