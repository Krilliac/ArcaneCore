using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// The world-lifetime writer of account data blobs and tutorial flags, with the per-account write throttle
/// (World:AccountDataWriteIntervalMs). The first change of an account in an interval is queued at once; later ones are held in memory,
/// coalesced to the latest value per type, and queued together when the interval ends, so a client cannot turn its packet budget into one
/// database write per packet. Nothing it sent is lost: <see cref="FlushAccount"/> queues the held values when a session ends (the 1.12
/// client uploads its settings in a burst as it logs out), and <see cref="StopAsync"/> queues them and drains the queue when the world stops.
/// <para>
/// Every write runs on <see cref="KeyedStoreWriteQueue{TStore}"/> (one key per account and type, newest wins, retried and retained on
/// failure), which resolves <see cref="IAccountDataStore"/> from a scope of its own for each attempt. The store a session holds is scoped to
/// its socket and wraps a pooled DbContext, which is not thread-safe and is returned when the session ends, so no write here ever touches it.
/// </para>
/// A reconnect resets the account's throttle (<see cref="FlushAccount"/> drops its state), so the first change of the new session is
/// written at once. <see cref="DrainAccountAsync"/> runs at authentication, before the settings are loaded, so a new session never reads
/// values an earlier session of the account still holds or has queued, and the earlier session's flush cannot land after the new one's writes.
/// <para>
/// Thread affinity: <see cref="SaveData"/>, <see cref="SaveTutorials"/> and <see cref="FlushAccount"/> may be called from any thread;
/// all state is guarded by one lock and the queue calls made under it only enqueue.
/// </para>
/// </summary>
public sealed class AccountDataFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers) : IWorldFeature
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, AccountState> _accounts = [];
    private readonly CancellationTokenSource _stopping = new();
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private bool _stopped;
    private Task? _stop;

    /// <summary>The write queue (tests and health reporting); null until attached, and without a store.</summary>
    public KeyedStoreWriteQueue<IAccountDataStore>? Writes { get; private set; }

    /// <summary>True once attached to a host that has an <see cref="IAccountDataStore"/>; handlers write inline on the session task when false.</summary>
    public bool IsAttached => Writes is not null;

    public void Attach(WorldRuntime world)
    {
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IAccountDataStore>() is null)
            {
                return;
            }
        }

        _logger = loggers.CreateLogger<AccountDataFeature>();
        var writes = new KeyedStoreWriteQueue<IAccountDataStore>(
            scopes, loggers.CreateLogger<KeyedStoreWriteQueue<IAccountDataStore>>(), "account data", services.GetService<ShutdownDrain>()?.Features);
        writes.Start();
        Writes = writes;
    }

    /// <summary>Completes once every write queued before the call has been attempted (tests; never throws).</summary>
    public Task FlushAsync() => Writes?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>Queue one blob (<paramref name="entry"/> with empty data erases) for the account, or hold it until the interval ends.</summary>
    public void SaveData(int accountId, int type, AccountDataEntry entry, int intervalMs)
    {
        KeyedStoreWriteQueue<IAccountDataStore> writes = Attached();
        AccountState state;
        long delayMs;
        lock (_gate)
        {
            state = StateOf(accountId);
            if (_stopped || Admit(state, intervalMs, out delayMs))
            {
                state.Data.Remove(type);
                EnqueueData(writes, accountId, type, entry);
                return;
            }

            state.Data[type] = entry;
        }

        ScheduleFlush(accountId, state, delayMs);
    }

    /// <summary>Queue the tutorial flag words for the account, or hold them until the interval ends.</summary>
    public void SaveTutorials(int accountId, uint[] tutorials, int intervalMs)
    {
        KeyedStoreWriteQueue<IAccountDataStore> writes = Attached();
        AccountState state;
        long delayMs;
        lock (_gate)
        {
            state = StateOf(accountId);
            if (_stopped || Admit(state, intervalMs, out delayMs))
            {
                state.Tutorials = null;
                EnqueueTutorials(writes, accountId, tutorials);
                return;
            }

            state.Tutorials = tutorials;
        }

        ScheduleFlush(accountId, state, delayMs);
    }

    /// <summary>
    /// Authentication of a new session of the account: queue everything an earlier session still holds, then wait until every write queued
    /// so far has been attempted (never throws; a failed write stays retained in the queue), so the settings read next include it.
    /// </summary>
    public Task DrainAccountAsync(int accountId)
    {
        FlushAccount(accountId);
        return FlushAsync();
    }

    /// <summary>Queue everything the account still holds (a session ended). Does not wait for the database.</summary>
    public void FlushAccount(int accountId)
    {
        if (Writes is null || accountId == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_accounts.Remove(accountId, out AccountState? state))
            {
                PushHeld(Writes, accountId, state);
            }
        }
    }

    /// <summary>The world is stopping: queue every held value, then drain the queue (throws naming the keys still not durable). Idempotent.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            return _stop ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        KeyedStoreWriteQueue<IAccountDataStore>? writes = Writes;
        if (writes is null)
        {
            return;
        }

        lock (_gate)
        {
            _stopped = true;
            foreach ((int accountId, AccountState state) in _accounts)
            {
                PushHeld(writes, accountId, state);
            }

            _accounts.Clear();
        }

        try
        {
            await writes.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    private KeyedStoreWriteQueue<IAccountDataStore> Attached()
        => Writes ?? throw new InvalidOperationException("the account data feature is not attached to a host with an account data store");

    private AccountState StateOf(int accountId)
    {
        if (!_accounts.TryGetValue(accountId, out AccountState? state))
        {
            _accounts[accountId] = state = new AccountState();
        }

        return state;
    }

    /// <summary>
    /// Under the gate. True: write now (the first change, or the interval has passed with nothing held). False: hold it; when no flush is
    /// scheduled yet, one is, and <paramref name="delayMs"/> says when (otherwise -1: the scheduled flush writes the latest value).
    /// </summary>
    private static bool Admit(AccountState state, int intervalMs, out long delayMs)
    {
        delayMs = -1;
        long now = Clock.Milliseconds();
        if (!state.FlushScheduled && (intervalMs <= 0 || !state.Written || now - state.LastWriteMs >= intervalMs))
        {
            state.Written = true;
            state.LastWriteMs = now;
            return true;
        }

        if (!state.FlushScheduled)
        {
            state.FlushScheduled = true;
            delayMs = Math.Max(0, state.LastWriteMs + intervalMs - now);
        }

        return false;
    }

    private void ScheduleFlush(int accountId, AccountState state, long delayMs)
    {
        if (delayMs >= 0)
        {
            _ = FlushLaterAsync(accountId, state, delayMs);
        }
    }

    private async Task FlushLaterAsync(int accountId, AccountState state, long delayMs)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _stopping.Token).ConfigureAwait(false);
            KeyedStoreWriteQueue<IAccountDataStore>? writes = Writes;
            lock (_gate)
            {
                state.FlushScheduled = false;
                state.LastWriteMs = Clock.Milliseconds();
                if (writes is not null)
                {
                    PushHeld(writes, accountId, state);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // the world is stopping: StopAsync queues what is held
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("accountdata.flush", ex);
            _logger.LogFault("accountdata-flush", ex, "account data flush for account {Account} failed", accountId);
        }
    }

    /// <summary>Under the gate: queue and clear what the account holds.</summary>
    private static void PushHeld(KeyedStoreWriteQueue<IAccountDataStore> writes, int accountId, AccountState state)
    {
        foreach ((int type, AccountDataEntry entry) in state.Data)
        {
            EnqueueData(writes, accountId, type, entry);
        }

        state.Data.Clear();
        if (state.Tutorials is { } tutorials)
        {
            EnqueueTutorials(writes, accountId, tutorials);
            state.Tutorials = null;
        }
    }

    private static void EnqueueData(KeyedStoreWriteQueue<IAccountDataStore> writes, int accountId, int type, AccountDataEntry entry)
        => writes.Save($"acct:{accountId}:{type}", store => store.SaveDataAsync(accountId, type, entry));

    private static void EnqueueTutorials(KeyedStoreWriteQueue<IAccountDataStore> writes, int accountId, uint[] tutorials)
        => writes.Save($"acct:{accountId}:tut", store => store.SaveTutorialsAsync(accountId, tutorials));

    private sealed class AccountState
    {
        public Dictionary<int, AccountDataEntry> Data { get; } = [];

        public uint[]? Tutorials { get; set; }

        public long LastWriteMs { get; set; }

        public bool Written { get; set; }

        public bool FlushScheduled { get; set; }
    }
}
