using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArcaneCore.Data.Characters.Ownership;

/// <summary>
/// Attaches <see cref="CharacterDatabaseWriterFence"/> to a <c>CharacterDbContext</c>: once the fence is tripped every connection open, transaction start, command (reads included; the
/// first keyword of a statement is deliberately not classified, CTEs, parenthesised selects, setval, CALL and batches all slip past a classifier) and commit throws
/// <see cref="CharacterDatabaseFencedException"/>. The commit hook covers a transaction begun before the trip; a rollback is never refused.
/// Added first, ahead of the SQLite write gate, so a refused call never queues for the gate. The World and Auth contexts are deliberately not fenced (the writer lock covers the characters
/// database only), and <see cref="ServerAdvisoryWriterLock"/> uses its own raw connection, so the fence cannot touch the lock. Stateless apart from the fence; thread-safe.
/// </summary>
public sealed class CharacterDatabaseWriterFenceInterceptor(CharacterDatabaseWriterFence fence) : DbTransactionInterceptor, IDbConnectionInterceptor, IDbCommandInterceptor
{
    // ---- connections ----

    public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        fence.ThrowIfTripped();
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        fence.ThrowIfTripped();
        return new ValueTask<InterceptionResult>(result);
    }

    // ---- transactions ----

    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        fence.ThrowIfTripped();
        return result;
    }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        fence.ThrowIfTripped();
        return new ValueTask<InterceptionResult<DbTransaction>>(result);
    }

    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        fence.ThrowIfTripped();
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        fence.ThrowIfTripped();
        return new ValueTask<InterceptionResult>(result);
    }

    // ---- commands ----

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        fence.ThrowIfTripped();
        return result;
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        fence.ThrowIfTripped();
        return new ValueTask<InterceptionResult<DbDataReader>>(result);
    }

    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        fence.ThrowIfTripped();
        return result;
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        fence.ThrowIfTripped();
        return new ValueTask<InterceptionResult<int>>(result);
    }

    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        fence.ThrowIfTripped();
        return result;
    }

    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        fence.ThrowIfTripped();
        return new ValueTask<InterceptionResult<object>>(result);
    }
}
