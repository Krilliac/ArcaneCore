using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class FactionStanding {
    public required Vanilla.Faction Faction { get; set; }
    public required uint Standing { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUShort((ushort)Faction, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Standing, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<FactionStanding> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var faction = (Vanilla.Faction)await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var standing = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new FactionStanding {
            Faction = faction,
            Standing = standing,
        };
    }

}

