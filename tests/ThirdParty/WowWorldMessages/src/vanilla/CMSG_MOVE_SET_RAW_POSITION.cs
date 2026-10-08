using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_MOVE_SET_RAW_POSITION: VanillaClientMessage, IWorldMessage {
    public required Vector3d Position { get; set; }
    public required float Orientation { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await Position.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Orientation, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 20, 225, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 20, 225, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_MOVE_SET_RAW_POSITION> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var position = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        var orientation = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new CMSG_MOVE_SET_RAW_POSITION {
            Position = position,
            Orientation = orientation,
        };
    }

}

