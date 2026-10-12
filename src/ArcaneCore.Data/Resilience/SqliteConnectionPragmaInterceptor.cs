using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// Sets SQLite's <c>busy_timeout</c> and <c>synchronous</c> on every connection EF opens (SQLite only; <see cref="DataServiceCollectionExtensions.ConfigureProvider"/>
/// adds it to the Sqlite case alone). Without it SQLite reports "busy" at once and Microsoft.Data.Sqlite's own retry loop sleeps a fixed
/// 150 ms per attempt, so with a dozen background writers on one file a writer is admitted up to 150 ms after the lock frees and the
/// writers starve each other. With it SQLite's own busy handler waits inside the call and retries within a few milliseconds.
/// The outer loop still bounds the whole wait by the connection's <c>Default Timeout</c>, and the busy timeout never exceeds that
/// timeout, so <see cref="DatabaseGuard"/>'s pessimistic-timeout contract is unchanged.
/// Stateless apart from the immutable timeout; thread-safe.
/// </summary>
public sealed class SqliteConnectionPragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>The shipped timeout (<c>Database:SqliteBusyTimeoutMs</c>).</summary>
    public const int DefaultBusyTimeoutMs = 5000;

    /// <summary>The shipped bound on a characters-database command's lock wait (<c>Database:SqliteWriteTimeoutMs</c>).</summary>
    public const int DefaultWriteTimeoutMs = 5000;

    /// <summary>The largest accepted <c>Database:SqliteBusyTimeoutMs</c>.</summary>
    public const int MaxBusyTimeoutMs = 60000;

    private static readonly ConcurrentDictionary<(int Busy, int Write, SqliteSynchronousMode Synchronous), SqliteConnectionPragmaInterceptor> Instances = new();

    private SqliteConnectionPragmaInterceptor(int busyTimeoutMs, int writeTimeoutMs, SqliteSynchronousMode synchronous)
    {
        Synchronous = synchronous;
        BusyTimeoutMs = busyTimeoutMs;
        WriteTimeoutMs = writeTimeoutMs;
    }

    /// <summary>The <c>PRAGMA synchronous</c> applied to every connection this interceptor opens (<c>Database:SqliteSynchronous</c>).</summary>
    public SqliteSynchronousMode Synchronous { get; }

    /// <summary>The configured busy timeout in milliseconds; 0 leaves SQLite's own "fail at once" behaviour.</summary>
    public int BusyTimeoutMs { get; }

    /// <summary>
    /// The bound in milliseconds on any command's lock wait on the connections this interceptor opens (0: none, the connection string's Default Timeout stands).
    /// Applied by lowering the connection's Default Timeout (whole seconds, rounded up; 0, unlimited, is replaced too) at open, before <c>busy_timeout</c> is derived from it, so EF's
    /// <c>BEGIN IMMEDIATE</c>, which the driver creates with that timeout, is bounded too. This is only the backstop: a command created BEFORE the open
    /// (EF's ExecuteUpdate/Delete, ExecuteScalar and raw SQL) has already copied the old Default Timeout, so <see cref="DataServiceCollectionExtensions.ConfigureProvider"/>
    /// also lowers it in the connection string. Used for the characters database, whose write queues must not sit in a
    /// 30 s lock wait.
    /// </summary>
    public int WriteTimeoutMs { get; }

    /// <summary>
    /// The shared interceptor for a timeout. One instance per value keeps EF's internal service provider cache keyed on the same
    /// interceptor object across the many option builds a host makes.
    /// </summary>
    public static SqliteConnectionPragmaInterceptor For(int busyTimeoutMs, int writeTimeoutMs = 0, SqliteSynchronousMode synchronous = SqliteSynchronousMode.Full)
    {
        if (busyTimeoutMs is < 0 or > MaxBusyTimeoutMs)
        {
            throw new InvalidOperationException(
                $"Database:SqliteBusyTimeoutMs must be between 0 and {MaxBusyTimeoutMs}, not {busyTimeoutMs}.");
        }

        if (writeTimeoutMs is < 0 or > MaxBusyTimeoutMs)
        {
            throw new InvalidOperationException(
                $"Database:SqliteWriteTimeoutMs must be between 0 and {MaxBusyTimeoutMs}, not {writeTimeoutMs}.");
        }

        if (!Enum.IsDefined(synchronous))
        {
            throw new InvalidOperationException($"Database:SqliteSynchronous must be Full or Normal, not {synchronous}.");
        }

        return Instances.GetOrAdd((busyTimeoutMs, writeTimeoutMs, synchronous), static key => new SqliteConnectionPragmaInterceptor(key.Busy, key.Write, key.Synchronous));
    }

    /// <summary>
    /// The one place the write bound becomes a <c>Default Timeout</c>: <paramref name="writeTimeoutMs"/> in whole seconds, rounded up, replaces
    /// <paramref name="currentSeconds"/> when that is longer or 0 (0 means unlimited in Microsoft.Data.Sqlite, so a bound must override it);
    /// a shorter current value is kept, and a <paramref name="writeTimeoutMs"/> of 0 or less changes nothing.
    /// </summary>
    public static int ApplyWriteBound(int currentSeconds, int writeTimeoutMs)
    {
        if (writeTimeoutMs <= 0)
        {
            return currentSeconds;
        }

        int bound = (int)((writeTimeoutMs + 999L) / 1000); // fits an int for any int input
        return currentSeconds == 0 || bound < currentSeconds ? bound : currentSeconds;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqlite)
        {
            using SqliteCommand command = CreatePragma(sqlite);
            command.ExecuteNonQuery();
        }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqlite)
        {
            await using SqliteCommand command = CreatePragma(sqlite);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private SqliteCommand CreatePragma(SqliteConnection connection)
    {
        connection.DefaultTimeout = ApplyWriteBound(connection.DefaultTimeout, WriteTimeoutMs);

        // Never wait inside SQLite longer than the connection's own timeout: that loop checks the clock only between attempts.
        int milliseconds = (int)Math.Min(BusyTimeoutMs, connection.DefaultTimeout * 1000L);
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant($"PRAGMA busy_timeout = {milliseconds}; PRAGMA synchronous = {(Synchronous == SqliteSynchronousMode.Normal ? "NORMAL" : "FULL")};");
        return command;
    }
}
