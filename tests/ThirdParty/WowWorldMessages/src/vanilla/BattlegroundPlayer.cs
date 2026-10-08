using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class BattlegroundPlayer {
    public required ulong Player { get; set; }
    public required Vanilla.PvpRank Rank { get; set; }
    public required uint KillingBlows { get; set; }
    public required uint HonorableKills { get; set; }
    public required uint Deaths { get; set; }
    public required uint BonusHonor { get; set; }
    /// <summary>
    /// This depends on the BG in question. AV expects 7: Graveyards Assaulted, Graveyards Defended, Towers Assaulted, Towers Defended, Secondary Objectives, LieutenantCount, SecondaryNpc
    /// WSG expects 2: Flag captures and flag returns
    /// AB expects 2: Bases assaulted and bases defended
    /// </summary>
    public required List<uint> Fields { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Player, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Rank, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(KillingBlows, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(HonorableKills, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Deaths, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(BonusHonor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Fields.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Fields) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public static async Task<BattlegroundPlayer> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var player = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var rank = (Vanilla.PvpRank)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var killingBlows = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var honorableKills = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var deaths = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var bonusHonor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfExtraFields = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var fields = new List<uint>();
        for (var i = 0; i < amountOfExtraFields; ++i) {
            fields.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
        }

        return new BattlegroundPlayer {
            Player = player,
            Rank = rank,
            KillingBlows = killingBlows,
            HonorableKills = honorableKills,
            Deaths = deaths,
            BonusHonor = bonusHonor,
            Fields = fields,
        };
    }

    internal int Size() {
        var size = 0;

        // player: Generator.Generated.DataTypeGuid
        size += 8;

        // rank: Generator.Generated.DataTypeEnum
        size += 4;

        // killing_blows: Generator.Generated.DataTypeInteger
        size += 4;

        // honorable_kills: Generator.Generated.DataTypeInteger
        size += 4;

        // deaths: Generator.Generated.DataTypeInteger
        size += 4;

        // bonus_honor: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_extra_fields: Generator.Generated.DataTypeInteger
        size += 4;

        // fields: Generator.Generated.DataTypeArray
        size += Fields.Sum(e => 4);

        return size;
    }

}

