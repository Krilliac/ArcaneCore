using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Cluster.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests.Cluster;

/// <summary>
/// The lease store's own connection pool and its commit mode. A disk stall on PostgreSQL's WAL made every commit take seconds; renewals went through the general pool, one transaction
/// per scope, each waiting for a WAL flush, and a round outlasted the keeper's deadline. Renewals now use a pool of their own (a distinct connection string) and, on PostgreSQL, do not wait for the flush.
/// </summary>
public sealed class LeaseRenewalPoolTests : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    /// <summary>Runs only with a PostgreSQL server in the environment; otherwise it is REPORTED SKIPPED, never silently passed.</summary>
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestDatabases.PostgresVariable)))
                Skip = $"Set {TestDatabases.PostgresVariable} to a PostgreSQL server connection string (no database) to run this test.";
        }
    }

    private static DatabaseConnectionOptions Postgres(string connectionString)
        => new() { Provider = DatabaseProvider.PostgreSql, ConnectionString = connectionString };

    [Fact]
    public void DedicatedContextFactory_IsNullOffPostgres_SoOtherProvidersKeepTheGeneralPool()
    {
        Assert.Null(EfRealmAuthorityLeaseStore.DedicatedContextFactory(
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=x.db" }, "arcane-lease", 2, TimeSpan.FromSeconds(3)));
        Assert.Null(EfRealmAuthorityLeaseStore.DedicatedContextFactory(
            new DatabaseConnectionOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = "Server=h;Database=d" }, "arcane-lease", 2, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void DedicatedContextFactory_OnPostgres_GivesADistinctPool_WithASmallSizeAndAShortConnectTimeout()
    {
        const string general = "Host=db.example.invalid;Database=characters;Username=u;Password=p;Maximum Pool Size=100";
        Func<CharacterDbContext>? factory = EfRealmAuthorityLeaseStore.DedicatedContextFactory(Postgres(general), "arcane-lease", 2, TimeSpan.FromSeconds(3));

        Assert.NotNull(factory);
        using CharacterDbContext db = factory();
        string leaseString = db.Database.GetConnectionString()!;
        var lease = new NpgsqlConnectionStringBuilder(leaseString);

        Assert.NotEqual(general, leaseString); // Npgsql keys pools by connection string: a different string is a different pool
        Assert.Equal("arcane-lease", lease.ApplicationName);
        Assert.Equal(2, lease.MaxPoolSize);
        Assert.Equal(0, lease.MinPoolSize);
        Assert.Equal(3, lease.Timeout);
        Assert.Equal(3, lease.CommandTimeout); // the string-level timeout reaches COMMIT, EF's per-command one does not
        Assert.True(lease.Pooling);
        Assert.Equal("db.example.invalid", lease.Host);
        Assert.Equal("characters", lease.Database);

        // The same factory yields contexts of that one pool, and the general string is untouched.
        using CharacterDbContext second = factory();
        Assert.Equal(leaseString, second.Database.GetConnectionString());
        Assert.Equal(100, new NpgsqlConnectionStringBuilder(general).MaxPoolSize);
    }

    [Fact]
    public async Task WithADedicatedFactory_EveryCallUsesItAndNeverTheGeneralScope()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);

        int created = 0;
        var general = new ServiceCollection();
        general.AddScoped<CharacterDbContext>(_ => throw new InvalidOperationException("the general pool must not be used"));
        var store = new EfRealmAuthorityLeaseStore(
            general.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromSeconds(3),
            () =>
            {
                Interlocked.Increment(ref created);
                return TestContexts.Create<CharacterDbContext>(connection);
            });
        Guid incarnation = Guid.NewGuid();

        AuthorityLeaseResult granted = store.TryAcquire(RealmAuthorityScopes.Social, "node-a", incarnation, Ttl, T0);
        AuthorityLeaseResult renewed = store.TryRenew(RealmAuthorityScopes.Social, "node-a", incarnation, granted.Lease!.FencingToken, Ttl, T0 + TimeSpan.FromSeconds(10));
        AuthorityLeaseResult released = store.TryRelease(RealmAuthorityScopes.Social, "node-a", incarnation, granted.Lease.FencingToken);

        Assert.Equal(AuthorityLeaseOutcome.Granted, granted.Outcome);
        Assert.Equal(AuthorityLeaseOutcome.Renewed, renewed.Outcome);
        Assert.Equal(T0 + TimeSpan.FromSeconds(10) + Ttl, renewed.Lease!.ExpiresAtUtc);
        Assert.Equal(AuthorityLeaseOutcome.Released, released.Outcome);
        Assert.Equal(3, created);
    }

    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return result;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return result;
        }
    }

    /// <summary>
    /// On a real PostgreSQL: the renewal's transaction sets <c>synchronous_commit = off</c> (SET LOCAL, before the UPDATE) and the acquire and release do not; and the relaxed commit does not weaken the fence:
    /// after the lease lapses, a takeover bumps the fencing token and the former holder's commit fence and renewal are refused.
    /// </summary>
    [PostgresFact]
    public async Task OnPostgres_RenewalCommitsAsynchronously_AcquireDoesNot_AndTakeoverStillFencesTheFormerHolder()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.PostgreSql);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);

        var log = new CommandLog();
        Func<CharacterDbContext> factory = EfRealmAuthorityLeaseStore.DedicatedContextFactory(connection, "arcane-lease-test", 2, TimeSpan.FromSeconds(5))!;
        Assert.NotNull(factory);
        // The dedicated pool's options carry no interceptor, so observe the same statements through a context built on the same string plus the log.
        string leaseString;
        using (CharacterDbContext probe = factory())
            leaseString = probe.Database.GetConnectionString()!;
        DbContextOptions<CharacterDbContext> observed = new DbContextOptionsBuilder<CharacterDbContext>().UseNpgsql(leaseString).AddInterceptors(log).Options;
        var store = new EfRealmAuthorityLeaseStore(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeSpan.FromSeconds(5), () => new CharacterDbContext(observed));
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        AuthorityLeaseResult granted = store.TryAcquire(RealmAuthorityScopes.Social, "node-a", a, Ttl, T0);
        Assert.DoesNotContain(log.Commands, c => c.Contains("synchronous_commit", StringComparison.Ordinal));

        AuthorityLeaseResult renewed = store.TryRenew(RealmAuthorityScopes.Social, "node-a", a, granted.Lease!.FencingToken, Ttl, T0 + TimeSpan.FromSeconds(10));
        Assert.Equal(AuthorityLeaseOutcome.Renewed, renewed.Outcome);
        int set = log.Commands.FindIndex(c => c.Contains("SET LOCAL synchronous_commit = off", StringComparison.Ordinal));
        int update = log.Commands.FindLastIndex(c => c.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
        Assert.True(set >= 0, "the renewal must set synchronous_commit off for its transaction");
        Assert.True(set < update, "SET LOCAL must come before the renewal's UPDATE");

        // Expiry, then a takeover: the token moves on, and the former holder is refused by the commit fence and by its own renewal.
        AuthorityLeaseResult takeover = store.TryAcquire(RealmAuthorityScopes.Social, "node-b", b, Ttl, T0 + TimeSpan.FromSeconds(10) + Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(AuthorityLeaseOutcome.Granted, takeover.Outcome);
        Assert.True(takeover.Lease!.FencingToken > granted.Lease.FencingToken);
        var former = new RealmLeaseFence(RealmAuthorityScopes.Social, "node-a", a, granted.Lease.FencingToken);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        await using (var tx = await db.Database.BeginTransactionAsync())
            await Assert.ThrowsAsync<RealmLeaseFenceLostException>(() => LeaseFencedWrite.EnsureHeldAsync(db, former, CancellationToken.None));
        Assert.NotEqual(
            AuthorityLeaseOutcome.Renewed,
            store.TryRenew(RealmAuthorityScopes.Social, "node-a", a, granted.Lease.FencingToken, Ttl, T0 + TimeSpan.FromSeconds(50)).Outcome);
    }
}
