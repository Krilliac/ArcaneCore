using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class AddonInfo {
    public required string AddonName { get; set; }
    public required byte AddonHasSignature { get; set; }
    public required uint AddonCrc { get; set; }
    public required uint AddonExtraCrc { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(AddonName, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(AddonHasSignature, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AddonCrc, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AddonExtraCrc, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<AddonInfo> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var addonName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var addonHasSignature = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var addonCrc = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var addonExtraCrc = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new AddonInfo {
            AddonName = addonName,
            AddonHasSignature = addonHasSignature,
            AddonCrc = addonCrc,
            AddonExtraCrc = addonExtraCrc,
        };
    }

    internal int Size() {
        var size = 0;

        // addon_name: Generator.Generated.DataTypeCstring
        size += AddonName.Length + 1;

        // addon_has_signature: Generator.Generated.DataTypeInteger
        size += 1;

        // addon_crc: Generator.Generated.DataTypeInteger
        size += 4;

        // addon_extra_crc: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

