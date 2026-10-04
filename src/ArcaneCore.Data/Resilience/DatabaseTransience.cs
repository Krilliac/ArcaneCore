using System.Data.Common;
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
}
