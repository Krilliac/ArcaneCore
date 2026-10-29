using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Cluster.Social;
using Microsoft.EntityFrameworkCore;
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
/// </summary>
public sealed class EfRealmAuthorityLeaseStore(IServiceScopeFactory scopes, TimeSpan? commandTimeout = null) : IRealmAuthorityLeaseStore
{
    private CharacterDbContext Open(IServiceScope serviceScope)
    {
        CharacterDbContext db = serviceScope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        if (commandTimeout is { } timeout)
        {
            db.Database.SetCommandTimeout(timeout);
        }

        return db;
    }

    public AuthorityLeaseResult TryAcquire(string scope, string ownerNodeId, Guid ownerIncarnation, TimeSpan ttl, DateTimeOffset nowUtc)
    {
        ValidateScopeAndOwner(scope, ownerNodeId);
        long now = nowUtc.ToUnixTimeMilliseconds();
        long expires = now + TtlMs(ttl);
        string incarnation = ownerIncarnation.ToString("D");
        using IServiceScope serviceScope = scopes.CreateScope();
        CharacterDbContext db = Open(serviceScope);
        using IDisposable exempt = CharacterWriteFence.Exempt();
        using var tx = db.Database.BeginTransaction();

        int renewed = db.Set<RealmAuthorityLeaseRow>()
            .Where(r => r.Scope == scope && r.Active && r.ExpiresAtUnixMs > now && r.OwnerNode == ownerNodeId && r.OwnerIncarnation == incarnation)
            .ExecuteUpdate(s => s.SetProperty(r => r.ExpiresAtUnixMs, expires));
        if (renewed > 0)
        {
            RealmAuthorityLeaseRow row = Read(db, scope)!;
            tx.Commit();
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.Renewed, ToLease(row));
        }

        int granted = db.Set<RealmAuthorityLeaseRow>()
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
        using IServiceScope serviceScope = scopes.CreateScope();
        CharacterDbContext db = Open(serviceScope);
        using IDisposable exempt = CharacterWriteFence.Exempt();
        using var tx = db.Database.BeginTransaction();

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

    public AuthorityLeaseResult TryRelease(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken)
    {
        ValidateScopeAndOwner(scope, ownerNodeId);
        string incarnation = ownerIncarnation.ToString("D");
        using IServiceScope serviceScope = scopes.CreateScope();
        CharacterDbContext db = Open(serviceScope);
        using IDisposable exempt = CharacterWriteFence.Exempt();
        using var tx = db.Database.BeginTransaction();

        int released = db.Set<RealmAuthorityLeaseRow>()
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
        using IServiceScope serviceScope = scopes.CreateScope();
        CharacterDbContext db = Open(serviceScope);
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
        using IServiceScope serviceScope = scopes.CreateScope();
        CharacterDbContext db = Open(serviceScope);
        using IDisposable exempt = CharacterWriteFence.Exempt();
        return db.Set<RealmAuthorityLeaseRow>().AsNoTracking()
            .Any(r => r.Scope == scope && r.FencingToken == fencingToken && r.Active && r.ExpiresAtUnixMs > now
                && r.OwnerNode == ownerNodeId && r.OwnerIncarnation == incarnation);
    }

    public RealmAuthorityLease? GetCurrent(string scope, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return null;
        using IServiceScope serviceScope = scopes.CreateScope();
        CharacterDbContext db = Open(serviceScope);
        using IDisposable exempt = CharacterWriteFence.Exempt();
        return Read(db, scope) is { } row ? ActiveLease(row, nowUtc.ToUnixTimeMilliseconds()) : null;
    }

    public long GetHighWaterToken(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return 0;
        using IServiceScope serviceScope = scopes.CreateScope();
        CharacterDbContext db = Open(serviceScope);
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
