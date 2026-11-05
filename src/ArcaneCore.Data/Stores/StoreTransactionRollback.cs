using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// The rollback half of a store's commit-failure handler. Each store records its own commit
/// failure first (its fault keys, severities and cancellation filter differ), then calls this to
/// roll back; the caller keeps the bare <c>throw;</c> so the original stack trace survives.
/// </summary>
internal static class StoreTransactionRollback
{
    /// <summary>
    /// Rolls <paramref name="transaction"/> back, ignoring cancellation so the previous state is
    /// restored. If the rollback itself fails, <paramref name="reportRollbackFailure"/> runs
    /// (per-site ledger keys) and an <see cref="AggregateException"/> carrying both errors is thrown.
    /// Returns normally when the rollback succeeded.
    /// <para>
    /// One rollback failure is tolerated: when <paramref name="commitError"/> is a provider error that carries a SQLSTATE (the
    /// server answered, for example a 40001 serialization failure at commit, so the provider already ended the transaction)
    /// and the rollback fails with an <see cref="InvalidOperationException"/> (the driver's "transaction has completed" state;
    /// an <see cref="ObjectDisposedException"/> is excluded). No driver message text is read. A lost connection or any other
    /// failure is still reported and thrown, and the tolerated case is logged at Debug when a logger is given.
    /// </para>
    /// </summary>
    internal static async Task RollbackAfterFailureAsync(
        IDbContextTransaction transaction,
        Exception commitError,
        string failureMessage,
        Action<Exception> reportRollbackFailure,
        ILogger? logger = null)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException completed) when (completed is not ObjectDisposedException && HasServerAnswer(commitError))
        {
            // The server answered the commit with an error, so the provider transaction is over (Npgsql completes it) and
            // there is nothing left to roll back; the commit error is the one the caller rethrows.
            logger?.LogDebug(completed, "Rollback skipped: the provider transaction had already ended after a database error");
        }
        catch (Exception rollbackError)
        {
            reportRollbackFailure(rollbackError);
            throw new AggregateException(failureMessage, commitError, rollbackError);
        }
    }

    /// <summary>True when the error, or an exception it wraps, is a <see cref="DbException"/> with a non-empty SQLSTATE.</summary>
    private static bool HasServerAnswer(Exception? error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: { Length: > 0 } })
            {
                return true;
            }
        }

        return false;
    }
}
