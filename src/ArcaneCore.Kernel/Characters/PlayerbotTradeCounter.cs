namespace ArcaneCore.Kernel.Characters;

/// <summary>What one <see cref="PlayerbotTradeCounter"/> row counts.</summary>
public enum PlayerbotTradeCounterKind : byte
{
    /// <summary>Settled desk deals of one bot inside the sliding hour: <c>Stamp</c> is the Unix-millisecond time, <c>Amount</c> the deals at that time.</summary>
    BotTrades = 0,

    /// <summary>Settled desk deals with one partner account inside the sliding hour: <c>Key</c> is the account id.</summary>
    AccountTrades = 1,

    /// <summary>Copper one bot paid out through the desk on one UTC day: <c>Stamp</c> is the day number (Unix ms / 86400000).</summary>
    BotOutflow = 2,

    /// <summary>Copper paid out to one partner account on one UTC day: <c>Key</c> is the account id.</summary>
    AccountOutflow = 3,

    /// <summary>Copper all bots paid out through the desk on one UTC day.</summary>
    ServerOutflow = 4,

    /// <summary>Gifts one bot received from one partner inside the sliding hour: <c>Key</c> is the partner's guid value, <c>Stamp</c> the Unix-millisecond time.</summary>
    GiftsReceived = 5,
}

/// <summary>
/// One persisted playerbot trade counter row (the hourly trade counts, the daily outflow totals and the hourly received-gift counts survive a restart). Counts and times
/// only. The identity is (<paramref name="Kind"/>, <paramref name="BotId"/>, <paramref name="Key"/>, <paramref name="Stamp"/>); a field a kind does not use is
/// <see cref="Guid.Empty"/> or 0.
/// </summary>
public sealed record PlayerbotTradeCounter(PlayerbotTradeCounterKind Kind, Guid BotId, ulong Key, long Stamp, long Amount)
{
    /// <summary>Rows one snapshot may hold.</summary>
    public const int MaxRows = 65_536;
}

public interface IPlayerbotTradeCounterStore
{
    Task<IReadOnlyList<PlayerbotTradeCounter>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically replace every stored counter row with <paramref name="rows"/> (an empty list clears the table).</summary>
    Task ReplaceAllAsync(IReadOnlyList<PlayerbotTradeCounter> rows, CancellationToken cancellationToken = default);
}
