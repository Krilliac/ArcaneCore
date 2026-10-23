using Microsoft.EntityFrameworkCore.Storage;

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
    /// </summary>
    internal static async Task RollbackAfterFailureAsync(
        IDbContextTransaction transaction,
        Exception commitError,
        string failureMessage,
        Action<Exception> reportRollbackFailure)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackError)
        {
            reportRollbackFailure(rollbackError);
            throw new AggregateException(failureMessage, commitError, rollbackError);
        }
    }
}
