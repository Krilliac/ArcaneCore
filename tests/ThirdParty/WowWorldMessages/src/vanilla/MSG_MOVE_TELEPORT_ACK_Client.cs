using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_MOVE_TELEPORT_ACK_Client: VanillaClientMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint MovementCounter { get; set; }
    public required uint Time { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MovementCounter, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Time, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 199, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 199, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_MOVE_TELEPORT_ACK_Client> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var movementCounter = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var time = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new MSG_MOVE_TELEPORT_ACK_Client {
            Guid = guid,
            MovementCounter = movementCounter,
            Time = time,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypePackedGuid
        size += Guid.PackedGuidLength();

        // movement_counter: Generator.Generated.DataTypeInteger
        size += 4;

        // time: Generator.Generated.DataTypeMilliseconds
        size += 4;

        return size;
    }

}

