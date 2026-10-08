using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class PetitionShowlist {
    public required uint Index { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: statically sets to guild charter item id (5863) and arena charter ids.
    /// </summary>
    public required uint CharterEntry { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: statically sets to guild charter display id (16161) and arena charter ids.
    /// </summary>
    public required uint CharterDisplayId { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: statically set to 1000 (10 silver) for guild charters and the cost of arena charters for that.
    /// </summary>
    public required uint GuildCharterCost { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: statically set to 1
    /// </summary>
    public required uint Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Index, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CharterEntry, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CharterDisplayId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(GuildCharterCost, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<PetitionShowlist> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var index = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var charterEntry = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var charterDisplayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var guildCharterCost = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new PetitionShowlist {
            Index = index,
            CharterEntry = charterEntry,
            CharterDisplayId = charterDisplayId,
            GuildCharterCost = guildCharterCost,
            Unknown1 = unknown1,
        };
    }

}

