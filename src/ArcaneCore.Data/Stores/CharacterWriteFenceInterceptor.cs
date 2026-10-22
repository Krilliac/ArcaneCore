using System.Data.Common;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// The one guard for every characters-database write transaction. When EF starts a transaction on a characters context this runs
/// <see cref="CharacterOwnershipGuard"/> inside it (before the caller's first statement), so a store that never heard of ownership is
/// still fenced: a process whose claim was taken over fails at <c>BEGIN</c>, writes nothing, and the transaction is rolled back.
/// <para>
/// Which token: a snapshot's own token, when its store opened the transaction through <see cref="CharacterWriteFence.UseExplicit"/>,
/// outranks the process's ambient <see cref="ICharacterOwnershipContext.Current"/>; with neither, the guard's missing-token rule applies
/// (the database must be unowned). Exempt: the ownership store, the schema bootstrap, the deleted-character lifetime fence and
/// read-only snapshots, which mark themselves with <see cref="CharacterWriteFence.Exempt"/>.
/// </para>
/// <para>
/// It also implements <see cref="ISaveChangesInterceptor"/> for one reason: EF only opens a transaction for a multi-statement
/// <c>SaveChanges</c>, so a one-row save would otherwise autocommit unverified; <c>SavingChanges</c> sets
/// <see cref="AutoTransactionBehavior.Always"/> on the context so every save begins (and verifies) a transaction. A transaction that EF adopts with
/// <c>UseTransaction</c> is not verified here (only the read-only snapshots do that). Stateless apart from the immutable holder, so one instance serves
/// every context; thread-safe. Registered only when <c>Cluster:Fencing:Enabled</c> is true, so a fence-off context has no interceptor and issues the same SQL as before. A store save that carries a snapshot token (only a fenced world mints one) pays one scope object per transaction.
/// </para>
/// </summary>
public sealed class CharacterWriteFenceInterceptor(ICharacterOwnershipContext ownership) : DbTransactionInterceptor, ISaveChangesInterceptor
{
    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        if (CharacterWriteFence.IsExempt)
            return result;

        try
        {
            await CharacterOwnershipGuard.VerifyOnTransactionAsync(Require(eventData), connection, result, Token(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FaultLedger.Shared.Record("data.character-write-fence", ex);
            AnomalyLedger.Shared.Report("database", "pragma-failed", "character-write-fence", ex.Message, AnomalySeverity.Warning);
            await AbandonAsync(result).ConfigureAwait(false);
            throw;
        }

        return result;
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        if (CharacterWriteFence.IsExempt)
            return result;

        try
        {
            // A synchronous SaveChanges: the guard has no synchronous form, and the connection is not shared with a synchronization context.
            CharacterOwnershipGuard.VerifyOnTransactionAsync(Require(eventData), connection, result, Token(), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            FaultLedger.Shared.Record("data.character-write-fence", ex);
            AnomalyLedger.Shared.Report("database", "pragma-failed", "character-write-fence", ex.Message, AnomalySeverity.Warning);
            AbandonAsync(result).AsTask().GetAwaiter().GetResult();
            throw;
        }

        return result;
    }

    InterceptionResult<int> ISaveChangesInterceptor.SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Always(eventData);
        return result;
    }

    ValueTask<InterceptionResult<int>> ISaveChangesInterceptor.SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
    {
        Always(eventData);
        return new(result);
    }

    private static void Always(DbContextEventData eventData)
    {
        if (eventData.Context is { } db)
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
    }

    private CharacterOwnershipToken? Token() => CharacterWriteFence.Explicit ?? ownership.Current;

    private static DbContext Require(TransactionEndEventData eventData)
        => eventData.Context ?? throw new InvalidOperationException("a characters transaction started without a context; the write fence cannot verify it");

    /// <summary>Roll back and release a transaction the fence refused, so a refused write holds no lock (SQLite's write lock in particular).</summary>
    private static async ValueTask AbandonAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The refusal is the error to report; a failed rollback of a transaction that is being discarded must not replace it.
            FaultLedger.Shared.Record("data.character-write-fence-rollback", ex);
            AnomalyLedger.Shared.Report("database", "pragma-failed", "character-write-fence-rollback", ex.Message, AnomalySeverity.Warning);
        }

        await transaction.DisposeAsync().ConfigureAwait(false);
    }
}
