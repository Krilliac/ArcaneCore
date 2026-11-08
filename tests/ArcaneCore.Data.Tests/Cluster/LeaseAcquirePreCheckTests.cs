using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Cluster.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Cluster;

/// <summary>
/// <see cref="EfRealmAuthorityLeaseStore.TryAcquire"/> on SQLite and MariaDB (PostgreSQL has its conditional <c>FOR UPDATE</c> in TryLockRow and is not covered here): a call another node's live lease refuses is
/// answered from a read-only look and runs no UPDATE, so it takes no write lock (SQLite's single writer, MariaDB's exclusive row lock behind the holder's fenced writers). Calls that can change the row still do.
/// Unproven until the integration gate runs it; MariaDB only on hosted CI.
/// </summary>
public sealed class LeaseAcquirePreCheckTests : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    public static IEnumerable<object[]> NonPostgresProviders() => TestDatabases.AvailableProviders().Where(p => (DatabaseProvider)p[0] != DatabaseProvider.PostgreSql);

    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public string[] Snapshot()
        {
            lock (_commands)
                return [.. _commands];
        }

        public void Clear()
        {
            lock (_commands)
                _commands.Clear();
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            lock (_commands)
                _commands.Add(command.CommandText);
            return result;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            lock (_commands)
                _commands.Add(command.CommandText);
            return result;
        }
    }

    private async Task<(EfRealmAuthorityLeaseStore Store, CommandLog Log)> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);

        var log = new CommandLog();
        var builder = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, connection);
        DbContextOptions<CharacterDbContext> options = builder.AddInterceptors(log).Options;
        var store = new EfRealmAuthorityLeaseStore(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), null, () => new CharacterDbContext(options));
        return (store, log);
    }

    private static bool HasUpdate(IEnumerable<string> commands) => commands.Any(c => c.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [MemberData(nameof(NonPostgresProviders))]
    public async Task AnAcquireRefusedByALiveLease_RunsNoUpdate_AndReportsTheHolder(DatabaseProvider provider)
    {
        (EfRealmAuthorityLeaseStore store, CommandLog log) = await CreateAsync(provider);
        Guid a = Guid.NewGuid();
        AuthorityLeaseResult granted = store.TryAcquire(RealmAuthorityScopes.Social, "node-a", a, Ttl, T0);
        Assert.Equal(AuthorityLeaseOutcome.Granted, granted.Outcome);
        log.Clear();

        AuthorityLeaseResult refused = store.TryAcquire(RealmAuthorityScopes.Social, "node-b", Guid.NewGuid(), Ttl, T0 + TimeSpan.FromSeconds(5));

        Assert.Equal(AuthorityLeaseOutcome.RejectedHeldByOther, refused.Outcome);
        Assert.Equal("node-a", refused.Lease!.OwnerNodeId);
        Assert.Equal(granted.Lease!.FencingToken, refused.Lease.FencingToken);
        Assert.NotEmpty(log.Snapshot()); // the look itself ran
        Assert.False(HasUpdate(log.Snapshot()), "a refused acquire must not write or lock the row: " + string.Join(" | ", log.Snapshot()));
    }

    [Theory]
    [MemberData(nameof(NonPostgresProviders))]
    public async Task AnAcquireThatCanChangeTheRow_StillRunsItsUpdates(DatabaseProvider provider)
    {
        (EfRealmAuthorityLeaseStore store, CommandLog log) = await CreateAsync(provider);
        Guid a = Guid.NewGuid();
        Assert.Equal(AuthorityLeaseOutcome.Granted, store.TryAcquire(RealmAuthorityScopes.Social, "node-a", a, Ttl, T0).Outcome);

        log.Clear(); // the same owner and incarnation: a renewal in place
        AuthorityLeaseResult renewed = store.TryAcquire(RealmAuthorityScopes.Social, "node-a", a, Ttl, T0 + TimeSpan.FromSeconds(10));
        Assert.Equal(AuthorityLeaseOutcome.Renewed, renewed.Outcome);
        Assert.True(HasUpdate(log.Snapshot()));

        log.Clear(); // another node after the lease lapsed: a takeover with a higher token
        AuthorityLeaseResult takeover = store.TryAcquire(RealmAuthorityScopes.Social, "node-b", Guid.NewGuid(), Ttl, T0 + TimeSpan.FromSeconds(10) + Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(AuthorityLeaseOutcome.Granted, takeover.Outcome);
        Assert.Equal(renewed.Lease!.FencingToken + 1, takeover.Lease!.FencingToken);
        Assert.True(HasUpdate(log.Snapshot()));
    }

    [Theory]
    [MemberData(nameof(NonPostgresProviders))]
    public async Task ThePreCheck_KeepsTheUnknownScopeError(DatabaseProvider provider)
    {
        (EfRealmAuthorityLeaseStore store, _) = await CreateAsync(provider);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => store.TryAcquire("no-such-scope", "node-a", Guid.NewGuid(), Ttl, T0));

        Assert.Equal("scope", ex.ParamName);
    }
}
