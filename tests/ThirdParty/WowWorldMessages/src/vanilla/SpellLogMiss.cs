using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class SpellLogMiss {
    public required ulong Target { get; set; }
    public required Vanilla.SpellMissInfo MissInfo { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Target, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)MissInfo, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<SpellLogMiss> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var target = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var missInfo = (Vanilla.SpellMissInfo)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SpellLogMiss {
            Target = target,
            MissInfo = missInfo,
        };
    }

}

