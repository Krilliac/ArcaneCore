using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

public sealed partial class ManagedPlayerbotFeature
{
    private const string HandoverTradeRecords = "playerbots.tradeRecords";

    // One writer at a time for every bot's ledger rows: a save and a forget never replace the same bot's rows concurrently, and each takes its snapshot inside
    // the gate, so the last writer holds the newest rows. The ledger changes rarely (a completed trade), so one gate costs nothing.
    private readonly SemaphoreSlim _tradeLedgerGate = new(1, 1);

    /// <summary>Load every stored trade ledger row (startup). A failure is recorded at <c>playerbot.social-memory</c>; the bots then start with no ledger.</summary>
    private async Task LoadTradeLedgerAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        if (provider.GetService<IPlayerbotTradeRecordStore>() is not { } store || Social.Ledger is not { } ledger) return;
        try { ledger.Load(await store.LoadAllAsync(cancellationToken).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
            logger.LogWarning(ex, "Playerbot trade ledger could not be loaded ({Type}); the bots start with none", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Write the bot's trade ledger when it changed since the last write. Off the world thread: the ledger is thread-safe and the rows are a copy. A failure is
    /// recorded at <c>playerbot.social-memory</c> and logged; it never blocks a stop or a checkpoint, and the next call tries again (the saved version is only
    /// recorded after the write).
    /// </summary>
    private async Task SaveBotTradeLedgerAsync(ActiveBot active)
    {
        Guid botId = active.Record.BotId;
        try
        {
            if (Social.Ledger is not { } ledger || Volatile.Read(ref active.SavedTradeVersion) == ledger.Version(botId)) return;
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IPlayerbotTradeRecordStore>() is not { } store)
            {
                Volatile.Write(ref active.SavedTradeVersion, ledger.Version(botId));
                return;
            }

            await _tradeLedgerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                long version = ledger.Version(botId); // before the snapshot: the saved version never runs ahead of the rows
                IReadOnlyList<PlayerbotTradeRecord> rows = ledger.Snapshot(botId);
                if (version == 0 && rows.Count == 0)
                {
                    Volatile.Write(ref active.SavedTradeVersion, version); // never written to: nothing to replace (a restored bot would otherwise send one DELETE)
                    return;
                }

                await RetryTransientBusyAsync(() => store.ReplaceAsync(botId, rows), CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref active.SavedTradeVersion, version);
            }
            finally { _tradeLedgerGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
            logger.LogWarning(ex, "Playerbot {BotId} trade ledger save failed ({Type})", botId, ex.GetType().Name);
        }
    }

    /// <summary>Deleted bots: forget their ledgers in memory and in the store. Never throws: a failure is recorded at <c>playerbot.social-memory</c>.</summary>
    private async Task ForgetTradeLedgerAsync(IServiceProvider provider, IEnumerable<Guid> botIds)
    {
        try
        {
            if (Social.Ledger is not { } ledger) return;
            Guid[] gone = [.. botIds.Where(id => id != Guid.Empty)];
            foreach (Guid botId in gone) ledger.Forget(botId);
            if (provider.GetService<IPlayerbotTradeRecordStore>() is not { } store) return;
            await _tradeLedgerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (Guid botId in gone)
                    await RetryTransientBusyAsync(() => store.ReplaceAsync(botId, []), CancellationToken.None).ConfigureAwait(false);
            }
            finally { _tradeLedgerGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
            logger.LogWarning(ex, "Playerbot trade ledger of deleted bots could not be removed ({Type})", ex.GetType().Name);
        }
    }
}
