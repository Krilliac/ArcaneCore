using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class RaidTargetUpdate {
    public required Vanilla.RaidTargetIndex Index { get; set; }
    public required ulong Guid { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Index, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<RaidTargetUpdate> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var index = (Vanilla.RaidTargetIndex)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new RaidTargetUpdate {
            Index = index,
            Guid = guid,
        };
    }

}

