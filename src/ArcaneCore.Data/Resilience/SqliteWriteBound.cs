using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// Bounds how long one write on a SQLite context may wait for another connection's lock (<c>Database:SqliteWriteTimeoutMs</c>). Microsoft.Data.Sqlite
/// ignores the cancellation token while it waits, and a per-command timeout on the context would not reach EF's <c>BEGIN IMMEDIATE</c>
/// (the driver creates that command itself with the connection's Default Timeout), so the bound is the connection's own Default Timeout, lowered for the
/// lifetime of the returned scope and then restored. The wait inside SQLite is separately limited by <c>busy_timeout</c>
/// (<see cref="SqliteConnectionPragmaInterceptor"/>, 5000 ms by default); the driver gives up at the first lock answer after the timeout has elapsed, so the real
/// bound is the longer of the two (with the defaults both are 5 s) plus the driver's 150 ms poll when busy_timeout is the shorter.
/// A no-op on every other provider, when the setting is 0, or when the connection already waits no longer.
/// </summary>
internal static class SqliteWriteBound
{
    /// <summary>
    /// Lower <paramref name="db"/>'s SQLite connection timeout; dispose the result to restore it. The connection belongs to the context (one per scope),
    /// so nothing else sees the changed value. Not thread-safe against concurrent use of the same context, which EF forbids anyway.
    /// </summary>
    public static IDisposable Apply(DbContext db, IOptions<DatabaseOptions>? databases)
    {
        int milliseconds = databases?.Value.SqliteWriteTimeoutMs ?? Stores.EfCharacterStore.DefaultSqliteWriteTimeoutMs;
        if (milliseconds <= 0 || db.Database.GetDbConnection() is not SqliteConnection sqlite)
        {
            return NoBound.Instance;
        }

        int seconds = (int)Math.Min(int.MaxValue, (milliseconds + 999L) / 1000);
        int original = sqlite.DefaultTimeout;
        if (seconds >= original)
        {
            return NoBound.Instance;
        }

        sqlite.DefaultTimeout = seconds;
        return new Restore(sqlite, original);
    }

    private sealed class NoBound : IDisposable
    {
        public static readonly NoBound Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Restore(SqliteConnection connection, int seconds) : IDisposable
    {
        public void Dispose() => connection.DefaultTimeout = seconds;
    }
}
