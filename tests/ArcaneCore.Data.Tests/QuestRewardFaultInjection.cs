using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArcaneCore.Data.Tests;

/// <summary>A provider-neutral <see cref="DbException"/> that carries a chosen SQLSTATE (40001, 40P01, 23505, ...).</summary>
internal sealed class FakeDbException(string sqlState) : DbException("injected " + sqlState)
{
    public override string? SqlState => sqlState;
}

/// <summary>
/// Throws from the begin, SaveChanges or commit step of a store's transaction, the first <c>failures</c> times,
/// counting the attempts that reach that step. <see cref="EndTransactionFirst"/> makes a commit failure end the
/// provider transaction first (as a server-side abort does), so EF's rollback meets the real driver's "completed" error.
/// <see cref="RollbackFailure"/> makes every rollback EF issues fail.
/// </summary>
internal sealed class InjectedFault
{
    private readonly string _shape;
    private readonly int _failures;
    private readonly Func<Exception> _make;
    private int _attempts;

    public InjectedFault(string shape, int failures, Func<Exception> make)
    {
        _shape = shape;
        _failures = failures;
        _make = make;
        Interceptors = [new Tx(this), new Save(this)];
    }

    public IInterceptor[] Interceptors { get; }

    public List<Exception> Thrown { get; } = [];

    public Action? OnThrow { get; set; }

    public bool EndTransactionFirst { get; set; }

    public Func<Exception>? RollbackFailure { get; set; }

    public int Attempts => Volatile.Read(ref _attempts);

    private void Hit(string step, DbTransaction? transaction = null)
    {
        if (step != _shape)
        {
            return;
        }

        if (Interlocked.Increment(ref _attempts) <= _failures)
        {
            if (EndTransactionFirst)
            {
                transaction?.Rollback();
            }

            Exception error = _make();
            Thrown.Add(error);
            OnThrow?.Invoke();
            throw error;
        }
    }

    private sealed class Tx(InjectedFault owner) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            owner.Hit("begin");
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            owner.Hit("commit", transaction);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
            => owner.RollbackFailure is { } make ? throw make() : ValueTask.FromResult(result);
    }

    private sealed class Save(InjectedFault owner) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            owner.Hit("save");
            return ValueTask.FromResult(result);
        }
    }
}
