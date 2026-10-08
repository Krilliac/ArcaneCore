using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class TransportInfo {
    public required ulong Guid { get; set; }
    public required Vector3d Position { get; set; }
    public required float Orientation { get; set; }
    public required uint Timestamp { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Guid, cancellationToken).ConfigureAwait(false);

        await Position.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Orientation, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Timestamp, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<TransportInfo> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var position = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        var orientation = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var timestamp = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new TransportInfo {
            Guid = guid,
            Position = position,
            Orientation = orientation,
            Timestamp = timestamp,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypePackedGuid
        size += Guid.PackedGuidLength();

        // position: Generator.Generated.DataTypeStruct
        size += 12;

        // orientation: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        // timestamp: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

