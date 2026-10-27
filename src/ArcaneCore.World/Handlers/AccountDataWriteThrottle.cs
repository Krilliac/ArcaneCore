using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// The per-account write throttle for account data blobs and tutorial flags (World:AccountDataWriteIntervalMs). The first change in an
/// interval is written at once; later ones are held, coalesced to the latest value per type, and written together when the interval
/// ends, so a client cannot turn its packet budget into one database write per packet and nothing it sent is lost. One throttle per
/// account and store (tests run several hosts, each with its own store).
/// </summary>
internal sealed class AccountDataWriteThrottle
{
    private static readonly ConditionalWeakTable<IAccountDataStore, ConcurrentDictionary<int, AccountDataWriteThrottle>> ByStore = new();

    private readonly object _gate = new();
    private readonly Dictionary<int, AccountDataEntry> _pendingData = [];
    private uint[]? _pendingTutorials;
    private long _lastWriteMs;
    private bool _written;
    private bool _flushScheduled;

    /// <summary>The throttle of <paramref name="accountId"/> on <paramref name="store"/>.</summary>
    public static AccountDataWriteThrottle For(IAccountDataStore store, int accountId)
        => ByStore.GetValue(store, static _ => new ConcurrentDictionary<int, AccountDataWriteThrottle>()).GetOrAdd(accountId, static _ => new AccountDataWriteThrottle());

    public Task SaveDataAsync(IAccountDataStore store, int accountId, int type, AccountDataEntry entry, int intervalMs, ILogger logger)
        => SubmitAsync(store, accountId, intervalMs, logger, () => _pendingData[type] = entry, () => store.SaveDataAsync(accountId, type, entry));

    public Task SaveTutorialsAsync(IAccountDataStore store, int accountId, uint[] tutorials, int intervalMs, ILogger logger)
        => SubmitAsync(store, accountId, intervalMs, logger, () => _pendingTutorials = tutorials, () => store.SaveTutorialsAsync(accountId, tutorials));

    private Task SubmitAsync(IAccountDataStore store, int accountId, int intervalMs, ILogger logger, Action hold, Func<Task> writeNow)
    {
        long delayMs;
        lock (_gate)
        {
            long now = Clock.Milliseconds();
            if (!_flushScheduled && (intervalMs <= 0 || !_written || now - _lastWriteMs >= intervalMs))
            {
                _written = true;
                _lastWriteMs = now;
                delayMs = -1;
            }
            else
            {
                hold();
                if (_flushScheduled)
                {
                    return Task.CompletedTask; // the scheduled flush writes the latest value
                }

                _flushScheduled = true;
                delayMs = Math.Max(0, _lastWriteMs + intervalMs - now);
            }
        }

        if (delayMs < 0)
        {
            return writeNow();
        }

        _ = FlushLaterAsync(store, accountId, delayMs, logger);
        return Task.CompletedTask;
    }

    private async Task FlushLaterAsync(IAccountDataStore store, int accountId, long delayMs, ILogger logger)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ConfigureAwait(false);
        KeyValuePair<int, AccountDataEntry>[] data;
        uint[]? tutorials;
        lock (_gate)
        {
            data = [.. _pendingData];
            _pendingData.Clear();
            tutorials = _pendingTutorials;
            _pendingTutorials = null;
            _flushScheduled = false;
            _lastWriteMs = Clock.Milliseconds();
        }

        try
        {
            foreach ((int type, AccountDataEntry entry) in data)
            {
                await store.SaveDataAsync(accountId, type, entry).ConfigureAwait(false);
            }

            if (tutorials is not null)
            {
                await store.SaveTutorialsAsync(accountId, tutorials).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("accountdata.flush", ex);
            logger.LogFault("accountdata-flush", ex, "account data flush for account {Account} failed", accountId);
        }
    }
}