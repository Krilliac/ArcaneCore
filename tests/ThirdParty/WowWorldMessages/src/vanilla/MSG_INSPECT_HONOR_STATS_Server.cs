using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_INSPECT_HONOR_STATS_Server: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required Vanilla.PvpRank HighestRank { get; set; }
    public required uint TodayHonorableAndDishonorable { get; set; }
    public required ushort YesterdayHonorable { get; set; }
    /// <summary>
    /// vmangos: Unknown (deprecated, yesterday dishonourable?)
    /// </summary>
    public required ushort Unknown1 { get; set; }
    public required ushort LastWeekHonorable { get; set; }
    /// <summary>
    /// vmangos: Unknown (deprecated, last week dishonourable?)
    /// </summary>
    public required ushort Unknown2 { get; set; }
    public required ushort ThisWeekHonorable { get; set; }
    /// <summary>
    /// vmangos: Unknown (deprecated, this week dishonourable?)
    /// </summary>
    public required ushort Unknown3 { get; set; }
    public required uint LifetimeHonorable { get; set; }
    public required uint LifetimeDishonorable { get; set; }
    public required uint YesterdayHonor { get; set; }
    public required uint LastWeekHonor { get; set; }
    public required uint ThisWeekHonor { get; set; }
    public required Vanilla.PvpRank LastWeekStanding { get; set; }
    public required byte RankProgressBar { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)HighestRank, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TodayHonorableAndDishonorable, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(YesterdayHonorable, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(LastWeekHonorable, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(Unknown2, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(ThisWeekHonorable, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(Unknown3, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(LifetimeHonorable, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(LifetimeDishonorable, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(YesterdayHonor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(LastWeekHonor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ThisWeekHonor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)LastWeekStanding, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(RankProgressBar, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 52, 726, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 52, 726, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_INSPECT_HONOR_STATS_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var highestRank = (Vanilla.PvpRank)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var todayHonorableAndDishonorable = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var yesterdayHonorable = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var lastWeekHonorable = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var unknown2 = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var thisWeekHonorable = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var unknown3 = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var lifetimeHonorable = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var lifetimeDishonorable = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var yesterdayHonor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var lastWeekHonor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var thisWeekHonor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var lastWeekStanding = (Vanilla.PvpRank)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var rankProgressBar = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new MSG_INSPECT_HONOR_STATS_Server {
            Guid = guid,
            HighestRank = highestRank,
            TodayHonorableAndDishonorable = todayHonorableAndDishonorable,
            YesterdayHonorable = yesterdayHonorable,
            Unknown1 = unknown1,
            LastWeekHonorable = lastWeekHonorable,
            Unknown2 = unknown2,
            ThisWeekHonorable = thisWeekHonorable,
            Unknown3 = unknown3,
            LifetimeHonorable = lifetimeHonorable,
            LifetimeDishonorable = lifetimeDishonorable,
            YesterdayHonor = yesterdayHonor,
            LastWeekHonor = lastWeekHonor,
            ThisWeekHonor = thisWeekHonor,
            LastWeekStanding = lastWeekStanding,
            RankProgressBar = rankProgressBar,
        };
    }

}

