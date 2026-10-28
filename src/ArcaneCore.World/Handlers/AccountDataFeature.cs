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
/// database write per packet. Nothing it sent is lost: the last session of an account to end (<see cref="EndSession"/>) queues the held
/// values (the 1.12 client uploads its settings in a burst as it logs out), and <see cref="StopAsync"/> queues them and drains the queue
/// when the world stops.
/// <para>
/// Every write runs on <see cref="KeyedStoreWriteQueue{TStore}"/> (one key per account and type, newest wins, retried and retained on
/// failure), which resolves <see cref="IAccountDataStore"/> from a scope of its own for each attempt. The store a session holds is scoped to
/// its socket and wraps a pooled DbContext, which is not thread-safe and is returned when the session ends, so no write here ever touches it.
/// </para>
/// <para>
/// Newest wins across sessions of one account. The account has one state, shared by its sessions, holding the latest value of every blob
/// and of the tutorial flags that is not yet confirmed written (held or queued or retained after a failure). A session that authenticates
/// reads its settings from the database and then overlays that unconfirmed state (<see cref="Snapshot"/>, <see cref="AttachSession"/>), so it
/// never serves a blob older than one an earlier session sent, and it waits for no queue: a slow or failing account data database cannot
/// delay a login. Its own writes replace the shared values, so a flush of the earlier session's held value can no longer land after them.
/// The state lives while a session is attached or a value is unconfirmed, so a reconnect with nothing pending starts a fresh throttle.
/// The one remaining window: a write the earlier session sends after the new session took its snapshot and before the registry kicks the
/// earlier session is newer than the new session's settings, and the new session may serve its older value until it writes.
/// </para>
/// Thread affinity: every public method may be called from any thread; all state is guarded by one lock and the queue calls made under it
/// only enqueue.
/// </summary>
public sealed class AccountDataFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILoggerFactory _loggers;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, AccountState> _accounts = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lazy<Task> _stop;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private bool _stopped;

    public AccountDataFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers)
    {
        _services = services;
        _scopes = scopes;
        _loggers = loggers;
        _stop = new Lazy<Task>(StopCoreAsync);
    }

    /// <summary>The write queue (tests and health reporting); null until attached, and without a store.</summary>
    public KeyedStoreWriteQueue<IAccountDataStore>? Writes { get; private set; }

    /// <summary>True once attached to a host that has an <see cref="IAccountDataStore"/>; handlers write inline on the session task when false.</summary>
    public bool IsAttached => Writes is not null;

    public void Attach(WorldRuntime world)
    {
        using (IServiceScope scope = _scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IAccountDataStore>() is null)
            {
                return;
            }
        }

        _logger = _loggers.CreateLogger<AccountDataFeature>();
        var writes = new KeyedStoreWriteQueue<IAccountDataStore>(
            _scopes, _loggers.CreateLogger<KeyedStoreWriteQueue<IAccountDataStore>>(), "account data", _services.GetService<ShutdownDrain>()?.Features);
        writes.Start();
        Writes = writes;
    }

    /// <summary>Completes once every write queued before the call has been attempted (tests; never throws).</summary>
    public Task FlushAsync() => Writes?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>Accounts with a session attached or a value not yet confirmed written (tests and health reporting).</summary>
    public int TrackedAccounts
    {
        get
        {
            lock (_gate)
            {
                return _accounts.Count;
            }
        }
    }

    /// <summary>
    /// Authentication, before the settings are read from the database: the account's values that are not yet confirmed written. Taking it
    /// before the read means a value confirmed in between is in the database or in the snapshot, never in neither. Null without anything.
    /// </summary>
    public PendingAccountData? Snapshot(int accountId)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(accountId, out AccountState? state) || (state.Latest.Count == 0 && state.LatestTutorials is null))
            {
                return null;
            }

            return new PendingAccountData([.. state.Latest], state.LatestTutorials);
        }
    }

    /// <summary>
    /// Authentication, right after the database read and in the same synchronous step that records the account id on the session:
    /// overlay <paramref name="pending"/> on <paramref name="settings"/> and count the session. Pair with exactly one <see cref="EndSession"/>.
    /// </summary>
    public void AttachSession(int accountId, AccountSettings settings, PendingAccountData? pending)
    {
        pending?.ApplyTo(settings);
        lock (_gate)
        {
            StateOf(accountId).Sessions++;
        }
    }

    /// <summary>A session of the account ended. The last one queues the values still held (no waiting for the database) and lets the state go.</summary>
    public void EndSession(int accountId)
    {
        KeyedStoreWriteQueue<IAccountDataStore>? writes = Writes;
        lock (_gate)
        {
            if (!_accounts.TryGetValue(accountId, out AccountState? state) || state.Sessions == 0)
            {
                return;
            }

            if (--state.Sessions > 0)
            {
                return; // another session of the account owns the held values now
            }

            if (writes is not null)
            {
                PushHeld(writes, accountId, state);
            }

            state.FlushScheduled = false; // a timer still waiting finds nothing held; it must not keep the state alive
            RemoveIfIdle(accountId, state);
        }
    }

    /// <summary>Queue one blob (<paramref name="entry"/> with empty data erases) for the account, or hold it until the interval ends.</summary>
    public void SaveData(int accountId, int type, AccountDataEntry entry, int intervalMs)
    {
        KeyedStoreWriteQueue<IAccountDataStore> writes = Attached();
        AccountState state;
        long delayMs;
        lock (_gate)
        {
            state = StateOf(accountId);
            state.Latest[type] = entry;
            if (_stopped || Admit(state, intervalMs, out delayMs))
            {
                state.HeldData.Remove(type);
                EnqueueData(writes, accountId, type, entry);
                return;
            }

            state.HeldData.Add(type);
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
            state.LatestTutorials = tutorials;
            if (_stopped || Admit(state, intervalMs, out delayMs))
            {
                state.TutorialsHeld = false;
                EnqueueTutorials(writes, accountId, tutorials);
                return;
            }

            state.TutorialsHeld = true;
        }

        ScheduleFlush(accountId, state, delayMs);
    }

    /// <summary>The world is stopping: queue every held value, then drain the queue (throws naming the keys still not durable). Idempotent.</summary>
    public Task StopAsync() => _stop.Value;

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

    /// <summary>Under the gate: forget the account when no session is attached, no flush is scheduled and every value is confirmed written.</summary>
    private void RemoveIfIdle(int accountId, AccountState state)
    {
        if (state.Sessions == 0 && !state.FlushScheduled && state.Latest.Count == 0 && state.LatestTutorials is null)
        {
            _accounts.Remove(accountId);
        }
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

                RemoveIfIdle(accountId, state);
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
    private void PushHeld(KeyedStoreWriteQueue<IAccountDataStore> writes, int accountId, AccountState state)
    {
        foreach (int type in state.HeldData)
        {
            EnqueueData(writes, accountId, type, state.Latest[type]);
        }

        state.HeldData.Clear();
        if (state.TutorialsHeld && state.LatestTutorials is { } tutorials)
        {
            EnqueueTutorials(writes, accountId, tutorials);
        }

        state.TutorialsHeld = false;
    }

    private void EnqueueData(KeyedStoreWriteQueue<IAccountDataStore> writes, int accountId, int type, AccountDataEntry entry)
        => writes.Save($"acct:{accountId}:{type}", async store =>
        {
            await store.SaveDataAsync(accountId, type, entry).ConfigureAwait(false);
            lock (_gate)
            {
                if (_accounts.TryGetValue(accountId, out AccountState? state) && state.Latest.TryGetValue(type, out AccountDataEntry? latest) && ReferenceEquals(latest, entry))
                {
                    state.Latest.Remove(type); // confirmed written, and nothing newer since
                    RemoveIfIdle(accountId, state);
                }
            }
        });

    private void EnqueueTutorials(KeyedStoreWriteQueue<IAccountDataStore> writes, int accountId, uint[] tutorials)
        => writes.Save($"acct:{accountId}:tut", async store =>
        {
            await store.SaveTutorialsAsync(accountId, tutorials).ConfigureAwait(false);
            lock (_gate)
            {
                if (_accounts.TryGetValue(accountId, out AccountState? state) && ReferenceEquals(state.LatestTutorials, tutorials))
                {
                    state.LatestTutorials = null;
                    RemoveIfIdle(accountId, state);
                }
            }
        });

    /// <summary>The unconfirmed values of an account at the time of <see cref="Snapshot"/>.</summary>
    public sealed class PendingAccountData
    {
        private readonly KeyValuePair<int, AccountDataEntry>[] _data;
        private readonly uint[]? _tutorials;

        internal PendingAccountData(KeyValuePair<int, AccountDataEntry>[] data, uint[]? tutorials)
        {
            _data = data;
            _tutorials = tutorials;
        }

        internal void ApplyTo(AccountSettings settings)
        {
            foreach ((int type, AccountDataEntry entry) in _data)
            {
                if ((uint)type < AccountSettings.DataTypeCount)
                {
                    settings.Data[type] = entry.Data.Length == 0 ? null : entry;
                }
            }

            _tutorials?.CopyTo(settings.Tutorials, 0);
        }
    }

    private sealed class AccountState
    {
        /// <summary>The newest value of each type that is not yet confirmed written.</summary>
        public Dictionary<int, AccountDataEntry> Latest { get; } = [];

        /// <summary>Types whose <see cref="Latest"/> value waits for the interval to end.</summary>
        public HashSet<int> HeldData { get; } = [];

        public uint[]? LatestTutorials { get; set; }

        public bool TutorialsHeld { get; set; }

        public int Sessions { get; set; }

        public long LastWriteMs { get; set; }

        public bool Written { get; set; }

        public bool FlushScheduled { get; set; }
    }
}
