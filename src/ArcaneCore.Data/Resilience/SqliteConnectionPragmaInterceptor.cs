using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// Sets SQLite's <c>busy_timeout</c> on every connection EF opens (SQLite only; <see cref="DataServiceCollectionExtensions.ConfigureProvider"/>
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

    /// <summary>The largest accepted <c>Database:SqliteBusyTimeoutMs</c>.</summary>
    public const int MaxBusyTimeoutMs = 60000;

    private static readonly ConcurrentDictionary<int, SqliteConnectionPragmaInterceptor> Instances = new();

    private SqliteConnectionPragmaInterceptor(int busyTimeoutMs) => BusyTimeoutMs = busyTimeoutMs;

    /// <summary>The configured busy timeout in milliseconds; 0 leaves SQLite's own "fail at once" behaviour.</summary>
    public int BusyTimeoutMs { get; }

    /// <summary>
    /// The shared interceptor for a timeout. One instance per value keeps EF's internal service provider cache keyed on the same
    /// interceptor object across the many option builds a host makes.
    /// </summary>
    public static SqliteConnectionPragmaInterceptor For(int busyTimeoutMs)
    {
        if (busyTimeoutMs is < 0 or > MaxBusyTimeoutMs)
        {
            throw new InvalidOperationException(
                $"Database:SqliteBusyTimeoutMs must be between 0 and {MaxBusyTimeoutMs}, not {busyTimeoutMs}.");
        }

        return Instances.GetOrAdd(busyTimeoutMs, static ms => new SqliteConnectionPragmaInterceptor(ms));
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
        // Never wait inside SQLite longer than the connection's own timeout: that loop checks the clock only between attempts.
        int milliseconds = (int)Math.Min(BusyTimeoutMs, connection.DefaultTimeout * 1000L);
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant($"PRAGMA busy_timeout = {milliseconds};");
        return command;
    }
}
