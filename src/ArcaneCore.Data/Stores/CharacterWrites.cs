using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// Puts a set-based characters-database write (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>/raw SQL, which EF never wraps in a transaction)
/// under the write fence. With the fence on (the context carries <see cref="CharacterWriteFenceInterceptor"/>) the write runs inside a
/// transaction that EF begins through <see cref="CharacterWriteFence.BeginTransactionAsync(DbContext, CharacterOwnershipToken?, CancellationToken)"/>,
/// so the ownership token is verified before the first statement and the whole write commits or rolls back together. With the fence off the
/// delegate is simply awaited: no transaction, no extra statement, the SQL is exactly what it was before this helper existed.
/// <para>
/// A write that is already inside a transaction (EF's or an ambient <see cref="System.Transactions.Transaction"/>) joins it; its owner decides
/// the outcome and has already verified. The helper is stateless; the context and its transaction stay on the caller's logical call (a
/// <see cref="DbContext"/> is single-threaded). Fence-off cost: one delegate and one scan of the context's few interceptors.
/// </para>
/// </summary>
internal static class CharacterWrites
{
    /// <summary>Whether <paramref name="db"/> was configured with the characters write fence (<c>Cluster:Fencing:Enabled</c>).</summary>
    public static bool IsFenced(DbContext db)
    {
        IEnumerable<IInterceptor>? interceptors = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors is null)
            return false;
        foreach (IInterceptor interceptor in interceptors)
        {
            if (interceptor is CharacterWriteFenceInterceptor)
                return true;
        }

        return false;
    }

    /// <summary>Runs <paramref name="write"/> in a verified transaction when the fence is on, plainly otherwise. <paramref name="token"/> outranks the ambient token when not null.</summary>
    public static async Task InTransactionAsync(
        DbContext db, Func<Task> write, CancellationToken cancellationToken, CharacterOwnershipToken? token = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(write);
        if (!NeedsTransaction(db))
        {
            await write().ConfigureAwait(false);
            return;
        }

        await using IDbContextTransaction transaction = await CharacterWriteFence.BeginTransactionAsync(db, token, cancellationToken).ConfigureAwait(false);
        await write().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="InTransactionAsync(DbContext, Func{Task}, CancellationToken, CharacterOwnershipToken?)"/>
    public static async Task<T> InTransactionAsync<T>(
        DbContext db, Func<Task<T>> write, CancellationToken cancellationToken, CharacterOwnershipToken? token = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(write);
        if (!NeedsTransaction(db))
            return await write().ConfigureAwait(false);

        await using IDbContextTransaction transaction = await CharacterWriteFence.BeginTransactionAsync(db, token, cancellationToken).ConfigureAwait(false);
        T result = await write().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static bool NeedsTransaction(DbContext db)
        => IsFenced(db) && db.Database.CurrentTransaction is null && System.Transactions.Transaction.Current is null;
}
