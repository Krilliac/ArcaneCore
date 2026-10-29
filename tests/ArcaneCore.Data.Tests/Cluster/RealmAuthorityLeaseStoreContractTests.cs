using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Cluster.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Cluster;

/// <summary>
/// One contract for the in-memory and the characters-database <see cref="IRealmAuthorityLeaseStore"/> (M15.2): a single holder per scope,
/// strictly higher fencing tokens across takeovers, and a release that keeps the high-water mark.
/// </summary>
public sealed class RealmAuthorityLeaseStoreContractTests : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    public static IEnumerable<object[]> Stores()
    {
        yield return ["memory", DatabaseProvider.Sqlite];
        foreach (object[] provider in TestDatabases.AvailableProviders())
            yield return ["ef", provider[0]];
    }

    private async Task<IRealmAuthorityLeaseStore> CreateAsync(string kind, DatabaseProvider provider)
        => kind == "memory" ? new InMemoryRealmAuthorityLeaseStore() : await CreateEfAsync(provider);

    private async Task<EfRealmAuthorityLeaseStore> CreateEfAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
        return StoreOn(connection);
    }

    private static EfRealmAuthorityLeaseStore StoreOn(DatabaseConnectionOptions connection)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => TestContexts.Create<CharacterDbContext>(connection));
        return new EfRealmAuthorityLeaseStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Acquire_GrantsToken1_SecondNodeRejected_SameOwnerRenewsInPlace(string kind, DatabaseProvider provider)
    {
        IRealmAuthorityLeaseStore store = await CreateAsync(kind, provider);
        Guid incarnation = Guid.NewGuid();

        AuthorityLeaseResult first = store.TryAcquire(RealmAuthorityScopes.Economy, "node-a", incarnation, Ttl, T0);
        Assert.Equal(AuthorityLeaseOutcome.Granted, first.Outcome);
        Assert.Equal(1, first.Lease!.FencingToken);
        Assert.Equal("node-a", first.Lease.OwnerNodeId);
        Assert.Equal(incarnation, first.Lease.OwnerIncarnation);

        AuthorityLeaseResult other = store.TryAcquire(RealmAuthorityScopes.Economy, "node-b", Guid.NewGuid(), Ttl, T0.AddSeconds(1));
        Assert.Equal(AuthorityLeaseOutcome.RejectedHeldByOther, other.Outcome);
        Assert.Equal("node-a", other.Lease!.OwnerNodeId);
        Assert.Equal(1, other.Lease.FencingToken);

        AuthorityLeaseResult renewed = store.TryAcquire(RealmAuthorityScopes.Economy, "node-a", incarnation, Ttl, T0.AddSeconds(10));
        Assert.Equal(AuthorityLeaseOutcome.Renewed, renewed.Outcome);
        Assert.Equal(1, renewed.Lease!.FencingToken);
        Assert.Equal(T0.AddSeconds(40), renewed.Lease.ExpiresAtUtc);

        // A restarted process on the same node name is a different incarnation: it does not inherit the lease.
        AuthorityLeaseResult restarted = store.TryAcquire(RealmAuthorityScopes.Economy, "node-a", Guid.NewGuid(), Ttl, T0.AddSeconds(11));
        Assert.Equal(AuthorityLeaseOutcome.RejectedHeldByOther, restarted.Outcome);

        // Scopes are independent.
        Assert.Equal(AuthorityLeaseOutcome.Granted, store.TryAcquire(RealmAuthorityScopes.Social, "node-b", Guid.NewGuid(), Ttl, T0).Outcome);
        Assert.True(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "node-a", 1, T0.AddSeconds(12)));
        // The incarnation-aware check: two processes configured with one node id cannot both pass.
        Assert.True(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "node-a", incarnation, 1, T0.AddSeconds(12)));
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "node-a", Guid.NewGuid(), 1, T0.AddSeconds(12)));
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "node-a", incarnation, 2, T0.AddSeconds(12)));
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "node-a", incarnation, 1, T0 + Ttl + TimeSpan.FromSeconds(11)));
        Assert.Equal(1, store.GetHighWaterToken(RealmAuthorityScopes.Economy));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Expiry_LetsAnotherNodeTakeOver_WithStrictlyHigherToken_AndOldTokenFailsValidateAndRenew(string kind, DatabaseProvider provider)
    {
        IRealmAuthorityLeaseStore store = await CreateAsync(kind, provider);
        Guid oldIncarnation = Guid.NewGuid(), newIncarnation = Guid.NewGuid();
        Assert.Equal(1, store.TryAcquire(RealmAuthorityScopes.Social, "node-a", oldIncarnation, Ttl, T0).Lease!.FencingToken);

        DateTimeOffset later = T0 + Ttl + TimeSpan.FromSeconds(1);
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Social, "node-a", 1, later)); // lapsed, not yet taken over
        Assert.Null(store.GetCurrent(RealmAuthorityScopes.Social, later));

        AuthorityLeaseResult takeover = store.TryAcquire(RealmAuthorityScopes.Social, "node-b", newIncarnation, Ttl, later);
        Assert.Equal(AuthorityLeaseOutcome.Granted, takeover.Outcome);
        Assert.Equal(2, takeover.Lease!.FencingToken);

        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Social, "node-a", 1, later));
        Assert.True(store.ValidateFencingToken(RealmAuthorityScopes.Social, "node-b", 2, later));
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Social, "node-b", 1, later));
        Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken,
            store.TryRenew(RealmAuthorityScopes.Social, "node-a", oldIncarnation, 1, Ttl, later).Outcome);
        Assert.Equal(AuthorityLeaseOutcome.Renewed,
            store.TryRenew(RealmAuthorityScopes.Social, "node-b", newIncarnation, 2, Ttl, later.AddSeconds(5)).Outcome);

        // A lease that lapsed cannot be renewed by its own holder either.
        Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken,
            store.TryRenew(RealmAuthorityScopes.Social, "node-b", newIncarnation, 2, Ttl, later.AddSeconds(5) + Ttl + TimeSpan.FromSeconds(1)).Outcome);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Release_ByNonOwnerOrStaleToken_IsRejected_ByOwner_PreservesHighWater(string kind, DatabaseProvider provider)
    {
        IRealmAuthorityLeaseStore store = await CreateAsync(kind, provider);
        Guid incarnation = Guid.NewGuid();
        store.TryAcquire(RealmAuthorityScopes.Economy, "node-a", incarnation, Ttl, T0);

        Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken,
            store.TryRelease(RealmAuthorityScopes.Economy, "node-b", incarnation, 1).Outcome);
        Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken,
            store.TryRelease(RealmAuthorityScopes.Economy, "node-a", Guid.NewGuid(), 1).Outcome);
        Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken,
            store.TryRelease(RealmAuthorityScopes.Economy, "node-a", incarnation, 7).Outcome);
        Assert.True(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "node-a", 1, T0.AddSeconds(1)));

        Assert.Equal(AuthorityLeaseOutcome.Released, store.TryRelease(RealmAuthorityScopes.Economy, "node-a", incarnation, 1).Outcome);
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "node-a", 1, T0.AddSeconds(1)));
        Assert.Null(store.GetCurrent(RealmAuthorityScopes.Economy, T0.AddSeconds(1)));
        Assert.Equal(1, store.GetHighWaterToken(RealmAuthorityScopes.Economy));

        // Released twice is not a second release.
        Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken, store.TryRelease(RealmAuthorityScopes.Economy, "node-a", incarnation, 1).Outcome);

        // The next acquisition continues above the preserved high-water mark.
        AuthorityLeaseResult next = store.TryAcquire(RealmAuthorityScopes.Economy, "node-b", Guid.NewGuid(), Ttl, T0.AddSeconds(2));
        Assert.Equal(AuthorityLeaseOutcome.Granted, next.Outcome);
        Assert.Equal(2, next.Lease!.FencingToken);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task UnknownScope_Throws(string kind, DatabaseProvider provider)
    {
        IRealmAuthorityLeaseStore store = await CreateAsync(kind, provider);

        Assert.Throws<ArgumentException>(() => store.TryAcquire(" ", "node-a", Guid.NewGuid(), Ttl, T0));
        Assert.Throws<ArgumentException>(() => store.TryAcquire(RealmAuthorityScopes.Economy, "", Guid.NewGuid(), Ttl, T0));
        Assert.Throws<ArgumentException>(() => store.TryRenew("", "node-a", Guid.NewGuid(), 1, Ttl, T0));
        Assert.Throws<ArgumentException>(() => store.TryRelease("", "node-a", Guid.NewGuid(), 1));

        // Validation of bad arguments answers false instead of throwing.
        Assert.False(store.ValidateFencingToken("", "node-a", 1, T0));
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "", 1, T0));
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "node-a", 0, T0));
        Assert.Null(store.GetCurrent("", T0));
        Assert.Equal(0, store.GetHighWaterToken(""));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EfStore_ScopeWithoutARow_Throws_ReadsAreEmpty(DatabaseProvider provider)
    {
        EfRealmAuthorityLeaseStore store = await CreateEfAsync(provider);

        Assert.Throws<ArgumentException>(() => store.TryAcquire("no-such-scope", "node-a", Guid.NewGuid(), Ttl, T0));
        Assert.Throws<ArgumentException>(() => store.TryRenew("no-such-scope", "node-a", Guid.NewGuid(), 1, Ttl, T0));
        Assert.Throws<ArgumentException>(() => store.TryRelease("no-such-scope", "node-a", Guid.NewGuid(), 1));
        Assert.Null(store.GetCurrent("no-such-scope", T0));
        Assert.Equal(0, store.GetHighWaterToken("no-such-scope"));
        Assert.False(store.ValidateFencingToken("no-such-scope", "node-a", 1, T0));
    }

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TwoStoresOnOneDatabaseFile_OnlyOneIsGranted(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
        EfRealmAuthorityLeaseStore a = StoreOn(connection), b = StoreOn(connection);

        for (int round = 0; round < 5; round++)
        {
            string scope = round % 2 == 0 ? RealmAuthorityScopes.Economy : RealmAuthorityScopes.Social;
            DateTimeOffset now = T0.AddMinutes(round * 2); // each round starts after the previous lease lapsed
            Task<AuthorityLeaseResult> ra = Task.Run(() => a.TryAcquire(scope, "node-a", Guid.NewGuid(), Ttl, now));
            Task<AuthorityLeaseResult> rb = Task.Run(() => b.TryAcquire(scope, "node-b", Guid.NewGuid(), Ttl, now));
            AuthorityLeaseResult[] results = await Task.WhenAll(ra, rb);

            Assert.Single(results, r => r.Outcome == AuthorityLeaseOutcome.Granted);
            Assert.Single(results, r => r.Outcome == AuthorityLeaseOutcome.RejectedHeldByOther);
            AuthorityLeaseResult granted = results.Single(r => r.Outcome == AuthorityLeaseOutcome.Granted);
            Assert.True(a.ValidateFencingToken(scope, granted.Lease!.OwnerNodeId, granted.Lease.FencingToken, now.AddSeconds(1)));
            Assert.Equal(granted.Lease.FencingToken, b.GetHighWaterToken(scope));
        }
    }

    [Fact]
    public void TheModule_IsACharactersModule_WithSeededScopes_AndNoopCleanup()
    {
        var module = new RealmAuthorityLeaseDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal("realm_authority_lease", RealmAuthorityLeaseDataModule.Table);
        Assert.Contains(CharacterDataCleanups.All, c => c.GetType() == module.GetType());
        Assert.Equal(
            [RealmAuthorityScopes.Economy, RealmAuthorityScopes.Instances, RealmAuthorityScopes.Social],
            module.SchemaChanges.OfType<EnsureRowChange>().Select(c => (string)c.KeyValue).Order());
    }

    [Fact]
    public void TheModule_RegistersNothing_SoCombinedAddsNoLeaseServices()
    {
        var services = new ServiceCollection();
        new RealmAuthorityLeaseDataModule().AddServices(services);

        Assert.Empty(services);
    }
}
