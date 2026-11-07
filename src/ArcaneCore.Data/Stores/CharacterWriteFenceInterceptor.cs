using System.Data.Common;
using ArcaneCore.Data.Characters.Ownership;
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
/// every context; thread-safe. In Worker fencing scope (the verifying token's scope is <c>worker:&lt;shard&gt;</c>) and under a <see cref="CharacterWriteClaim"/>
/// scope it also proves, in the same transaction, that this worker still holds the per-character claim of each named character
/// (<see cref="CharacterClaimGuard"/>); a refused claim throws <see cref="CharacterClaimRejectedException"/>. In Realm scope, or with no claim scope in force, it issues no claim SQL.
/// A synchronous transaction (<c>SaveChanges</c>, <c>Database.BeginTransaction</c>) cannot be verified, because the guard and the claim check have no synchronous form: <see cref="TransactionStarted"/> rolls it back and throws <see cref="NotSupportedException"/> (fail closed) unless the work is <see cref="CharacterWriteFence.Exempt"/>.
/// Registered only when <c>Cluster:Fencing:Enabled</c> is true, so a fence-off context has no interceptor and issues the same SQL as before. A store save that carries a snapshot token (only a fenced world mints one) pays one scope object per transaction.
/// </para>
/// </summary>
public sealed class CharacterWriteFenceInterceptor(ICharacterOwnershipContext ownership) : DbTransactionInterceptor, ISaveChangesInterceptor
{
    /// <summary>
    /// Asynchronous transaction start: runs the ownership guard (and, in Worker scope under a claim scope, the per-character claim check) inside the new transaction before the
    /// caller's first statement. A refusal or failure is reported, the transaction is rolled back and the exception rethrown. Exempt work passes through unchecked.
    /// </summary>
    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        if (CharacterWriteFence.IsExempt)
            return result;

        try
        {
            await CharacterOwnershipGuard.VerifyOnTransactionAsync(Require(eventData), connection, result, Token(), cancellationToken).ConfigureAwait(false);
            await VerifyClaimAsync(eventData, connection, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(ex);
            await AbandonAsync(result).ConfigureAwait(false);
            throw;
        }

        return result;
    }

    /// <summary>
    /// Synchronous transaction start on a characters context: refused. It is rolled back and a <see cref="NotSupportedException"/> is thrown, because the ownership guard and the claim
    /// check have no synchronous form and blocking a pool thread on them risks a deadlock. Exempt work (<see cref="CharacterWriteFence.IsExempt"/>) passes through unchecked.
    /// </summary>
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        if (CharacterWriteFence.IsExempt)
            return result;

        // A synchronous transaction cannot be verified: the guard and the claim check have no synchronous form, and blocking a pool thread on them risks a deadlock. Fail closed.
        var refusal = new NotSupportedException("characters writes must use the async API (SaveChangesAsync / BeginTransactionAsync); the write fence cannot verify a synchronous transaction.");
        try
        {
            result.Rollback();
        }
        catch (Exception ex)
        {
            // The refusal is the error to report; a failed rollback of a transaction that is being discarded must not replace it.
            FaultLedger.Shared.Record("data.character-write-fence-rollback", ex);
            AnomalyLedger.Shared.Report("database", "pragma-failed", "character-write-fence-rollback", ex.Message, AnomalySeverity.Warning);
        }
        finally
        {
            result.Dispose();
        }

        Report(refusal);
        throw refusal;
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

    /// <summary>The per-character claim check: only for a worker-shard token and only while a <see cref="CharacterWriteClaim"/> scope is in force.</summary>
    private Task VerifyClaimAsync(TransactionEndEventData eventData, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        => CharacterWriteClaim.Current is { } frame && Token() is { } token && CharacterOwnershipScopes.IsWorker(token.Scope)
            ? CharacterClaimGuard.VerifyAsync(Require(eventData), connection, transaction, token, frame, cancellationToken)
            : Task.CompletedTask;

    /// <summary>Record a refusal of the write fence. A lost per-character claim is routine (the character moved to another world), so it is not an anomaly.</summary>
    private static void Report(Exception ex)
    {
        if (ex is CharacterClaimRejectedException)
            return;

        FaultLedger.Shared.Record("data.character-write-fence", ex);
        AnomalyLedger.Shared.Report("database", "pragma-failed", "character-write-fence", ex.Message, AnomalySeverity.Warning);
    }

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
