using System.Data.Common;
using System.Linq.Expressions;
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

    /// <summary>
    /// Whether this process is the live holder of <paramref name="scope"/>. The default is "a fence exists", which is right for a source that forgets a lease it gave back; the lease keeper overrides
    /// it, because it keeps a lost lease's old token in <see cref="FenceFor"/> (so the commit fence refuses the write) while the node is no longer the holder.
    /// </summary>
    bool Holds(string scope) => FenceFor(scope) is not null;

    /// <summary>
    /// The maps this process owns (Cluster:Worker:OwnedMaps). A worker's per-map sweeps (the expired respawn deletes) touch only rows of these maps, because another worker owns the rest
    /// (writer-lock design section 4, row 3). The default is every map, which is the Combined and Realm behaviour.
    /// </summary>
    OwnedMapSet OwnedMaps => OwnedMapSet.All;
}

/// <summary>
/// The set of maps one worker owns, as inclusive map id ranges, for the data layer (which cannot see the game layer's map ownership type). An empty list owns every map; a non-empty list owns the listed
/// maps, plus any instance copy (instance id other than 0) while <see cref="AllowInstanceMaps"/> is set. Immutable, so safe from any thread.
/// </summary>
public sealed class OwnedMapSet
{
    /// <summary>Every map: the Combined and Realm behaviour.</summary>
    public static OwnedMapSet All { get; } = new([], allowInstanceMaps: true);

    private readonly (uint First, uint Last)[] _ranges;

    public OwnedMapSet(IEnumerable<(uint First, uint Last)> ranges, bool allowInstanceMaps)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        _ranges = [.. ranges];
        AllowInstanceMaps = allowInstanceMaps;
    }

    /// <summary>True when no map is listed, so every map is owned.</summary>
    public bool OwnsEverything => _ranges.Length == 0;

    /// <summary>Whether instance copies of maps outside the list are owned too (only meaningful for a non-empty list).</summary>
    public bool AllowInstanceMaps { get; }

    /// <summary>Whether this process owns the map copy: everything for an empty list; else a listed map, or any instance copy while <see cref="AllowInstanceMaps"/>.</summary>
    public bool Owns(uint mapId, uint instanceId)
    {
        if (OwnsEverything || (instanceId != 0 && AllowInstanceMaps))
            return true;
        foreach ((uint first, uint last) in _ranges)
        {
            if (mapId >= first && mapId <= last)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A row filter equal to <see cref="Owns"/> for an entity whose map id is a <see cref="uint"/> property named <paramref name="mapIdProperty"/> and whose instance id is an <see cref="int"/>
    /// property named <paramref name="instanceIdProperty"/>, built as a plain comparison tree so EF translates it to SQL. For <see cref="OwnsEverything"/> it is the constant true.
    /// </summary>
    public Expression<Func<TRow, bool>> RowFilter<TRow>(string mapIdProperty, string instanceIdProperty)
    {
        ArgumentException.ThrowIfNullOrEmpty(mapIdProperty);
        ArgumentException.ThrowIfNullOrEmpty(instanceIdProperty);
        ParameterExpression row = Expression.Parameter(typeof(TRow), "r");
        if (OwnsEverything)
            return Expression.Lambda<Func<TRow, bool>>(Expression.Constant(true), row);

        MemberExpression map = Expression.Property(row, mapIdProperty);
        Expression? any = null;
        foreach ((uint first, uint last) in _ranges)
        {
            Expression term = first == last
                ? Expression.Equal(map, Expression.Constant(first, typeof(uint)))
                : Expression.AndAlso(
                    Expression.GreaterThanOrEqual(map, Expression.Constant(first, typeof(uint))),
                    Expression.LessThanOrEqual(map, Expression.Constant(last, typeof(uint))));
            any = any is null ? term : Expression.OrElse(any, term);
        }

        if (AllowInstanceMaps)
            any = Expression.OrElse(any!, Expression.NotEqual(Expression.Property(row, instanceIdProperty), Expression.Constant(0, typeof(int))));

        return Expression.Lambda<Func<TRow, bool>>(any!, row);
    }
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
/// transaction ends (a row write on SQLite, whose single writer then excludes a takeover; a shared row lock on MariaDB, the way <c>CharacterOwnershipGuard</c> does; FOR KEY SHARE on PostgreSQL, which every change of authority excludes by taking FOR UPDATE first).
/// A partitioned former holder whose lease was taken over is therefore refused at commit, whatever its clock says. The check ignores the lease expiry on purpose: an expired
/// lease nobody has taken over yet is still the only one that could be written under, and the keeper's timer handles that case.
/// <para>Two guards, by site: a write that verifies the lease row at commit uses <see cref="RequireFence"/> (the commit fence decides a node the keeper marked lost locally); a write with no commit check uses
/// <see cref="RequireHeld"/>, which also refuses after a local loss. Known residual, accepted: a WAL stall that loses renewals (they commit with synchronous_commit off, see <see cref="EfRealmAuthorityLeaseStore"/>),
/// plus a PostgreSQL crash can let another node (any live map worker: its renewal tick takes a lapsed instances, maintenance or worldstate lease, not only a node at start) take the lease over while this one still believes it holds it; until its next renewal fails, a store behind
/// <see cref="RequireHeld"/> (no commit check) can still write briefly after the takeover. Stores behind <see cref="RequireFence"/> are refused at commit.</para>
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
    /// Refuses a realm-singleton write from a node that is not the scope's live holder. With no <paramref name="source"/> (the Combined role, tests: nothing is clustered, so this process is
    /// the only writer) it does nothing; with a source that does not hold the scope (<see cref="IRealmLeaseFenceSource.Holds"/>: never taken, held by another node, given back, or marked lost by the keeper)
    /// it throws <see cref="RealmLeaseNotHeldException"/>. This is the safe default for every write that has no check of the lease row at commit: a deposed node must not write. This is the gap
    /// <see cref="InTransactionAsync{T}"/> leaves on purpose (a non-holder's write goes through unchecked there). Stores whose write verifies the lease row in its own transaction use <see cref="RequireFence"/>.
    /// Synchronous and cheap (one in-memory lookup).
    /// </summary>
    public static void RequireHeld(IRealmLeaseFenceSource? source, string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        if (!IsHolder(source, scope))
            throw new RealmLeaseNotHeldException(scope);
    }

    /// <summary>
    /// <see cref="RequireHeld"/> for a write that verifies the lease row at commit (<see cref="InTransactionAsync{T}"/>, <see cref="SaveChangesAsync"/> or <see cref="EnsureHeldAsync"/> in the write's own transaction):
    /// refuses only a node that has no fence for <paramref name="scope"/> (never taken, held by another node, given back). A keeper that marked the lease lost locally (a slow renewal round, a transient store failure)
    /// keeps the old token in <see cref="IRealmLeaseFenceSource.FenceFor"/>, so the commit fence decides: it refuses the write exactly when another node really took the lease over and accepts it while the token is still the
    /// row's. Never call it from a write that has no commit-time check: there nothing would stop a deposed node.
    /// <para>Returns the fence it read (null only when there is no <paramref name="source"/>). A write passes that same fence to the commit check (<see cref="EnsureHeldAsync"/>, or the overloads of
    /// <see cref="InTransactionAsync{T}(CharacterDbContext, RealmLeaseFence?, Func{Task{T}}, CancellationToken)"/> and
    /// <see cref="SaveChangesAsync(CharacterDbContext, RealmLeaseFence?, CancellationToken)"/>) instead of asking the source a second time: the keeper forgets its fences in
    /// <c>ReleaseAllAsync</c>, so a second read after a release would be null and the write would go unchecked.</para>
    /// </summary>
    public static RealmLeaseFence? RequireFence(IRealmLeaseFenceSource? source, string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        if (source is null)
            return null;
        return source.FenceFor(scope) ?? throw new RealmLeaseNotHeldException(scope);
    }

    /// <summary>
    /// Whether this process may make the realm-singleton writes of <paramref name="scope"/>: always with no <paramref name="source"/> (nothing is clustered), otherwise only while the source
    /// reports the lease held (<see cref="IRealmLeaseFenceSource.Holds"/>: not released, not lost). Sweeps that should skip quietly on a non-holder or a node that lost the lease (boot cleanups) ask this; writes that must refuse call <see cref="RequireHeld"/> (or <see cref="RequireFence"/> when they verify the lease row at commit).
    /// </summary>
    public static bool IsHolder(IRealmLeaseFenceSource? source, string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        return source is null || source.Holds(scope);
    }

    /// <summary>
    /// <see cref="RequireHeld"/> then <see cref="InTransactionAsync{T}"/>: a node that is not the holder is refused before anything is written, and the holder's write is verified against the lease row
    /// in the transaction that makes it. The one call a realm-singleton store write makes.
    /// </summary>
    public static Task<T> HeldWriteAsync<T>(
        CharacterDbContext db, IRealmLeaseFenceSource? source, string scope, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        RealmLeaseFence? fence = source?.FenceFor(scope); // read before the holder check, so a release between the two cannot leave the write unchecked
        RequireHeld(source, scope);
        return InTransactionAsync(db, fence, write, cancellationToken);
    }

    /// <summary>The same as the generic overload, for a write with no result.</summary>
    public static Task HeldWriteAsync(
        CharacterDbContext db, IRealmLeaseFenceSource? source, string scope, Func<Task> write, CancellationToken cancellationToken)
    {
        RealmLeaseFence? fence = source?.FenceFor(scope);
        RequireHeld(source, scope);
        return InTransactionAsync<bool>(db, fence, async () =>
        {
            await write().ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="write"/> after verifying the lease <paramref name="source"/> reports for <paramref name="scope"/>, in one transaction (joining the caller's when there is one,
    /// whose owner then commits). With no source, or a source that holds no lease for the scope, the delegate is simply awaited.
    /// </summary>
    public static Task<T> InTransactionAsync<T>(
        CharacterDbContext db, IRealmLeaseFenceSource? source, string scope, Func<Task<T>> write, CancellationToken cancellationToken)
        => InTransactionAsync(db, source?.FenceFor(scope), write, cancellationToken);

    /// <summary>
    /// <see cref="InTransactionAsync{T}(CharacterDbContext, IRealmLeaseFenceSource?, string, Func{Task{T}}, CancellationToken)"/> under a fence the caller already read (<see cref="RequireFence"/> returns it), so the
    /// lease is not looked up a second time. A null <paramref name="fence"/> means no check (no source, or a scope this node never held), as the overload without a fence does.
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(
        CharacterDbContext db, RealmLeaseFence? fence, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(write);
        if (fence is not { } held)
            return await write().ConfigureAwait(false);

        if (db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            await EnsureHeldAsync(db, held, cancellationToken).ConfigureAwait(false);
            return await write().ConfigureAwait(false);
        }

        await using IDbContextTransaction transaction = await CharacterWriteFence.BeginTransactionAsync(db, null, cancellationToken).ConfigureAwait(false);
        await EnsureHeldAsync(db, held, cancellationToken).ConfigureAwait(false);
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

    /// <summary><see cref="SaveChangesAsync(CharacterDbContext, IRealmLeaseFenceSource?, string, CancellationToken)"/> under a fence the caller already read (<see cref="RequireFence"/> returns it).</summary>
    public static Task SaveChangesAsync(CharacterDbContext db, RealmLeaseFence? fence, CancellationToken cancellationToken)
        => InTransactionAsync<int>(db, fence, () => db.SaveChangesAsync(cancellationToken), cancellationToken);

    private static async Task<bool> LockSharedAsync(CharacterDbContext db, RealmLeaseFence fence, string incarnation, CancellationToken cancellationToken)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        string lockClause = db.Database.ProviderName switch
        {
            "Pomelo.EntityFrameworkCore.MySql" => " LOCK IN SHARE MODE",
            // FOR KEY SHARE, not FOR SHARE: it conflicts with FOR UPDATE (every change of authority, see EfRealmAuthorityLeaseStore: acquire, takeover, release) but NOT with the FOR NO KEY UPDATE of a
            // renewal (which changes only expires_at_unix_ms), so a renewal never waits for an in-flight fenced writer's commit, and a takeover still waits for it.
            // Why the changes of authority must lock FOR UPDATE first and not just UPDATE: their UPDATE changes no key column, so it takes only FOR NO KEY UPDATE, which does not conflict with FOR KEY SHARE; a
            // FOR KEY SHARE locker skips waiting for an updater unless the tuple's HEAP_KEYS_UPDATED bit is set, and only a FOR UPDATE lock (or an update of a key column) sets it.
            "Npgsql.EntityFrameworkCore.PostgreSQL" => " FOR KEY SHARE",
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
