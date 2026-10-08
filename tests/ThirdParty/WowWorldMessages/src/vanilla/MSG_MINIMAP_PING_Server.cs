using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_MINIMAP_PING_Server: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required float PositionX { get; set; }
    public required float PositionY { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(PositionX, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(PositionY, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 18, 469, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 18, 469, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_MINIMAP_PING_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var positionX = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var positionY = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new MSG_MINIMAP_PING_Server {
            Guid = guid,
            PositionX = positionX,
            PositionY = positionY,
        };
    }

}

