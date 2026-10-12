using System.Data;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// The per-call state of the characters write fence: which token a write transaction is verified against and which code is exempt.
/// Both values flow with the logical call (<see cref="AsyncLocal{T}"/>), so they reach the interceptors that EF runs inside
/// <c>BeginTransactionAsync</c> and inside an executed command without any store passing them down. Nothing here is shared between
/// calls; a scope restores the previous value when disposed, so scopes nest and a caller's scope is never lost.
/// </summary>
internal static class CharacterWriteFence
{
    private static readonly AsyncLocal<CharacterOwnershipToken?> ExplicitToken = new();
    private static readonly AsyncLocal<int> ExemptDepth = new();

    /// <summary>The token a snapshot carried into the current call, or null when the call is verified against the ambient process token.</summary>
    public static CharacterOwnershipToken? Explicit => ExplicitToken.Value;

    public static bool IsExempt => ExemptDepth.Value > 0;

    /// <summary>
    /// Marks the current call exempt from the fence: the ownership store (it is the fence), the schema bootstrap (it runs before
    /// the ownership row can be read), the lifetime fence of a deleted character and read-only snapshots.
    /// </summary>
    public static IDisposable Exempt()
    {
        ExemptDepth.Value++;
        return new Scope(static () => ExemptDepth.Value--);
    }

    /// <summary>Verify the transactions begun inside this scope against <paramref name="token"/> instead of the ambient one; a null token changes nothing.</summary>
    public static IDisposable UseExplicit(CharacterOwnershipToken? token)
    {
        CharacterOwnershipToken? previous = ExplicitToken.Value;
        if (token is not null)
            ExplicitToken.Value = token;
        return new Scope(() => ExplicitToken.Value = previous);
    }

    /// <summary>Begin a transaction whose start-of-transaction verification uses the snapshot's own token when it has one.</summary>
    public static async Task<IDbContextTransaction> BeginTransactionAsync(DbContext db, CharacterOwnershipToken? token, CancellationToken cancellationToken)
    {
        using (UseExplicit(token))
            return await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="BeginTransactionAsync(DbContext, CharacterOwnershipToken?, CancellationToken)"/>
    public static async Task<IDbContextTransaction> BeginTransactionAsync(DbContext db, CharacterOwnershipToken? token, IsolationLevel isolation, CancellationToken cancellationToken)
    {
        using (UseExplicit(token))
            return await db.Database.BeginTransactionAsync(isolation, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
