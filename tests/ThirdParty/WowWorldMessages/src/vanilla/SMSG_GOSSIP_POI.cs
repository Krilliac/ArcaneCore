using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GOSSIP_POI: VanillaServerMessage, IWorldMessage {
    public required uint Flags { get; set; }
    public required Vector2d Position { get; set; }
    public required uint Icon { get; set; }
    public required uint Data { get; set; }
    public required string LocationName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Flags, cancellationToken).ConfigureAwait(false);

        await Position.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Icon, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Data, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(LocationName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 548, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 548, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GOSSIP_POI> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var flags = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var position = await Vector2d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        var icon = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var data = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var locationName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_GOSSIP_POI {
            Flags = flags,
            Position = position,
            Icon = icon,
            Data = data,
            LocationName = locationName,
        };
    }

    internal int Size() {
        var size = 0;

        // flags: Generator.Generated.DataTypeInteger
        size += 4;

        // position: Generator.Generated.DataTypeStruct
        size += 8;

        // icon: Generator.Generated.DataTypeInteger
        size += 4;

        // data: Generator.Generated.DataTypeInteger
        size += 4;

        // location_name: Generator.Generated.DataTypeCstring
        size += LocationName.Length + 1;

        return size;
    }

}

