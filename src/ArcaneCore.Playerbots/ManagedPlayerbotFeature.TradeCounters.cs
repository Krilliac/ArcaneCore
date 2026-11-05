using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

public sealed partial class ManagedPlayerbotFeature
{
    private const string HandoverTradeCounters = "playerbots.tradeCounters";

    private BotSocial.PlayerbotGiftBook? _giftBook;

    // One writer at a time; each takes its snapshot inside the gate, so the last writer holds the newest counters.
    private readonly SemaphoreSlim _tradeCounterGate = new(1, 1);
    private long _savedDeskCounterVersion = -1;
    private long _savedGiftVersion = -1;

    /// <summary>The gifts every bot received inside the last hour, shared by the bots' party AIs and saved with the desk's counters. Any thread (the book has its own lock).</summary>
    internal BotSocial.PlayerbotGiftBook GiftBook => _giftBook ?? CreateGiftBook();

    private BotSocial.PlayerbotGiftBook CreateGiftBook()
    {
        var created = new BotSocial.PlayerbotGiftBook();
        return Interlocked.CompareExchange(ref _giftBook, created, null) ?? created;
    }

    /// <summary>The desk's counters and the gift book as rows (the save and the handover), bounded to <see cref="PlayerbotTradeCounter.MaxRows"/>.</summary>
    private PlayerbotTradeCounter[] ExportTradeCounters()
    {
        long now = _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds();
        return [.. TradeDesk.ExportCounters().Concat(GiftBook.Export(now)).Take(PlayerbotTradeCounter.MaxRows)];
    }

    private void ImportTradeCounters(IReadOnlyList<PlayerbotTradeCounter> rows)
    {
        TradeDesk.ImportCounters(rows);
        GiftBook.Import(rows, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
        Volatile.Write(ref _savedDeskCounterVersion, TradeDesk.CounterVersion);
        Volatile.Write(ref _savedGiftVersion, GiftBook.Version);
    }

    /// <summary>Load the stored trade counters (startup). A failure is recorded at <c>playerbot.social-memory</c>; the desk then starts with empty counters.</summary>
    private async Task LoadTradeCountersAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        if (provider.GetService<IPlayerbotTradeCounterStore>() is not { } store) return;
        try { ImportTradeCounters(await store.LoadAllAsync(cancellationToken).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
            logger.LogWarning(ex, "Playerbot trade counters could not be loaded ({Type}); the desk starts with none", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Write the trade counters when the desk's or the gift book's version moved since the last write. Off the world thread: both are thread-safe and the rows are a copy.
    /// A failure is recorded at <c>playerbot.social-memory</c> and logged; it never blocks a stop or a checkpoint, and the next call tries again (the saved versions are
    /// only recorded after the write).
    /// </summary>
    private async Task SaveTradeCountersAsync()
    {
        try
        {
            if (Volatile.Read(ref _savedDeskCounterVersion) == TradeDesk.CounterVersion && Volatile.Read(ref _savedGiftVersion) == GiftBook.Version) return;
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IPlayerbotTradeCounterStore>() is not { } store) return;
            await _tradeCounterGate.WaitAsync().ConfigureAwait(false);
            try
            {
                long deskVersion = TradeDesk.CounterVersion; // before the snapshot: the saved version never runs ahead of the rows
                long giftVersion = GiftBook.Version;
                if (Volatile.Read(ref _savedDeskCounterVersion) == deskVersion && Volatile.Read(ref _savedGiftVersion) == giftVersion) return;
                PlayerbotTradeCounter[] rows = ExportTradeCounters();
                await RetryTransientBusyAsync(() => store.ReplaceAllAsync(rows), CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _savedDeskCounterVersion, deskVersion);
                Volatile.Write(ref _savedGiftVersion, giftVersion);
            }
            finally { _tradeCounterGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
            logger.LogWarning(ex, "Playerbot trade counters save failed ({Type})", ex.GetType().Name);
        }
    }
}
