using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Cluster;

/// <summary>
/// The lease fencing token is checked at commit (<see cref="LeaseFencedWrite"/>): after an expiry and a takeover by another node, the former holder's economy, social and
/// instance writes are refused and nothing is written, while the new holder writes normally. On every available provider.
/// </summary>
public sealed class LeaseCommitFencingTests : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private sealed class Source(Func<string, RealmLeaseFence?> fence) : IRealmLeaseFenceSource
    {
        public RealmLeaseFence? FenceFor(string scope) => fence(scope);
    }

    private sealed record Topology(DatabaseConnectionOptions Connection, IRealmLeaseFenceSource OldHolder, IRealmLeaseFenceSource NewHolder, IRealmLeaseFenceSource Released);

    /// <summary>
    /// Node A takes every scope, its leases expire, node B takes them over (the fencing token moves from 1 to 2). A still believes it holds token 1.
    /// A third source models a holder that released its lease.
    /// </summary>
    private async Task<Topology> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);

        var services = new ServiceCollection();
        services.AddScoped(_ => TestContexts.Create<CharacterDbContext>(connection));
        var leases = new EfRealmAuthorityLeaseStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        Guid incarnationA = Guid.NewGuid();
        Guid incarnationB = Guid.NewGuid();
        Dictionary<string, RealmLeaseFence> a = [];
        Dictionary<string, RealmLeaseFence> b = [];
        foreach (string scope in RealmAuthorityLeaseDataModule.Scopes)
        {
            AuthorityLeaseResult first = leases.TryAcquire(scope, "node-a", incarnationA, Ttl, T0);
            Assert.Equal(AuthorityLeaseOutcome.Granted, first.Outcome);
            a[scope] = new RealmLeaseFence(scope, "node-a", incarnationA, first.Lease!.FencingToken);

            AuthorityLeaseResult takeover = leases.TryAcquire(scope, "node-b", incarnationB, Ttl, T0 + Ttl + TimeSpan.FromSeconds(1));
            Assert.Equal(AuthorityLeaseOutcome.Granted, takeover.Outcome);
            Assert.True(takeover.Lease!.FencingToken > first.Lease.FencingToken);
            b[scope] = new RealmLeaseFence(scope, "node-b", incarnationB, takeover.Lease.FencingToken);
        }

        // A fence for a token that was never the current one, as a holder that released and was never re-granted would carry.
        Dictionary<string, RealmLeaseFence> released = RealmAuthorityLeaseDataModule.Scopes.ToDictionary(
            s => s, s => new RealmLeaseFence(s, "node-c", Guid.NewGuid(), b[s].FencingToken));
        return new Topology(
            connection,
            new Source(s => a.GetValueOrDefault(s) is { } f ? f : null),
            new Source(s => b.GetValueOrDefault(s) is { } f ? f : null),
            new Source(s => released.GetValueOrDefault(s) is { } f ? f : null));
    }

    private static ItemInstanceData Item(uint guid) => new()
    {
        Guid = guid, Entry = 4000, Count = 5, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21], Durability = 30,
    };

    private static AuctionRecord Auction(uint id, uint itemGuid) => new()
    {
        Id = id, HouseId = 7, ItemGuid = itemGuid, ItemEntry = 4000, ItemCount = 5, SellerId = 0, StartBid = 75, Buyout = 100,
        ExpireTime = 1_900_000_000,
    };

    private static EconomyCommitRequest MintRequest(uint guid, uint auctionId)
        => new(Guid.NewGuid(), [], [new MintEscrowItem(Item(guid)), new InsertAuction(Auction(auctionId, guid))]);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Economy_commit_of_a_former_holder_is_refused_and_writes_nothing(DatabaseProvider provider)
    {
        Topology topology = await CreateAsync(provider);

        foreach (IRealmLeaseFenceSource stale in new[] { topology.OldHolder, topology.Released })
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(topology.Connection);
            await Assert.ThrowsAsync<RealmLeaseFenceLostException>(() => new EfEconomyStore(db, stale).CommitAsync(MintRequest(900, 1)));
        }

        await using (CharacterDbContext check = TestContexts.Create<CharacterDbContext>(topology.Connection))
        {
            Assert.Empty(await check.Set<ItemInstanceRow>().AsNoTracking().ToListAsync());
            Assert.Empty(await check.Set<EconomyOperationRow>().AsNoTracking().ToListAsync());
            Assert.Empty((await new EfEconomyStore(check).GetAuctionSnapshotAsync(new AuctionSnapshotFilter())).Auctions);
        }

        await using CharacterDbContext current = TestContexts.Create<CharacterDbContext>(topology.Connection);
        Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(current, topology.NewHolder).CommitAsync(MintRequest(900, 1)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Economy_commit_without_a_fence_source_or_without_a_held_lease_is_not_fenced(DatabaseProvider provider)
    {
        Topology topology = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(topology.Connection))
            Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(MintRequest(900, 1)));

        // This process holds no lease for the scope (it is not the singleton writer): nothing to check.
        await using CharacterDbContext other = TestContexts.Create<CharacterDbContext>(topology.Connection);
        Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(other, new Source(_ => null)).CommitAsync(MintRequest(901, 2)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Instance_write_of_a_former_holder_is_refused_and_writes_nothing(DatabaseProvider provider)
    {
        Topology topology = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(topology.Connection))
        {
            await Assert.ThrowsAsync<RealmLeaseFenceLostException>(
                () => new EfInstanceStore(db, topology.OldHolder).SaveInstanceAsync(new InstanceRecord(5, 33, 1_900_000_000)));
        }

        await using (CharacterDbContext check = TestContexts.Create<CharacterDbContext>(topology.Connection))
            Assert.Empty(await check.Set<InstanceRow>().AsNoTracking().ToListAsync());

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(topology.Connection))
            await new EfInstanceStore(db, topology.NewHolder).SaveInstanceAsync(new InstanceRecord(5, 33, 1_900_000_000));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(topology.Connection))
        {
            await Assert.ThrowsAsync<RealmLeaseFenceLostException>(() => new EfInstanceStore(db, topology.OldHolder).DeleteInstanceAsync(5));
        }

        await using CharacterDbContext after = TestContexts.Create<CharacterDbContext>(topology.Connection);
        Assert.Equal(5, (await after.Set<InstanceRow>().AsNoTracking().SingleAsync()).Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Social_write_of_a_former_holder_is_refused_and_writes_nothing(DatabaseProvider provider)
    {
        Topology topology = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(topology.Connection))
        {
            await Assert.ThrowsAsync<RealmLeaseFenceLostException>(
                () => new EfSocialStore(db, topology.OldHolder).SetSocialAsync(1, 2, SocialFlags.Friend));
        }

        await using (CharacterDbContext check = TestContexts.Create<CharacterDbContext>(topology.Connection))
            Assert.Empty(await check.Set<CharacterSocialRow>().AsNoTracking().ToListAsync());

        await using CharacterDbContext current = TestContexts.Create<CharacterDbContext>(topology.Connection);
        await new EfSocialStore(current, topology.NewHolder).SetSocialAsync(1, 2, SocialFlags.Friend);
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(topology.Connection);
        Assert.Single(await verify.Set<CharacterSocialRow>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EnsureHeld_matches_token_owner_and_incarnation_only(DatabaseProvider provider)
    {
        Topology topology = await CreateAsync(provider);
        RealmLeaseFence current = topology.NewHolder.FenceFor(RealmAuthorityScopes.Economy)!.Value;
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(topology.Connection);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await LeaseFencedWrite.EnsureHeldAsync(db, current, CancellationToken.None);
            await tx.CommitAsync();
        }

        foreach (RealmLeaseFence wrong in new[]
        {
            current with { FencingToken = current.FencingToken - 1 },
            current with { OwnerNodeId = "node-x" },
            current with { OwnerIncarnation = Guid.NewGuid() },
            current with { Scope = RealmAuthorityScopes.Social, FencingToken = current.FencingToken + 1 },
        })
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<RealmLeaseFenceLostException>(() => LeaseFencedWrite.EnsureHeldAsync(db, wrong, CancellationToken.None));
        }
    }
}
