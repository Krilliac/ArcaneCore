using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// Puts EF's SQLite writes behind the file's <see cref="SqliteWriteGate"/> (always on for file-backed SQLite since 2026-10-17). A transaction EF begins (Microsoft.Data.Sqlite opens it as
/// <c>BEGIN IMMEDIATE</c>, which takes the write lock at once) takes the gate before the BEGIN and gives it back when the transaction is committed, rolled back, fails, is disposed,
/// or its connection closes. A write statement run outside any transaction (EF's single-statement SaveChanges, ExecuteUpdate/ExecuteDelete with the write fence off, raw SQL) takes it
/// around the statement. Reads, transactions adopted with <c>UseTransaction</c> (the deferred read snapshots) and statements inside a transaction are left alone.
/// <para>
/// Added only to the SQLite case of <see cref="DataServiceCollectionExtensions.ConfigureProvider"/>, and only for a file-backed database (others have no gate). One shared instance per
/// settings and logger (<see cref="For"/>), so EF's internal service provider cache stays keyed on one object. Thread-safe: per-connection, per-transaction and per-command holds live in weak tables.
/// </para>
/// </summary>
public sealed class SqliteWriteGateInterceptor : DbTransactionInterceptor, IDbCommandInterceptor, IDbConnectionInterceptor
{
    private static readonly ConcurrentDictionary<(bool, int, int, int, int, ILogger?), SqliteWriteGateInterceptor> Instances = new();
    private static readonly string DisposedEventName = RelationalEventId.TransactionDisposed.Name!;

    private readonly ConditionalWeakTable<DbConnection, SqliteWriteGate.Hold> _byConnection = new();
    private readonly ConditionalWeakTable<DbTransaction, SqliteWriteGate.Hold> _byTransaction = new();
    private readonly ConditionalWeakTable<DbCommand, SqliteWriteGate.Hold> _byCommand = new();
    private readonly ConcurrentDictionary<SqliteWriteGate, long> _lastStats = new();
    private readonly ConcurrentDictionary<SqliteWriteGate, long> _lastLoggedFaults = new();
    private readonly ConcurrentDictionary<SqliteWriteGate, bool> _subscribedGates = new();
    private readonly ILogger? _logger;
    private readonly SqliteSingleWriterOptions _options;
    private readonly DisposalObserver _disposals;

    private SqliteWriteGateInterceptor(SqliteSingleWriterOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
        _disposals = new DisposalObserver(this);
    }

    /// <summary>The shared interceptor for these settings and logger.</summary>
    public static SqliteWriteGateInterceptor For(SqliteSingleWriterOptions options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var snapshot = new SqliteSingleWriterOptions
        {
            Enabled = options.Enabled, WaitMs = options.WaitMs, MaxHoldMs = options.MaxHoldMs, WarnWaitMs = options.WarnWaitMs, StatsIntervalMs = options.StatsIntervalMs,
        };
        return Instances.GetOrAdd((snapshot.Enabled, snapshot.WaitMs, snapshot.MaxHoldMs, snapshot.WarnWaitMs, snapshot.StatsIntervalMs, logger),
            _ => new SqliteWriteGateInterceptor(snapshot, logger));
    }

    // ---- transactions ----

    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        if (GateFor(connection) is { } gate)
        {
            ReleaseStale(connection);
            int timeout = TimeoutFor(connection);
            SqliteWriteGate.Hold hold = gate.Enter(timeout, OwnerOf(eventData.Context, gate), _options.MaxHoldMs) ?? throw gate.Busy(timeout);
            Granted(hold);
            _byConnection.AddOrUpdate(connection, hold);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        if (GateFor(connection) is { } gate)
        {
            ReleaseStale(connection);
            int timeout = TimeoutFor(connection);
            SqliteWriteGate.Hold hold = await gate.EnterAsync(timeout, OwnerOf(eventData.Context, gate), _options.MaxHoldMs, cancellationToken).ConfigureAwait(false)
                ?? throw gate.Busy(timeout);
            Granted(hold);
            _byConnection.AddOrUpdate(connection, hold);
        }

        return result;
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Attach(connection, result);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Attach(connection, result);
        return new ValueTask<DbTransaction>(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => EndTransaction(transaction);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        EndTransaction(transaction);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => EndTransaction(transaction);

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        EndTransaction(transaction);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        // A failed commit or rollback: Microsoft.Data.Sqlite has ended (or will end, at disposal) the transaction; the gate goes back at disposal or close.
        // A failure of a statement inside the transaction is not a transaction event. Nothing to do here except for a transaction that is already gone.
        if (transaction.Connection is null) EndTransaction(transaction);
    }

    private void Attach(DbConnection connection, DbTransaction transaction)
    {
        if (_byConnection.TryGetValue(connection, out SqliteWriteGate.Hold? hold) && !hold.Released)
        {
            _byTransaction.AddOrUpdate(transaction, hold);
            if (connection is SqliteConnection sqlite)
            {
                // From here on the hold stands for an open SQLite transaction. Code that ends it behind EF's back (the write fence rolls back a transaction it
                // refuses, straight on the DbTransaction) leaves no event; a waiter then finds the connection back in autocommit and releases the hold.
                var weak = new WeakReference<SqliteConnection>(sqlite);
                hold.Probe = () => weak.TryGetTarget(out SqliteConnection? target) && HasTransaction(target);
            }
        }
    }

    private void EndTransaction(DbTransaction transaction)
    {
        if (_byTransaction.TryGetValue(transaction, out SqliteWriteGate.Hold? hold))
        {
            _byTransaction.Remove(transaction);
            Release(hold);
        }
    }

    internal void Disposed(DbTransaction transaction) => EndTransaction(transaction);

    // ---- connections ----

    public InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        ReleaseConnection(connection);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        ReleaseConnection(connection);
        return new ValueTask<InterceptionResult>(result);
    }

    public InterceptionResult ConnectionDisposing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        ReleaseConnection(connection);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionDisposingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        ReleaseConnection(connection);
        return new ValueTask<InterceptionResult>(result);
    }

    private void ReleaseConnection(DbConnection connection)
    {
        if (_byConnection.TryGetValue(connection, out SqliteWriteGate.Hold? hold))
        {
            _byConnection.Remove(connection);
            Release(hold);
        }
    }

    /// <summary>A hold left on a connection whose transaction ended without an event we saw (a BEGIN that threw, an unobserved disposal): give it back.</summary>
    private void ReleaseStale(DbConnection connection)
    {
        if (_byConnection.TryGetValue(connection, out SqliteWriteGate.Hold? hold) && (hold.Released || connection is SqliteConnection { } sqlite && !HasTransaction(sqlite)))
            ReleaseConnection(connection);
    }

    private static bool HasTransaction(SqliteConnection connection)
    {
        // SqliteConnection keeps its open transaction internally; a command created now inherits nothing, so ask SQLite itself.
        if (connection.State != System.Data.ConnectionState.Open || connection.Handle is not { IsClosed: false, IsInvalid: false } handle) return false;
        return SQLitePCL.raw.sqlite3_get_autocommit(handle) == 0;
    }

    // ---- autocommit write statements ----

    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnterCommand(command, eventData);
        return result;
    }

    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await EnterCommandAsync(command, eventData, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        ExitCommand(command);
        return result;
    }

    public ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ExitCommand(command);
        return new ValueTask<int>(result);
    }

    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnterCommand(command, eventData);
        return result;
    }

    public async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await EnterCommandAsync(command, eventData, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        ExitCommand(command);
        return result;
    }

    public ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        ExitCommand(command);
        return new ValueTask<object?>(result);
    }

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnterCommand(command, eventData);
        return result;
    }

    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await EnterCommandAsync(command, eventData, cancellationToken).ConfigureAwait(false);
        return result;
    }

    // A reader's statement holds the lock until it is stepped to the end or reset: the hold goes back when the reader is disposed.
    public InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        ExitCommand(command);
        return result;
    }

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData) => ExitCommand(command);

    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ExitCommand(command);
        return Task.CompletedTask;
    }

    public void CommandCanceled(DbCommand command, CommandEndEventData eventData) => ExitCommand(command);

    public Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ExitCommand(command);
        return Task.CompletedTask;
    }

    private void EnterCommand(DbCommand command, CommandEventData eventData)
    {
        if (command.Transaction is not null || !IsWrite(command.CommandText) || GateFor(command.Connection) is not { } gate) return;
        if (command.Connection is { } connection) ReleaseStale(connection);
        int timeout = TimeoutFor(command);
        SqliteWriteGate.Hold hold = gate.Enter(timeout, OwnerOf(eventData.Context, gate), _options.MaxHoldMs) ?? throw gate.Busy(timeout);
        Granted(hold);
        _byCommand.AddOrUpdate(command, hold);
    }

    private async ValueTask EnterCommandAsync(DbCommand command, CommandEventData eventData, CancellationToken cancellationToken)
    {
        if (command.Transaction is not null || !IsWrite(command.CommandText) || GateFor(command.Connection) is not { } gate) return;
        if (command.Connection is { } connection) ReleaseStale(connection);
        int timeout = TimeoutFor(command);
        SqliteWriteGate.Hold hold = await gate.EnterAsync(timeout, OwnerOf(eventData.Context, gate), _options.MaxHoldMs, cancellationToken).ConfigureAwait(false)
            ?? throw gate.Busy(timeout);
        Granted(hold);
        _byCommand.AddOrUpdate(command, hold);
    }

    private void ExitCommand(DbCommand command)
    {
        if (_byCommand.TryGetValue(command, out SqliteWriteGate.Hold? hold))
        {
            _byCommand.Remove(command);
            Release(hold);
        }
    }

    /// <summary>
    /// Whether <paramref name="sql"/> writes: its first keyword (after whitespace and comments) is INSERT, UPDATE, DELETE, REPLACE, CREATE, DROP, ALTER, VACUUM or REINDEX, or it is a
    /// WITH that contains one of INSERT/UPDATE/DELETE/REPLACE. A batch that begins with a read and writes later is not recognised (EF never issues one outside a transaction).
    /// </summary>
    internal static bool IsWrite(string? sql)
    {
        if (string.IsNullOrEmpty(sql)) return false;
        ReadOnlySpan<char> text = sql.AsSpan();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == ';' || c == '(') { i++; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                int end = text[i..].IndexOf('\n');
                if (end < 0) return false;
                i += end + 1;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) return false;
                i += end + 4;
                continue;
            }

            break;
        }

        int start = i;
        while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
        ReadOnlySpan<char> word = text[start..i];
        if (word.Equals("INSERT", StringComparison.OrdinalIgnoreCase) || word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
            || word.Equals("DELETE", StringComparison.OrdinalIgnoreCase) || word.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
            || word.Equals("CREATE", StringComparison.OrdinalIgnoreCase) || word.Equals("DROP", StringComparison.OrdinalIgnoreCase)
            || word.Equals("ALTER", StringComparison.OrdinalIgnoreCase) || word.Equals("VACUUM", StringComparison.OrdinalIgnoreCase)
            || word.Equals("REINDEX", StringComparison.OrdinalIgnoreCase))
            return true;
        if (word.Equals("WITH", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> rest = text[i..];
            return rest.Contains("INSERT", StringComparison.OrdinalIgnoreCase) || rest.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || rest.Contains("DELETE", StringComparison.OrdinalIgnoreCase) || rest.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    // ---- shared ----

    private SqliteWriteGate? GateFor(DbConnection? connection)
    {
        if (!_options.Enabled || connection is not SqliteConnection sqlite) return null;
        SqliteWriteGate? gate = SqliteWriteGate.For(sqlite.ConnectionString);
        if (gate is not null && _subscribedGates.TryAdd(gate, true))
        {
            gate.StuckReleased += OnStuckReleased;
            gate.DeadReleased += OnDeadReleased;
        }

        return gate;
    }

    private int TimeoutFor(DbConnection connection)
    {
        if (_options.WaitMs > 0) return _options.WaitMs;
        int seconds = connection is SqliteConnection sqlite ? sqlite.DefaultTimeout : 30;
        return seconds <= 0 ? 30_000 : (int)Math.Min(seconds * 1000L, SqliteSingleWriterOptions.MaxMs);
    }

    private int TimeoutFor(DbCommand command)
    {
        if (_options.WaitMs > 0) return _options.WaitMs;
        int seconds = command.CommandTimeout;
        return seconds <= 0 ? 30_000 : (int)Math.Min(seconds * 1000L, SqliteSingleWriterOptions.MaxMs);
    }

    private static string OwnerOf(DbContext? context, SqliteWriteGate gate)
    {
        string name = context?.GetType().Name ?? "(no context)";
        // Naming the caller costs a stack walk; only pay it when someone is queued behind this writer and may want to know who it was.
        return gate.Waiting > 0 ? name + " " + CallerHint() : name;
    }

    private void Granted(SqliteWriteGate.Hold hold)
    {
        if (_options.WarnWaitMs > 0 && hold.WaitedMs >= _options.WarnWaitMs && _logger is not null)
        {
            if (!hold.Owner.Contains(' ', StringComparison.Ordinal)) hold.Owner += " " + CallerHint();
            _logger.LogWarning("SQLite write gate {File}: {Owner} waited {WaitMs} ms for the write lock ({Waiting} still waiting)",
                Path.GetFileName(hold.Gate.Path), hold.Owner, (long)hold.WaitedMs, hold.Gate.Waiting);
        }
    }

    private void Release(SqliteWriteGate.Hold hold)
    {
        hold.Release();
        MaybeLogStats(hold.Gate);
    }

    private void MaybeLogStats(SqliteWriteGate gate)
    {
        if (_options.StatsIntervalMs <= 0 || _logger is null) return;
        long now = Stopwatch.GetTimestamp();
        long last = _lastStats.GetOrAdd(gate, now);
        if (Stopwatch.GetElapsedTime(last, now).TotalMilliseconds < _options.StatsIntervalMs || !_lastStats.TryUpdate(gate, now, last)) return;
        GateStats stats = gate.TakeStats();
        long lastFaults = _lastLoggedFaults.TryGetValue(gate, out long logged) ? logged : 0;
        if (stats.Acquired == 0 && stats.Timeouts == 0 && stats.Forced == 0 && stats.HandlerFaultsTotal == lastFaults) return;
        _lastLoggedFaults[gate] = stats.HandlerFaultsTotal;
        _logger.LogInformation(
            "SQLite write gate {File}: {Writes} writes in the last {IntervalS} s, {Contended} queued (wait mean {WaitMeanMs:0.0} ms, max {WaitMaxMs:0} ms), longest hold {HoldMaxMs:0} ms by {HoldOwner}, {Timeouts} timed out, {Forced} stuck holds released; {Waiting} waiting now (lifetime {Total} writes, {TotalTimeouts} timeouts, {TotalDead} ended unseen, {HandlerFaults} log handler faults, first: {FirstHandlerFault})",
            Path.GetFileName(stats.Path), stats.Acquired, _options.StatsIntervalMs / 1000, stats.Contended, stats.WaitMsMean, stats.WaitMsMax, stats.HoldMsMax, stats.HoldMaxOwner ?? "-",
            stats.Timeouts, stats.Forced, stats.Waiting, stats.AcquiredTotal, stats.TimeoutsTotal, gate.DeadTotal, stats.HandlerFaultsTotal, stats.FirstHandlerFault ?? "-");
    }

    private void OnStuckReleased(SqliteWriteGate.Hold hold)
        => _logger?.LogError(
            "SQLite write gate {File}: released a hold of {HeldMs} ms by {Owner} (longer than the {MaxHoldMs} ms hold bound; a transaction that was never ended?). The next writer now waits on SQLite's own lock",
            Path.GetFileName(hold.Gate.Path), (long)hold.HeldMs, hold.Owner, _options.MaxHoldMs);

    // Debug, not a warning: the usual case is the window between Microsoft.Data.Sqlite running COMMIT and EF raising TransactionCommitted (live,
    // 2026-10-17: five per redeploy, 0-534 ms, all CharacterDbContext). The hold is released either way; the lifetime count is on the stats line.
    private void OnDeadReleased(SqliteWriteGate.Hold hold)
        => _logger?.LogDebug(
            "SQLite write gate {File}: released a hold of {HeldMs} ms by {Owner} whose transaction had already ended before its commit, rollback or close event arrived",
            Path.GetFileName(hold.Gate.Path), (long)hold.HeldMs, hold.Owner);

    /// <summary>The first frame outside the framework and this namespace's interceptors.</summary>
    internal static string CallerHint()
    {
        foreach (StackFrame frame in new StackTrace(2, fNeedFileInfo: false).GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            if (method is null || type is null) continue;
            string? ns = type.Namespace;
            if (ns is null || ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal)
                || ns.StartsWith("SQLitePCL", StringComparison.Ordinal) || ns == typeof(SqliteWriteGateInterceptor).Namespace
                || ns.StartsWith("ArcaneCore.Data.Stores", StringComparison.Ordinal) && type.Name.StartsWith("CharacterWrite", StringComparison.Ordinal))
                continue;
            if (type.DeclaringType is { } owner && type.Name.Length > 2 && type.Name[0] == '<' && type.Name.IndexOf('>') is var end and > 1)
                return owner.Name + "." + type.Name[1..end];
            return type.Name + "." + method.Name;
        }

        return "(no application frame)";
    }

    /// <summary>EF has no interceptor hook for a transaction disposed without commit or rollback; its diagnostic event is the only signal (see SlowTransactionInterceptor).</summary>
    private sealed class DisposalObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly SqliteWriteGateInterceptor _owner;
        private readonly Lock _gate = new();
        private readonly List<IDisposable> _subscriptions = [];

        public DisposalObserver(SqliteWriteGateInterceptor owner)
        {
            _owner = owner;
            DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != DbLoggerCategory.Name) return;
            lock (_gate) _subscriptions.Add(listener.Subscribe(this, static name => name == DisposedEventName));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == DisposedEventName && value.Value is TransactionEventData data) _owner.Disposed(data.Transaction);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
