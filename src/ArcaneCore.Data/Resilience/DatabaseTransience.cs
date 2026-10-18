using System.Data.Common;
using System.Globalization;
using ArcaneCore.Kernel.Resilience;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// <see cref="TransientFailure"/> extended with what the three providers say when the <i>database</i>, not the
/// statement, is the problem. A constraint violation, "no such table", a failed translation or a domain exception is
/// never transient: it must surface to the caller and must not open a circuit.
/// </summary>
public static class DatabaseTransience
{
    /// <summary>The cached delegate form of <see cref="IsTransient"/>.</summary>
    public static readonly Func<Exception, bool> Classifier = IsTransient;

    /// <summary>
    /// True when the failure is SQLite reporting that another connection holds the file: <c>SQLITE_BUSY</c> (5) or <c>SQLITE_LOCKED</c> (6),
    /// directly or wrapped (EF's <see cref="DbUpdateException"/>, an aggregate, any inner exception). The wait already happened inside
    /// the driver (up to the command timeout), so a write queue must not repeat it inline: it keeps the write and retries later.
    /// Other engines never match (their lock waits surface as <see cref="DbException.IsTransient"/>, not as this code).
    /// </summary>
    public static bool IsBusy(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception? current = exception;
        for (int depth = 0; current is not null && depth < TransientFailure.MaxDepth; depth++)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (IsBusy(inner))
                    {
                        return true;
                    }
                }
            }

            current = current.InnerException;
        }

        return false;
    }

    /// <summary>True when the failure means the database could not be reached, opened, or answered in time.</summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception? current = exception;
        for (int depth = 0; current is not null && depth < TransientFailure.MaxDepth; depth++)
        {
            switch (current)
            {
                // SQLite: 5 BUSY and 6 LOCKED (another writer), 10 IOERR, 14 CANTOPEN (file or directory gone),
                // 26 NOTADB (file replaced by something else). Microsoft.Data.Sqlite does not set DbException.IsTransient.
                case SqliteException { SqliteErrorCode: 5 or 6 or 10 or 14 or 26 }:
                    return true;

                // EF wraps a provider failure of SaveChanges; the provider exception underneath decides.
                case DbUpdateException { InnerException: DbException inner }:
                    return TransientFailure.IsTransient(inner) || IsTransient(inner);

                // MySqlConnector and Npgsql: connection refused, host unreachable, server gone away, deadlock, lock wait.
                case DbException { IsTransient: true }:
                    return true;
            }

            current = current.InnerException;
        }

        return TransientFailure.IsTransient(exception);
    }

    /// <summary>
    /// Inspects the exception chain (<see cref="SqliteException.SqliteErrorCode"/>, <see cref="SqliteException.SqliteExtendedErrorCode"/>,
    /// <see cref="DbException.SqlState"/>, <see cref="System.Runtime.InteropServices.ExternalException.ErrorCode"/>, <see cref="DbUpdateConcurrencyException"/>,
    /// <see cref="DbUpdateException"/>, <see cref="TimeoutException"/>, <see cref="OperationCanceledException"/>) and returns a
    /// structured, credential-safe diagnostic summary (<c>type=... code=... sqlState=... transient=... busy=...</c>).
    /// </summary>
    public static string DescribeException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        SqliteException? sqlite = FindInChain<SqliteException>(exception);
        DbException? db = sqlite ?? FindInChain<DbException>(exception);
        bool isConcurrency = FindInChain<DbUpdateConcurrencyException>(exception) is not null;
        bool isUpdate = FindInChain<DbUpdateException>(exception) is not null;
        bool isTimeout = FindInChain<TimeoutException>(exception) is not null || FindInChain<TimeoutRejectedException>(exception) is not null;
        bool isCanceled = FindInChain<OperationCanceledException>(exception) is not null;

        string typeName = db is not null && !ReferenceEquals(db, exception)
            ? $"{exception.GetType().Name}->{db.GetType().Name}"
            : exception.GetType().Name;

        string code;
        if (sqlite is not null)
        {
            code = sqlite.SqliteExtendedErrorCode != 0 && sqlite.SqliteExtendedErrorCode != sqlite.SqliteErrorCode
                ? $"{sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}"
                : sqlite.SqliteErrorCode.ToString(CultureInfo.InvariantCulture);
        }
        else if (db is not null && db.ErrorCode != 0)
        {
            code = db.ErrorCode.ToString(CultureInfo.InvariantCulture);
        }
        else if (isConcurrency)
        {
            code = "concurrency";
        }
        else if (isUpdate)
        {
            code = "db-update";
        }
        else if (isTimeout)
        {
            code = "timeout";
        }
        else if (isCanceled)
        {
            code = "canceled";
        }
        else
        {
            code = "none";
        }

        string sqlState = db is not null && !string.IsNullOrWhiteSpace(db.SqlState) ? db.SqlState : "none";
        bool transient = !isCanceled && IsTransient(exception);
        bool busy = IsBusy(exception);

        return $"type={typeName} code={code} sqlState={sqlState} transient={(transient ? "true" : "false")} busy={(busy ? "true" : "false")}";
    }

    /// <summary>
    /// Formats the provider error code on <paramref name="exception"/> (<see cref="SqliteException.SqliteErrorCode"/>,
    /// <see cref="SqliteException.SqliteExtendedErrorCode"/>, <see cref="DbException.SqlState"/>, <see cref="System.Runtime.InteropServices.ExternalException.ErrorCode"/>),
    /// or null when the exception carries no provider code.
    /// </summary>
    public static string? FormatProviderCode(Exception? exception)
    {
        return exception switch
        {
            SqliteException sqlite when sqlite.SqliteExtendedErrorCode != 0 && sqlite.SqliteExtendedErrorCode != sqlite.SqliteErrorCode
                => $"SqliteErrorCode={sqlite.SqliteErrorCode}, Extended={sqlite.SqliteExtendedErrorCode}",
            SqliteException sqlite
                => $"SqliteErrorCode={sqlite.SqliteErrorCode}",
            DbException db when !string.IsNullOrWhiteSpace(db.SqlState) && db.ErrorCode != 0
                => $"SqlState={db.SqlState}, ErrorCode={db.ErrorCode}",
            DbException db when !string.IsNullOrWhiteSpace(db.SqlState)
                => $"SqlState={db.SqlState}",
            DbException db when db.ErrorCode != 0
                => $"ErrorCode={db.ErrorCode}",
            _ => null,
        };
    }

    private static TException? FindInChain<TException>(Exception exception, int depth = 0)
        where TException : Exception
    {
        Exception? current = exception;
        for (int d = depth; current is not null && d < TransientFailure.MaxDepth; d++)
        {
            if (current is TException matched)
            {
                return matched;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (FindInChain<TException>(inner, d + 1) is { } nested)
                    {
                        return nested;
                    }
                }
            }

            current = current.InnerException;
        }

        return null;
    }
}
