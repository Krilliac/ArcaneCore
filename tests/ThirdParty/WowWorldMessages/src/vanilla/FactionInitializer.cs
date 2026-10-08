using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class FactionInitializer {
    public required Vanilla.FactionFlag Flag { get; set; }
    public required uint Standing { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Flag, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Standing, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<FactionInitializer> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var flag = (FactionFlag)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var standing = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new FactionInitializer {
            Flag = flag,
            Standing = standing,
        };
    }

}

