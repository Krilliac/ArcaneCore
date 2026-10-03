using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Runs several importers as one unit: if any later one fails (or the run is cancelled), the
/// rows every earlier one wrote or replaced are restored. The importers
/// (<c>CreatureDumpImporter.WriteAsync</c>, <c>GameObjectLootDumpImporter.WriteAsync</c>) keep
/// their own contract unchanged — they treat the transaction opened here as a caller's and
/// protect their part with a savepoint — so this adds the outer boundary only.
/// <para>
/// Same preconditions as the importers: an empty change tracker (so batching cannot save
/// unrelated state), and no ambient <c>System.Transactions</c> transaction without an EF one. A
/// transaction already open on the context stays the caller's: this run is protected by a
/// savepoint, which is rolled back on failure and released on success, never committed.
/// </para>
/// </summary>
public static class ImportTransaction
{
    public static async Task RunAsync(DbContext db, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(work);
        if (db.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException("Content imports require an empty change tracker; save caller changes and clear tracking, or use a dedicated context.");
        }

        IDbContextTransaction? callerTransaction = db.Database.CurrentTransaction;
        if (callerTransaction is null && System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Content imports require an explicit EF transaction when a caller owns the transaction; ambient transactions are not supported.");
        }

        if (callerTransaction is { SupportsSavepoints: false })
        {
            throw new InvalidOperationException("The caller's transaction does not support savepoints; the import cannot protect its existing work.");
        }

        await using IDbContextTransaction? ownedTransaction = callerTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        IDbContextTransaction transaction = callerTransaction ?? ownedTransaction!;
        string? savepoint = callerTransaction is not null ? "ArcaneContentImport_" + Guid.NewGuid().ToString("N") : null;
        if (savepoint is not null)
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await work(cancellationToken).ConfigureAwait(false);
            if (savepoint is not null)
            {
                await transaction.ReleaseSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception importError)
        {
            try
            {
                // Cancellation interrupts the import, not the rollback that preserves the
                // previous content. Never roll back a transaction owned by the caller.
                if (savepoint is not null)
                {
                    await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                    await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Content import and rollback failed; discard the context and transaction.", importError, rollbackError);
            }

            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
