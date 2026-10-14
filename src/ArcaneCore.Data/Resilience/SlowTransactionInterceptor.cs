using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// Names the code that kept a database transaction open too long. On SQLite a transaction that has taken the write lock (EF begins
/// it with <c>BEGIN IMMEDIATE</c>) makes every other writer wait and, past the busy timeout, fail with "database is locked"; the log of
/// such an incident showed the victims but never the holder. When a transaction EF started ends (commit, rollback or failure) after more than
/// the threshold (<c>Database:SlowTransactionWarnMs</c>, default <see cref="DefaultWarnMs"/>, 0 disables) this logs one WARN naming the
/// <see cref="DbContext"/> type, the outcome, the time it was open, and a caller hint.
/// <para>
/// The open time is measured here, from <c>TransactionStarted</c> to the end event; EF's own <see cref="TransactionEndEventData.Duration"/> is only the
/// commit (or rollback) call, so the warning reports the two parts separately: the time the application held the transaction before ending it, and the time
/// spent inside the end call (on SQLite the WAL fsync of a commit under <c>synchronous=FULL</c>, or only the WAL write under <c>synchronous=NORMAL</c>, which the message reports as the setting in effect for the context; either way disk I/O rather than application code). A transaction costs one
/// small allocation and a weak-table entry while the threshold is on; only an over-threshold one captures a stack
/// trace. The hint is the first frame outside the framework and this type, which is the method that committed or rolled the transaction back when the
/// provider completes synchronously (SQLite always does), and may be "(no application frame)" on a provider that resumes on another thread. A transaction
/// is reported at most once. One is reported when it ends, not while it is held; one adopted with <c>UseTransaction</c> was not started by EF and is not timed.
/// A transaction disposed without a commit or rollback is reported as such: EF has no interceptor hook for disposal (<c>RelationalEventId.TransactionDisposed</c>
/// is a logger and diagnostic-source event only, and the logger level is the host's to filter), so while the threshold is on this subscribes to EF's
/// <see cref="DiagnosticListener"/> for that one event name (a predicate keeps every other EF event from allocating event data) and ignores the event for any
/// transaction it did not stamp.
/// </para>
/// Immutable threshold and logger plus a weak table of open transactions and a diagnostic subscription (released by <see cref="Dispose"/>); thread-safe; one
/// instance per host (registered as a singleton so EF's internal service provider cache is keyed on the same interceptor across option builds). Applies to every provider.
/// </summary>
public sealed class SlowTransactionInterceptor : DbTransactionInterceptor, IDisposable
{
    /// <summary>The shipped threshold (<c>Database:SlowTransactionWarnMs</c>).</summary>
    public const int DefaultWarnMs = 2000;

    private readonly ConditionalWeakTable<DbTransaction, Stamp> _open = new();
    private readonly ILogger _logger;
    private readonly DisposalObserver? _disposals;

    public SlowTransactionInterceptor(int thresholdMs, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ThresholdMs = Math.Max(0, thresholdMs);
        _logger = logger;
        if (ThresholdMs > 0)
        {
            _disposals = new DisposalObserver(this);
        }
    }

    /// <summary>The configured threshold in milliseconds; 0 means never warn.</summary>
    public int ThresholdMs { get; }

    /// <summary>Releases the diagnostic subscription. Idempotent.</summary>
    public void Dispose() => _disposals?.Dispose();

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Begin(result);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Begin(result);
        return new ValueTask<DbTransaction>(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        => End(transaction, eventData.Context, "committed", eventData.Duration);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        End(transaction, eventData.Context, "committed", eventData.Duration);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        => End(transaction, eventData.Context, "rolled back", eventData.Duration);

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        End(transaction, eventData.Context, "rolled back", eventData.Duration);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        => Fail(transaction, eventData, CancellationToken.None);

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Fail(transaction, eventData, cancellationToken);
        return Task.CompletedTask;
    }

    private void Begin(DbTransaction transaction)
    {
        if (ThresholdMs > 0)
        {
            _open.AddOrUpdate(transaction, new Stamp(Stopwatch.GetTimestamp()));
        }
    }

    private void Fail(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken)
    {
        bool hadStamp = _open.TryGetValue(transaction, out Stamp? stamp);
        if (hadStamp)
        {
            _open.Remove(transaction); // reported at most once: a later end/dispose event of the same transaction finds no stamp
        }

        if (ThresholdMs <= 0 || cancellationToken.IsCancellationRequested || eventData.Exception is OperationCanceledException)
        {
            return;
        }

        TimeSpan open = hadStamp && stamp is not null ? Stopwatch.GetElapsedTime(stamp.Ticks) : eventData.Duration;
        string diagnosis = eventData.Exception is null ? "failed" : DatabaseTransience.DescribeException(eventData.Exception);
        if (open.TotalMilliseconds >= ThresholdMs)
        {
            LogSlowEnd(eventData.Context, $"failed [{diagnosis}]", open, eventData.Duration);
            return;
        }

        _logger.LogWarning(
            "database transaction {Action} failed on {Context}: {DurationMs} ms open ({EndMs} ms in {Action}; {Diagnosis}). Called from {Caller}",
            eventData.Action,
            eventData.Context?.GetType().Name ?? "(unknown context)",
            (long)open.TotalMilliseconds,
            (long)eventData.Duration.TotalMilliseconds,
            eventData.Action,
            diagnosis,
            CallerHint());
    }

    private void End(DbTransaction transaction, DbContext? context, string outcome, TimeSpan endCall)
    {
        if (ThresholdMs <= 0 || !_open.TryGetValue(transaction, out Stamp? stamp))
        {
            return;
        }

        _open.Remove(transaction); // reported at most once: a later end event of the same transaction finds no stamp
        TimeSpan open = Stopwatch.GetElapsedTime(stamp.Ticks);
        if (open.TotalMilliseconds < ThresholdMs)
        {
            return;
        }

        LogSlowEnd(context, outcome, open, endCall);
    }

    private void LogSlowEnd(DbContext? context, string outcome, TimeSpan open, TimeSpan endCall)
    {
        long openMs = (long)open.TotalMilliseconds;
        long endMs = Math.Clamp((long)endCall.TotalMilliseconds, 0, openMs);
        _logger.LogWarning(
            "database transaction on {Context} was open {DurationMs} ms ({Outcome}; warns at {ThresholdMs} ms): {HeldMs} ms held by the application before the end call and {EndMs} ms inside the end call ({EndCallKind}), so other writers may have failed as locked. Closed by {Caller}",
            context?.GetType().Name ?? "(unknown context)", openMs, outcome, ThresholdMs, openMs - endMs, endMs, EndCallKind(context), CallerHint());
    }

    /// <summary>
    /// What the end call spends its time on, for the message: on SQLite it names the <c>synchronous</c> setting actually in effect for the context (read from the
    /// <see cref="SqliteConnectionPragmaInterceptor"/> its options carry), because under NORMAL a commit does not fsync the WAL. Slow path only.
    /// </summary>
    private static string? DataSourceOf(DbContext? context)
    {
        try
        {
            return context?.Database.GetDbConnection().DataSource;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static string EndCallKind(DbContext? context)
    {
        SqliteConnectionPragmaInterceptor? pragma = null;
        if (context is not null)
        {
            foreach (object interceptor in context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors ?? [])
            {
                if (interceptor is SqliteConnectionPragmaInterceptor found)
                {
                    pragma = found;
                    break;
                }
            }
        }

        return pragma?.EffectiveFor(DataSourceOf(context)) switch
        {
            SqliteSynchronousMode.Full => "commit: the WAL fsync under synchronous=FULL, i.e. disk I/O",
            SqliteSynchronousMode.Normal => "commit: no WAL fsync under synchronous=NORMAL, so disk I/O contention or a WAL checkpoint",
            _ => "commit or rollback: disk or network I/O",
        };
    }

    /// <summary>EF logged <c>TransactionDisposed</c> for <paramref name="transaction"/>: if this interceptor still holds its stamp, it was never committed, rolled back or failed.</summary>
    private void Disposed(DbTransaction? transaction, DbContext? context)
    {
        if (ThresholdMs <= 0 || transaction is null || !_open.TryGetValue(transaction, out Stamp? stamp))
        {
            return;
        }

        _open.Remove(transaction);
        TimeSpan open = Stopwatch.GetElapsedTime(stamp.Ticks);
        if (open.TotalMilliseconds < ThresholdMs)
        {
            return;
        }

        _logger.LogWarning(
            "database transaction on {Context} was open {DurationMs} ms and was disposed without a commit or rollback (warns at {ThresholdMs} ms): it held the database for that long and was then abandoned, so other writers may have failed as locked. Disposed by {Caller}",
            context?.GetType().Name ?? "(unknown context)", (long)open.TotalMilliseconds, ThresholdMs, CallerHint());
    }

    private sealed class Stamp(long ticks)
    {
        public long Ticks { get; } = ticks;
    }

    /// <summary>
    /// Feeds EF's <c>TransactionDisposed</c> diagnostic event to <see cref="Disposed"/>. Subscribes to every EF listener (EF creates one per internal
    /// service provider), with a predicate that enables only that event. Thread-safe; the callback runs synchronously on the disposing thread.
    /// </summary>
    private sealed class DisposalObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private static readonly string EventName = RelationalEventId.TransactionDisposed.Name!;
        private readonly Lock _gate = new();
        private readonly SlowTransactionInterceptor _owner;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _all;
        private bool _disposed;

        public DisposalObserver(SlowTransactionInterceptor owner)
        {
            _owner = owner;
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != DbLoggerCategory.Name)
            {
                return;
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    _subscriptions.Add(listener.Subscribe(this, static name => name == EventName));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == EventName && value.Value is TransactionEventData data)
            {
                _owner.Disposed(data.Transaction, data.Context);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (IDisposable subscription in _subscriptions)
                {
                    subscription.Dispose();
                }

                _subscriptions.Clear();
            }

            _all.Dispose();
        }
    }

    /// <summary>The first frame that is application code: not these interceptors, not EF, the BCL or the SQLite/ADO.NET providers.</summary>
    internal static string CallerHint()
    {
        foreach (StackFrame frame in new StackTrace(1, fNeedFileInfo: false).GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            if (method is null || type is null || IsSelf(type) || IsFramework(type.Namespace))
            {
                continue;
            }

            return Describe(method, type);
        }

        return "(no application frame)";
    }

    /// <summary>One of the interceptors, or a type nested in one (the observer, a lambda closure).</summary>
    private static bool IsSelf(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type == typeof(SlowTransactionInterceptor) || type == typeof(SlowCommandInterceptor);
    }

    private static bool IsFramework(string? ns)
        => ns is null
            || ns.StartsWith("System", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft", StringComparison.Ordinal)
            || ns.StartsWith("Npgsql", StringComparison.Ordinal)
            || ns.StartsWith("MySql", StringComparison.Ordinal)
            || ns.StartsWith("Pomelo", StringComparison.Ordinal)
            || ns.StartsWith("Xunit", StringComparison.Ordinal);

    /// <summary>"Namespace.Type.Method"; an async method's compiler-generated state machine <c>Type+&lt;Method&gt;d__3.MoveNext</c> is reported as <c>Type.Method</c>.</summary>
    private static string Describe(MethodBase method, Type type)
    {
        if (type.DeclaringType is { } owner && type.Name.Length > 2 && type.Name[0] == '<' && type.Name.IndexOf('>') is var end and > 1)
        {
            return owner.FullName + "." + type.Name[1..end];
        }

        return type.FullName + "." + method.Name;
    }
}
