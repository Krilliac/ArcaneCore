using System.Data.Common;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Characters.Cluster;

/// <summary>The lease a writer claims to hold: the scope, the owner node and incarnation, and the fencing token it was granted.</summary>
public readonly record struct RealmLeaseFence(string Scope, string OwnerNodeId, Guid OwnerIncarnation, long FencingToken);

/// <summary>
/// Tells a store which realm-authority lease this process holds. Implemented by the lease keeper of a clustered node; a process with no keeper (the Combined role, tests)
/// registers none, and its writes are not fenced. Must be thread-safe and cheap: it is asked before every singleton write.
/// </summary>
public interface IRealmLeaseFenceSource
{
    /// <summary>The fence for <paramref name="scope"/>, or null when this process holds no lease for it (then it is not the singleton writer and nothing is checked).</summary>
    RealmLeaseFence? FenceFor(string scope);
}

/// <summary>
/// A realm-singleton write was refused because the writer lease is no longer the one in the lease table: another node took the scope over (the fencing token moved on),
/// or the lease was released. Nothing was written. An ownership rejection, so callers that stop on those stop on this; the epoch is the stale token.
/// </summary>
public sealed class RealmLeaseFenceLostException(string scope, string ownerNodeId, long fencingToken)
    : CharacterOwnershipRejectedException(scope, fencingToken,
        $"the realm authority lease '{scope}' of node {ownerNodeId} at fencing token {fencingToken} is no longer held; the write was refused")
{
    /// <summary>The node whose write was refused.</summary>
    public string OwnerNodeId { get; } = ownerNodeId;
}

/// <summary>
/// A realm-singleton write was refused because this node does not hold the scope's lease at all (another node does, or none was ever taken, or it was released). Nothing was written.
/// Unlike <see cref="RealmLeaseFenceLostException"/> it says the node was never the writer, so the caller skips or forwards the operation instead of treating it as a takeover.
/// </summary>
public sealed class RealmLeaseNotHeldException(string scope)
    : CharacterOwnershipRejectedException(scope, 0, $"this node does not hold the realm authority lease '{scope}'; the realm-wide write was refused")
{
}

/// <summary>
/// Closes the gap between the lease keeper's fail-stop timer and the database: a realm-singleton write (economy, social, instances) verifies, inside the transaction
/// that makes the write, that the <c>realm_authority_lease</c> row still carries the writer's fencing token, owner and incarnation, and holds that row against a takeover until the
/// transaction ends (a row write on SQLite, whose single writer then excludes a takeover; a shared row lock on PostgreSQL and MariaDB, the way <c>CharacterOwnershipGuard</c> does).
/// A partitioned former holder whose lease was taken over is therefore refused at commit, whatever its clock says. The check ignores the lease expiry on purpose: an expired
/// lease nobody has taken over yet is still the only one that could be written under, and the keeper's timer handles that case.
/// <para>Stateless; every call works on the caller's context, which stays on the caller's logical call (a context is single-threaded).</para>
/// </summary>
public static class LeaseFencedWrite
{
    /// <summary>Throws <see cref="RealmLeaseFenceLostException"/> unless the lease row still matches <paramref name="fence"/>. Call it inside the transaction of the write.</summary>
    public static async Task EnsureHeldAsync(CharacterDbContext db, RealmLeaseFence fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        string incarnation = fence.OwnerIncarnation.ToString("D");
        bool held;
        using (CharacterWriteFence.Exempt())
        {
            if (db.Database.IsSqlite())
            {
                held = await db.Set<RealmAuthorityLeaseRow>()
                    .Where(r => r.Scope == fence.Scope && r.FencingToken == fence.FencingToken && r.Active
                        && r.OwnerNode == fence.OwnerNodeId && r.OwnerIncarnation == incarnation)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAtUnixMs, r => r.ExpiresAtUnixMs), cancellationToken)
                    .ConfigureAwait(false) > 0;
            }
            else
            {
                held = await LockSharedAsync(db, fence, incarnation, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!held)
            throw new RealmLeaseFenceLostException(fence.Scope, fence.OwnerNodeId, fence.FencingToken);
    }

    /// <summary>
    /// Refuses a realm-singleton write from a node that is not the scope's holder. With no <paramref name="source"/> (the Combined role, tests: nothing is clustered, so this process is
    /// the only writer) it does nothing; with a source that reports no lease for <paramref name="scope"/> it throws <see cref="RealmLeaseNotHeldException"/>. This is the gap
    /// <see cref="InTransactionAsync{T}"/> leaves on purpose (a non-holder's write goes through unchecked there). Call it before the write; the write itself is still verified against the lease row
    /// by <see cref="InTransactionAsync{T}"/> or <see cref="EnsureHeldAsync"/>. Synchronous and cheap (one in-memory lookup).
    /// </summary>
    public static void RequireHeld(IRealmLeaseFenceSource? source, string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        if (source is not null && source.FenceFor(scope) is null)
            throw new RealmLeaseNotHeldException(scope);
    }

    /// <summary>
    /// Runs <paramref name="write"/> after verifying the lease <paramref name="source"/> reports for <paramref name="scope"/>, in one transaction (joining the caller's when there is one,
    /// whose owner then commits). With no source, or a source that holds no lease for the scope, the delegate is simply awaited.
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(
        CharacterDbContext db, IRealmLeaseFenceSource? source, string scope, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(write);
        if (source?.FenceFor(scope) is not { } fence)
            return await write().ConfigureAwait(false);

        if (db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            await EnsureHeldAsync(db, fence, cancellationToken).ConfigureAwait(false);
            return await write().ConfigureAwait(false);
        }

        await using IDbContextTransaction transaction = await CharacterWriteFence.BeginTransactionAsync(db, null, cancellationToken).ConfigureAwait(false);
        await EnsureHeldAsync(db, fence, cancellationToken).ConfigureAwait(false);
        T result = await write().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The same as the generic overload, for a write with no result.</summary>
    public static async Task InTransactionAsync(
        CharacterDbContext db, IRealmLeaseFenceSource? source, string scope, Func<Task> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        await InTransactionAsync<bool>(db, source, scope, async () =>
        {
            await write().ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>SaveChangesAsync</c> under the verified transaction of <see cref="InTransactionAsync{T}"/>.</summary>
    public static Task SaveChangesAsync(CharacterDbContext db, IRealmLeaseFenceSource? source, string scope, CancellationToken cancellationToken)
        => InTransactionAsync<int>(db, source, scope, () => db.SaveChangesAsync(cancellationToken), cancellationToken);

    private static async Task<bool> LockSharedAsync(CharacterDbContext db, RealmLeaseFence fence, string incarnation, CancellationToken cancellationToken)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        string lockClause = db.Database.ProviderName switch
        {
            "Pomelo.EntityFrameworkCore.MySql" => " LOCK IN SHARE MODE",
            "Npgsql.EntityFrameworkCore.PostgreSQL" => " FOR SHARE",
            _ => throw new NotSupportedException($"no lease fence lock for provider {db.Database.ProviderName}"),
        };

        await DbConnectionOpen.EnsureOpenAsync(db.Database, cancellationToken).ConfigureAwait(false);
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT 1 FROM {sql.DelimitIdentifier(RealmAuthorityLeaseDataModule.Table)} "
            + $"WHERE {sql.DelimitIdentifier("scope")} = @scope AND {sql.DelimitIdentifier("fencing_token")} = @token AND {sql.DelimitIdentifier("active")} = TRUE "
            + $"AND {sql.DelimitIdentifier("owner_node")} = @node AND {sql.DelimitIdentifier("owner_incarnation")} = @incarnation{lockClause}";
        Add(command, "@scope", fence.Scope);
        Add(command, "@token", fence.FencingToken);
        Add(command, "@node", fence.OwnerNodeId);
        Add(command, "@incarnation", incarnation);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
