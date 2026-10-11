using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// Names the code that kept a database transaction open too long. On SQLite a transaction that has taken the write lock (EF begins
/// it with <c>BEGIN IMMEDIATE</c>) makes every other writer wait and, past the busy timeout, fail with "database is locked"; the log of
/// such an incident showed the victims but never the holder. When a transaction EF started ends (commit, rollback or failure) after more than
/// the threshold (<c>Database:SlowTransactionWarnMs</c>, default <see cref="DefaultWarnMs"/>, 0 disables) this logs one WARN naming the
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> type, the outcome, the time it was open, and a caller hint.
/// <para>
/// The open time is measured here, from <c>TransactionStarted</c> to the end event (EF's own <see cref="TransactionEndEventData.Duration"/> is only the
/// commit call). A transaction costs one small allocation and a weak-table entry while the threshold is on; only an over-threshold one captures a stack
/// trace. The hint is the first frame outside the framework and this type, which is the method that committed or rolled the transaction back when the
/// provider completes synchronously (SQLite always does), and may be "(no application frame)" on a provider that resumes on another thread. A transaction
/// is reported at most once. One is reported when it ends, not while it is held; one disposed without a commit or rollback is not seen (EF has no
/// interceptor hook for disposal: <c>TransactionDisposed</c> is a logger event only); one adopted with <c>UseTransaction</c> was not started by EF and is not timed.
/// </para>
/// Immutable threshold and logger plus a weak table of open transactions; thread-safe; one instance per host (registered as a singleton so EF's internal
/// service provider cache is keyed on the same interceptor across option builds). Applies to every provider.
/// </summary>
public sealed class SlowTransactionInterceptor : DbTransactionInterceptor
{
    /// <summary>The shipped threshold (<c>Database:SlowTransactionWarnMs</c>).</summary>
    public const int DefaultWarnMs = 2000;

    private readonly ConditionalWeakTable<DbTransaction, Stamp> _open = new();
    private readonly ILogger _logger;

    public SlowTransactionInterceptor(int thresholdMs, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ThresholdMs = Math.Max(0, thresholdMs);
        _logger = logger;
    }

    /// <summary>The configured threshold in milliseconds; 0 means never warn.</summary>
    public int ThresholdMs { get; }

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
        => End(transaction, eventData.Context, "committed");

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        End(transaction, eventData.Context, "committed");
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        => End(transaction, eventData.Context, "rolled back");

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        End(transaction, eventData.Context, "rolled back");
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        => End(transaction, eventData.Context, "failed");

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        End(transaction, eventData.Context, "failed");
        return Task.CompletedTask;
    }

    private void Begin(DbTransaction transaction)
    {
        if (ThresholdMs > 0)
        {
            _open.AddOrUpdate(transaction, new Stamp(Stopwatch.GetTimestamp()));
        }
    }

    private void End(DbTransaction transaction, Microsoft.EntityFrameworkCore.DbContext? context, string outcome)
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

        _logger.LogWarning(
            "database transaction on {Context} was open {DurationMs} ms ({Outcome}; warns at {ThresholdMs} ms): it held the database for that long, so other writers may have failed as locked. Closed by {Caller}",
            context?.GetType().Name ?? "(unknown context)", (long)open.TotalMilliseconds, outcome, ThresholdMs, CallerHint());
    }

    private sealed class Stamp(long ticks)
    {
        public long Ticks { get; } = ticks;
    }

    /// <summary>The first frame that is application code: not this type, not EF, the BCL or the SQLite/ADO.NET providers.</summary>
    internal static string CallerHint()
    {
        foreach (StackFrame frame in new StackTrace(1, fNeedFileInfo: false).GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            if (method is null || type is null || type == typeof(SlowTransactionInterceptor) || IsFramework(type.Namespace))
            {
                continue;
            }

            return Describe(method, type);
        }

        return "(no application frame)";
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
