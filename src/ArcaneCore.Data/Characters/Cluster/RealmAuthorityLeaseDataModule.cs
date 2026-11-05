using ArcaneCore.Data;
using System.Data.Common;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Cluster.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Cluster;

/// <summary>One realm-singleton authority lease (M15.2). Times are Unix milliseconds: SQLite EF cannot compare <see cref="DateTimeOffset"/> in SQL.</summary>
public sealed class RealmAuthorityLeaseRow
{
    public string Scope { get; set; } = string.Empty;
    public string? OwnerNode { get; set; }
    public string? OwnerIncarnation { get; set; }

    /// <summary>The highest fencing token ever issued for the scope; never decremented, so it survives a release.</summary>
    public long FencingToken { get; set; }
    public long AcquiredAtUnixMs { get; set; }
    public long ExpiresAtUnixMs { get; set; }
    public bool Active { get; set; }
}

/// <summary>
/// Characters schema 67: the durable realm-singleton lease table (<c>realm_authority_lease</c>), per realm next to
/// <c>character_ownership</c>. Nothing reads or writes it unless a node runs as a non-Combined cluster role (<c>Cluster:Node:Role</c>).
/// </summary>
public sealed class RealmAuthorityLeaseDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 67;
    public const string Table = "realm_authority_lease";

    public static readonly IReadOnlyList<string> Scopes =
        [RealmAuthorityScopes.Economy, RealmAuthorityScopes.Social, RealmAuthorityScopes.Instances];

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(Table),
        .. Scopes.Select(scope => new EnsureRowChange(Table, "scope", scope, new Dictionary<string, object?>
        {
            ["owner_node"] = null,
            ["owner_incarnation"] = null,
            ["fencing_token"] = 0L,
            ["acquired_at_unix_ms"] = 0L,
            ["expires_at_unix_ms"] = 0L,
            ["active"] = false,
        })),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RealmAuthorityLeaseRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Scope);
            entity.Property(r => r.Scope).HasColumnName("scope").HasMaxLength(32).ValueGeneratedNever();
            entity.Property(r => r.OwnerNode).HasColumnName("owner_node").HasMaxLength(128);
            entity.Property(r => r.OwnerIncarnation).HasColumnName("owner_incarnation").HasMaxLength(36);
            entity.Property(r => r.FencingToken).HasColumnName("fencing_token");
            entity.Property(r => r.AcquiredAtUnixMs).HasColumnName("acquired_at_unix_ms");
            entity.Property(r => r.ExpiresAtUnixMs).HasColumnName("expires_at_unix_ms");
            entity.Property(r => r.Active).HasColumnName("active");
            // Fixtures that use EnsureCreated instead of the schema bootstrapper still need the scope rows.
            entity.HasData(Scopes.Select(scope => new RealmAuthorityLeaseRow { Scope = scope }));
        });
    }

    // Registers nothing: only a node with a cluster role (Cluster:Node:Role other than Combined) uses the store, and AddClusterNodeRole registers it.
    public void AddServices(IServiceCollection services)
    {
    }

    // Leases belong to nodes, not characters.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>
/// <see cref="IRealmAuthorityLeaseStore"/> over the characters database. Every call opens its own scope and context, so the store is a singleton
/// that any thread may call; each decision is one conditional UPDATE inside a transaction, so two nodes on one database cannot both be granted.
/// Fencing tokens are the row's high-water mark and only ever grow. <paramref name="commandTimeout"/> bounds every database command (null: the provider
/// default), so a hung database fails a renewal in time for the keeper to stop the node before the lease lapses.
/// <para>PostgreSQL only: with <paramref name="contextFactory"/> (acquire, renew, release) and <paramref name="readContextFactory"/> (<see cref="GetCurrent"/>, <see cref="ValidateFencingToken(string, string, long, DateTimeOffset)"/>)
/// (see <see cref="DedicatedContextFactory"/>) the calls use contexts of those factories instead of the general context pool, so the lease connections are their own pools and a stalled general pool, or a burst of
/// readers (the peer locator polls <see cref="GetCurrent"/> every second), cannot queue a renewal. MariaDB and SQLite keep the general pool, and MariaDB keeps the coupling described next.</para>
/// <para>Row locks on PostgreSQL (the fence in <see cref="LeaseFencedWrite"/> takes <c>FOR KEY SHARE</c> on the lease row and holds it to the end of the writer's transaction): a renewal changes only
/// <c>expires_at_unix_ms</c> (the primary key is <c>scope</c> alone, there is no other unique index), so its UPDATE takes <c>FOR NO KEY UPDATE</c>, which does not conflict with <c>FOR KEY SHARE</c>: a renewal never waits for a
/// writer's commit. Every change of authority (owner, incarnation, fencing token, active: <see cref="TryAcquire"/>, which is also the takeover, and <see cref="TryRelease"/>) first takes <c>SELECT ... FOR UPDATE</c> on the row in the
/// same transaction (only when the call could change the row: a refused acquire or a release by a non-holder locks nothing), which does conflict with <c>FOR KEY SHARE</c>, so it waits for in-flight fenced commits and a fenced write is always ordered wholly before or wholly after it. A plain UPDATE there would take only
/// <c>FOR NO KEY UPDATE</c> and silently break the fence. On MariaDB the fence's shared lock still conflicts with every UPDATE of the row, so a renewal there still waits for in-flight fenced commits.</para>
/// <para>A renewal on PostgreSQL commits with <c>synchronous_commit = off</c> (see <see cref="TryRenew"/>).</para>
/// </summary>
public sealed class EfRealmAuthorityLeaseStore(
    IServiceScopeFactory scopes, TimeSpan? commandTimeout = null, Func<CharacterDbContext>? contextFactory = null, Func<CharacterDbContext>? readContextFactory = null) : IRealmAuthorityLeaseStore
{
    /// <summary>A context for one call and what must be disposed with it: a scope of the general pool, or a context of the dedicated lease pool (the store's context factory).</summary>
    private sealed class LeaseDb(CharacterDbContext db, IServiceScope? scope) : IDisposable
    {
        public CharacterDbContext Db { get; } = db;

        public void Dispose()
        {
            if (scope is null)
                Db.Dispose(); // a context of the dedicated factory is ours; a scoped one is returned with its scope
            scope?.Dispose();
        }
    }

    private LeaseDb Open(bool read = false)
    {
        IServiceScope? serviceScope = null;
        CharacterDbContext db;
        Func<CharacterDbContext>? factory = read ? readContextFactory ?? contextFactory : contextFactory;
        if (factory is not null)
        {
            db = factory();
        }
        else
        {
            serviceScope = scopes.CreateScope();
            db = serviceScope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        }

        if (commandTimeout is { } timeout)
        {
            db.Database.SetCommandTimeout(timeout);
        }

        return new LeaseDb(db, serviceScope);
    }

    /// <summary>
    /// A factory of contexts that connect through their OWN connection pool, or null when the provider is not PostgreSQL (then the store uses the general context pool; MariaDB is deliberately unchanged). Npgsql pools
    /// connections per connection string, so the string gets a distinct application name (visible in pg_stat_activity), a small <paramref name="maxPoolSize"/> and a short connect/pool-wait timeout equal to
    /// <paramref name="commandTimeout"/>, which is also the string's <c>Command Timeout</c>: EF's per-command timeout does not reach the internal commands (COMMIT, ROLLBACK), the string-level one does. A general pool that is
    /// saturated or stalled cannot starve a renewal, and the node's lease connections stay a handful (at most <paramref name="maxPoolSize"/> per process and factory). The contexts carry no interceptors: every lease call
    /// is exempt from the characters write fence anyway. The options are built once and shared.
    /// </summary>
    public static Func<CharacterDbContext>? DedicatedContextFactory(DatabaseConnectionOptions connection, string applicationName, int maxPoolSize, TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Provider != DatabaseProvider.PostgreSql || string.IsNullOrWhiteSpace(connection.ConnectionString))
            return null;

        int seconds = Math.Max(1, (int)Math.Ceiling(commandTimeout.TotalSeconds));
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection.ConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = Math.Max(1, maxPoolSize),
            Timeout = seconds,
            CommandTimeout = seconds,
        };
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseNpgsql(builder.ConnectionString).Options;
        return () => new CharacterDbContext(options);
    }

    /// <summary>
    /// PostgreSQL only: <c>SELECT 1 FROM realm_authority_lease WHERE scope = @scope AND (<paramref name="condition"/>) FOR UPDATE</c> in the current transaction, true when it locked the row. The condition is the
    /// store's own condition for the change the call is about to make, so only a row this call could change is locked: <c>FOR UPDATE</c> conflicts with the fence's <c>FOR KEY SHARE</c>, so a change of authority waits for
    /// in-flight fenced commits (bounded by the command timeout), and a call that will be refused takes no row lock at all (it neither waits behind the holder's writers, nor blocks the holder's renewal and writers, nor
    /// gets a transaction id that would make its commit wait for the WAL). Read Committed re-evaluates the condition after any wait. The caller must skip its UPDATEs when this returns false: a concurrent change
    /// (a release committing in between) could make the UPDATE match without the lock. Other providers take no lock here and return true: the UPDATEs then run exactly as before.
    /// </summary>
    private static bool TryLockRow(CharacterDbContext db, string scope, string condition, params (string Name, object Value)[] parameters)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return true;

        using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (db.Database.GetCommandTimeout() is { } seconds)
            command.CommandTimeout = seconds;
        command.CommandText = $"SELECT 1 FROM {RealmAuthorityLeaseDataModule.Table} WHERE scope = @scope AND ({condition}) FOR UPDATE";
        AddParameter(command, "@scope", scope);
        foreach ((string name, object value) in parameters)
            AddParameter(command, name, value);
        return command.ExecuteScalar() is not null;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public AuthorityLeaseResult TryAcquire(string scope, string ownerNodeId, Guid ownerIncarnation, TimeSpan ttl, DateTimeOffset nowUtc)
    {
        ValidateScopeAndOwner(scope, ownerNodeId);
        long now = nowUtc.ToUnixTimeMilliseconds();
        long expires = now + TtlMs(ttl);
        string incarnation = ownerIncarnation.ToString("D");
        using LeaseDb lease = Open();
        CharacterDbContext db = lease.Db;
        using IDisposable exempt = CharacterWriteFence.Exempt();
        using var tx = db.Database.BeginTransaction();
        // Acquire and takeover change authority: on PostgreSQL lock the row (FOR UPDATE, waits for in-flight fenced commits) only when one of the two UPDATEs below could change it: the lease is free or lapsed (and the
        // token can grow), or it is this owner's own (the renewal branch). A call that will be refused locks nothing and skips both UPDATEs.
        bool locked = TryLockRow(db, scope,
            "(owner_node = @node AND owner_incarnation = @incarnation) OR ((NOT active OR expires_at_unix_ms <= @now) AND fencing_token < @maxToken)",
            ("@node", ownerNodeId), ("@incarnation", incarnation), ("@now", now), ("@maxToken", long.MaxValue));

        int renewed = !locked ? 0 : db.Set<RealmAuthorityLeaseRow>()
            .Where(r => r.Scope == scope && r.Active && r.ExpiresAtUnixMs > now && r.OwnerNode == ownerNodeId && r.OwnerIncarnation == incarnation)
            .ExecuteUpdate(s => s.SetProperty(r => r.ExpiresAtUnixMs, expires));
        if (renewed > 0)
        {
            RealmAuthorityLeaseRow row = Read(db, scope)!;
            tx.Commit();
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.Renewed, ToLease(row));
        }

        int granted = !locked ? 0 : db.Set<RealmAuthorityLeaseRow>()
            .Where(r => r.Scope == scope && (!r.Active || r.ExpiresAtUnixMs <= now) && r.FencingToken < long.MaxValue)
            .ExecuteUpdate(s => s
                .SetProperty(r => r.OwnerNode, ownerNodeId)
                .SetProperty(r => r.OwnerIncarnation, incarnation)
                .SetProperty(r => r.FencingToken, r => r.FencingToken + 1)
                .SetProperty(r => r.AcquiredAtUnixMs, now)
                .SetProperty(r => r.ExpiresAtUnixMs, expires)
                .SetProperty(r => r.Active, true));
        RealmAuthorityLeaseRow current = Read(db, scope) ?? throw UnknownScope(scope);
        tx.Commit();
        if (granted > 0)
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.Granted, ToLease(current));
        return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedHeldByOther, ActiveLease(current, now));
    }

    public AuthorityLeaseResult TryRenew(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken, TimeSpan ttl, DateTimeOffset nowUtc)
    {
        ValidateScopeAndOwner(scope, ownerNodeId);
        long now = nowUtc.ToUnixTimeMilliseconds();
        long expires = now + TtlMs(ttl);
        string incarnation = ownerIncarnation.ToString("D");
        using LeaseDb lease = Open();
        CharacterDbContext db = lease.Db;
        using IDisposable exempt = CharacterWriteFence.Exempt();
        using var tx = db.Database.BeginTransaction();
        RelaxCommitDurability(db);

        int renewed = db.Set<RealmAuthorityLeaseRow>()
            .Where(r => r.Scope == scope && r.FencingToken == fencingToken && r.Active && r.ExpiresAtUnixMs > now
                && r.OwnerNode == ownerNodeId && r.OwnerIncarnation == incarnation)
            .ExecuteUpdate(s => s.SetProperty(r => r.ExpiresAtUnixMs, expires));
        RealmAuthorityLeaseRow row = Read(db, scope) ?? throw UnknownScope(scope);
        tx.Commit();
        if (renewed > 0)
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.Renewed, ToLease(row));

        // The same answers as InMemoryRealmAuthorityLeaseStore: a token that is not the row's, or a lease that lapsed or was released, is stale;
        // the right token held by someone else is held-by-other.
        RealmAuthorityLease? active = ActiveLease(row, now);
        if (row.FencingToken != fencingToken || active is null)
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, active);
        bool sameOwner = row.OwnerNode == ownerNodeId && row.OwnerIncarnation == incarnation;
        return new AuthorityLeaseResult(sameOwner ? AuthorityLeaseOutcome.RejectedStaleToken : AuthorityLeaseOutcome.RejectedHeldByOther, active);
    }

    /// <summary>
    /// PostgreSQL only: this transaction's commit does not wait for the WAL flush (<c>SET LOCAL synchronous_commit = off</c>, scoped to the transaction, so it does not outlive it on a pooled connection). A stalled WAL disk made
    /// every commit take seconds and a five-scope round outlast the keeper's deadline. It stays safe because a renewal's durability is not what fences: the row is visible to every session at once, and if PostgreSQL crashes
    /// the renewals not yet flushed are lost, which leaves the row showing an OLDER expiry than the node believes. The loss is not bounded by time: normally the WAL writer flushes within about three
    /// <c>wal_writer_delay</c> (200 ms by default), but while the WAL disk stalls the unflushed tail grows for as long as the stall lasts. What bounds the damage is not the flush: a takeover (<see cref="TryAcquire"/>, which commits
    /// synchronously) still needs <c>!active || expires &lt;= now</c> on the row and always increments the fencing token; the commit fence (<see cref="LeaseFencedWrite"/>) compares token and owner, never the expiry,
    /// so a deposed node is refused at commit by every fenced store; and the keeper stops the node at its own local deadline. A store with no commit check can write in the gap (see the residual in <see cref="LeaseFencedWrite"/>).
    /// WAL is flushed in order, so a later synchronous commit (a takeover, a release) also makes every earlier renewal durable.
    /// </summary>
    private static void RelaxCommitDurability(CharacterDbContext db)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            db.Database.ExecuteSqlRaw("SET LOCAL synchronous_commit = off");
    }

    public AuthorityLeaseResult TryRelease(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken)
    {
        ValidateScopeAndOwner(scope, ownerNodeId);
        string incarnation = ownerIncarnation.ToString("D");
        using LeaseDb lease = Open();
        CharacterDbContext db = lease.Db;
        using IDisposable exempt = CharacterWriteFence.Exempt();
        using var tx = db.Database.BeginTransaction();
        // A release changes authority: on PostgreSQL lock the row (waits for in-flight fenced commits) only when the UPDATE below would match it; a release by a non-holder locks nothing and skips the UPDATE.
        bool locked = TryLockRow(db, scope,
            "fencing_token = @token AND active AND owner_node = @node AND owner_incarnation = @incarnation",
            ("@token", fencingToken), ("@node", ownerNodeId), ("@incarnation", incarnation));

        int released = !locked ? 0 : db.Set<RealmAuthorityLeaseRow>()
            .Where(r => r.Scope == scope && r.FencingToken == fencingToken && r.Active && r.OwnerNode == ownerNodeId && r.OwnerIncarnation == incarnation)
            .ExecuteUpdate(s => s
                .SetProperty(r => r.OwnerNode, (string?)null)
                .SetProperty(r => r.OwnerIncarnation, (string?)null)
                .SetProperty(r => r.Active, false));
        RealmAuthorityLeaseRow row = Read(db, scope) ?? throw UnknownScope(scope);
        tx.Commit();
        if (released > 0)
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.Released, null);
        return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, row.Active ? ToLease(row) : null);
    }

    public bool ValidateFencingToken(string scope, string ownerNodeId, long fencingToken, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(ownerNodeId) || fencingToken <= 0)
            return false;
        long now = nowUtc.ToUnixTimeMilliseconds();
        using LeaseDb lease = Open(read: true);
        CharacterDbContext db = lease.Db;
        using IDisposable exempt = CharacterWriteFence.Exempt();
        return db.Set<RealmAuthorityLeaseRow>().AsNoTracking()
            .Any(r => r.Scope == scope && r.FencingToken == fencingToken && r.Active && r.ExpiresAtUnixMs > now && r.OwnerNode == ownerNodeId);
    }

    public bool ValidateFencingToken(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(ownerNodeId) || fencingToken <= 0)
            return false;
        long now = nowUtc.ToUnixTimeMilliseconds();
        string incarnation = ownerIncarnation.ToString("D");
        using LeaseDb lease = Open(read: true);
        CharacterDbContext db = lease.Db;
        using IDisposable exempt = CharacterWriteFence.Exempt();
        return db.Set<RealmAuthorityLeaseRow>().AsNoTracking()
            .Any(r => r.Scope == scope && r.FencingToken == fencingToken && r.Active && r.ExpiresAtUnixMs > now
                && r.OwnerNode == ownerNodeId && r.OwnerIncarnation == incarnation);
    }

    public RealmAuthorityLease? GetCurrent(string scope, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return null;
        using LeaseDb lease = Open(read: true);
        CharacterDbContext db = lease.Db;
        using IDisposable exempt = CharacterWriteFence.Exempt();
        return Read(db, scope) is { } row ? ActiveLease(row, nowUtc.ToUnixTimeMilliseconds()) : null;
    }

    public long GetHighWaterToken(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return 0;
        using LeaseDb lease = Open(read: true);
        CharacterDbContext db = lease.Db;
        using IDisposable exempt = CharacterWriteFence.Exempt();
        return Read(db, scope)?.FencingToken ?? 0;
    }

    private static RealmAuthorityLeaseRow? Read(CharacterDbContext db, string scope)
        => db.Set<RealmAuthorityLeaseRow>().AsNoTracking().SingleOrDefault(r => r.Scope == scope);

    private static RealmAuthorityLease? ActiveLease(RealmAuthorityLeaseRow row, long nowUnixMs)
        => row.Active && row.ExpiresAtUnixMs > nowUnixMs ? ToLease(row) : null;

    private static RealmAuthorityLease ToLease(RealmAuthorityLeaseRow row) => new(
        row.Scope,
        row.OwnerNode ?? string.Empty,
        Guid.TryParse(row.OwnerIncarnation, out Guid incarnation) ? incarnation : Guid.Empty,
        row.FencingToken,
        DateTimeOffset.FromUnixTimeMilliseconds(row.AcquiredAtUnixMs),
        DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresAtUnixMs));

    private static long TtlMs(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Lease TTL must be positive.");
        return (long)ttl.TotalMilliseconds;
    }

    private static void ValidateScopeAndOwner(string scope, string ownerNodeId)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must be non-empty.", nameof(scope));
        if (string.IsNullOrWhiteSpace(ownerNodeId) || ownerNodeId.Length > 128)
            throw new ArgumentException("OwnerNodeId must be non-empty and at most 128 characters.", nameof(ownerNodeId));
    }

    private static ArgumentException UnknownScope(string scope)
        => new($"the realm authority lease row '{scope}' does not exist; use one of {string.Join(", ", RealmAuthorityLeaseDataModule.Scopes)}", nameof(scope));
}
