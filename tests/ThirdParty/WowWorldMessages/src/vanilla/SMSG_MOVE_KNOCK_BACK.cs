using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_MOVE_KNOCK_BACK: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    /// <summary>
    /// mangoszero: Sequence
    /// mangoszero sets to 0
    /// </summary>
    public required uint MovementCounter { get; set; }
    /// <summary>
    /// cmangos/mangoszero/vmangos: x direction
    /// </summary>
    public required float VCos { get; set; }
    /// <summary>
    /// cmangos/mangoszero/vmangos: y direction
    /// </summary>
    public required float VSin { get; set; }
    /// <summary>
    /// cmangos/mangoszero/vmangos: Horizontal speed
    /// </summary>
    public required float HorizontalSpeed { get; set; }
    /// <summary>
    /// cmangos/mangoszero/vmangos: Z Movement speed (vertical)
    /// </summary>
    public required float VerticalSpeed { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MovementCounter, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(VCos, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(VSin, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(HorizontalSpeed, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(VerticalSpeed, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 239, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 239, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_MOVE_KNOCK_BACK> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var movementCounter = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var vCos = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var vSin = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var horizontalSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var verticalSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new SMSG_MOVE_KNOCK_BACK {
            Guid = guid,
            MovementCounter = movementCounter,
            VCos = vCos,
            VSin = vSin,
            HorizontalSpeed = horizontalSpeed,
            VerticalSpeed = verticalSpeed,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypePackedGuid
        size += Guid.PackedGuidLength();

        // movement_counter: Generator.Generated.DataTypeInteger
        size += 4;

        // v_cos: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        // v_sin: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        // horizontal_speed: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        // vertical_speed: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        return size;
    }

}

