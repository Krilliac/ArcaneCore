namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// One managed playerbot's trade ledger row for another character: counts and times only, never items, prices or text
/// (docs/design: playerbot social P4, section 4.4). <paramref name="LastScamUnixMs"/> and <paramref name="LastNinjaUnixMs"/> are 0 for never.
/// </summary>
/// <param name="FairDeals">Deals that completed at the agreed terms (0..).</param>
/// <param name="Scams">Value-moving changes after the partner had matched a deal (0..).</param>
/// <param name="LastGiftDay">The UTC day number (unix ms / 86400000) of the last gift that counted, 0 for never.</param>
public sealed record PlayerbotTradeRecord(
    Guid BotId, int CharacterId, ulong OtherGuid,
    int FairDeals, int Scams, long LastScamUnixMs, long LastNinjaUnixMs, int LastGiftDay)
{
    /// <summary>Rows one bot may store (the same bound as its relations).</summary>
    public const int MaxRowsPerBot = 4096;
}

public interface IPlayerbotTradeRecordStore
{
    Task<IReadOnlyList<PlayerbotTradeRecord>> LoadAllAsync(CancellationToken cancellationToken = default);
    /// <summary>Atomically replace one bot's ledger rows.</summary>
    Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotTradeRecord> rows, CancellationToken cancellationToken = default);
}
