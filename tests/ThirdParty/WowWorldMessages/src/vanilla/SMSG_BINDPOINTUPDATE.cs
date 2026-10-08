using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_BINDPOINTUPDATE: VanillaServerMessage, IWorldMessage {
    public required Vector3d Position { get; set; }
    public required Vanilla.Map Map { get; set; }
    public required Vanilla.Area Area { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await Position.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Area, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 22, 341, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 22, 341, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_BINDPOINTUPDATE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var position = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_BINDPOINTUPDATE {
            Position = position,
            Map = map,
            Area = area,
        };
    }

}

