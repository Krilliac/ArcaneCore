using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_FORCE_RUN_SPEED_CHANGE: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    /// <summary>
    /// cmangos/mangoszero/vmangos: set to 0
    /// cmangos/mangoszero/vmangos: moveEvent, NUM_PMOVE_EVTS = 0x39
    /// </summary>
    public required uint MoveEvent { get; set; }
    public required float Speed { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MoveEvent, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Speed, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 226, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 226, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_FORCE_RUN_SPEED_CHANGE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var moveEvent = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var speed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new SMSG_FORCE_RUN_SPEED_CHANGE {
            Guid = guid,
            MoveEvent = moveEvent,
            Speed = speed,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypePackedGuid
        size += Guid.PackedGuidLength();

        // move_event: Generator.Generated.DataTypeInteger
        size += 4;

        // speed: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        return size;
    }

}

