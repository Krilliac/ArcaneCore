using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Per-character claims (writer-lock design C5, _wf/writer-lock-DESIGN.md section 1b) on every configured provider. The SQLite branch of the claim check is an
/// idempotent row UPDATE; MariaDB and PostgreSQL take a shared row lock (<c>LOCK IN SHARE MODE</c>, <c>FOR SHARE OF o</c>), which only these cases exercise
/// where <c>ARCANECORE_TEST_MARIADB</c> / <c>ARCANECORE_TEST_POSTGRES</c> name a server. The World.Tests <c>CharacterClaimTests</c> cover the queue and Realm-scope cases on SQLite.
/// </summary>
public sealed class CharacterClaimProviderTests : IAsyncLifetime
{
    private static readonly WorkerNodeId NodeA = new("node-a");
    private static readonly WorkerNodeId NodeB = new("node-b");
    private readonly TestDatabases _databases = new();
    private readonly List<ServiceProvider> _providers = [];

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
            await provider.DisposeAsync();
        await _databases.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ALiveClaim_IsRefused_AndADeadIncarnationsClaim_IsTakenOver(DatabaseProvider provider)
    {
        (ServiceProvider services, EfTransferDecisionStore store) = await OpenAsync(await _databases.CreateAsync(provider));
        CharacterOwnershipToken a = await AcquireAsync(services, "a", 1);
        CharacterOwnershipToken b = await AcquireAsync(services, "b", 30);

        Assert.Equal(CharacterClaimOutcome.Claimed, (await store.TryClaimCharacterAsync(41, a)).Outcome);
        CharacterClaimResult refused = await store.TryClaimCharacterAsync(41, b);
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, refused.Outcome);
        Assert.Equal(a.Scope, refused.PreviousScope);
        await WriteAsync(services, 41, a);
        await Assert.ThrowsAsync<CharacterClaimRejectedException>(() => WriteAsync(services, 41, b));

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().TryReclaimAsync(a, "node-restarted", Guid.NewGuid(), [1u]));
        }

        Assert.Equal(CharacterClaimOutcome.TookOverDeadOwner, (await store.TryClaimCharacterAsync(41, b)).Outcome);
        await WriteAsync(services, 41, b);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ARetiredSourcesRetainedSnapshot_IsDurablyRefusedAfterCommit(DatabaseProvider provider)
    {
        (ServiceProvider services, EfTransferDecisionStore store) = await OpenAsync(await _databases.CreateAsync(provider));
        CharacterOwnershipToken a = await AcquireAsync(services, "a", 1);
        CharacterOwnershipToken b = await AcquireAsync(services, "b", 30);
        await store.TryClaimCharacterAsync(42, a);
        await WriteAsync(services, 42, a);

        Guid transfer = Guid.NewGuid();
        await store.CreateReservationAsync(transfer, 42, 1, 1, NodeA, NodeB, 1, 0, 30, 0, a);
        await store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.SuspendAndCapture, Bundle(transfer, 42, a));
        await store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.InstallPassive);
        await store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.StageWorldPort);
        await WriteAsync(services, 42, a); // the settlement save during the reservation still passes

        TransferDecisionRecord committed = await store.CommitDecisionAsync(transfer, b.Incarnation, CancellationToken.None, b.Scope);
        Assert.Equal(b.Scope, committed.TargetOwnershipToken!.Scope);

        await Assert.ThrowsAsync<CharacterClaimRejectedException>(() => WriteAsync(services, 42, a));
        await WriteAsync(services, 42, b);
        Assert.Equal(b.Scope, (await store.MarkCompletedAsync(transfer)).TargetOwnershipToken!.Scope);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ADeadShard_IsReapedAtLogin_OnlyWithAStaleHeartbeatAndAFreeLock(DatabaseProvider provider)
    {
        (ServiceProvider services, EfTransferDecisionStore store) = await OpenAsync(await _databases.CreateAsync(provider));
        CharacterOwnershipToken a = await AcquireAsync(services, "a", 1);
        CharacterOwnershipToken b = await AcquireAsync(services, "b", 30);
        await store.TryClaimCharacterAsync(43, a);
        var policy = new CharacterClaimReapPolicy(TimeSpan.FromSeconds(150), (_, _) => Task.FromResult(true));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await HeartbeatAsync(services, a, now);
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, (await store.TryClaimCharacterAsync(43, b, policy)).Outcome);
        await HeartbeatAsync(services, a, now - (long)TimeSpan.FromMinutes(10).TotalMilliseconds);
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, (await store.TryClaimCharacterAsync(43, b)).Outcome);
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner,
            (await store.TryClaimCharacterAsync(43, b, new CharacterClaimReapPolicy(TimeSpan.FromSeconds(150), (_, _) => Task.FromResult(false)))).Outcome);
        Assert.Equal(CharacterClaimOutcome.TookOverDeadOwner, (await store.TryClaimCharacterAsync(43, b, policy)).Outcome);
        await WriteAsync(services, 43, b);
    }

    /// <summary>Begin and commit a characters transaction as the worker holding <paramref name="as"/> under the claim scope the write queues set; the fence verifies at begin.</summary>
    private static async Task WriteAsync(ServiceProvider services, int characterId, CharacterOwnershipToken @as)
    {
        services.GetRequiredService<CharacterOwnershipContext>().Current = @as;
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        using (CharacterWriteClaim.Use(characterId))
        {
            await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
            await transaction.CommitAsync();
        }
    }

    private static async Task HeartbeatAsync(ServiceProvider services, CharacterOwnershipToken token, long nowUnixMs)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().HeartbeatAsync(token, nowUnixMs));
    }

    private static async Task<CharacterOwnershipToken> AcquireAsync(ServiceProvider services, string shard, uint map)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
            .TryAcquireAsync(CharacterOwnershipScopes.ForShard(shard), "node-" + shard, Guid.NewGuid(), [map]))!;
    }

    private async Task<(ServiceProvider Services, EfTransferDecisionStore Store)> OpenAsync(DatabaseConnectionOptions connection)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
            ["Cluster:Fencing:Enabled"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharacterDatabase(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        _providers.Add(provider);
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        return (provider, provider.GetRequiredService<EfTransferDecisionStore>());
    }

    private static PlayerTransferBundle Bundle(Guid transferId, ulong guid, CharacterOwnershipToken source) => new()
    {
        TransferId = transferId,
        CharacterGuid = guid,
        CharacterId = (int)guid,
        AccountId = 1,
        SessionGeneration = 1,
        SourceWorkerId = NodeA,
        TargetWorkerId = NodeB,
        SourceOwnershipToken = source,
        CapturedAtUtcMs = 1_700_000_000_000,
        SourceMapId = 0,
        SourcePosition = new TransferPosition(-8949.95f, -132.49f, 83.53f, 0.5f),
        TargetMapId = 0,
        TargetPosition = new TransferPosition(-8949.95f, -132.49f, 83.53f, 0.5f),
        Health = 55,
        MaxHealth = 60,
        Powers = [10, 0, 100, 0, 0],
        Level = 3,
        Money = 1234,
        Items = [new TransferItemSnapshot(500, 25, 0, 15, 1, 20, 20, 0, 0, 0, 0, [0, 0, 0, 0, 0], [new TransferItemEnchantment(0, 37, 0, 0)])],
        Quests = [new TransferQuestSlotSnapshot(0, 783, 1, false, false, 0, [1, 0, 0, 0])],
        KnownSpells = [78, 81, 2457],
        HomeBind = new HomeBind(0, 12, -8949.95f, -132.49f, 83.53f),
        Corpse = new CorpseSnapshot(0, 1f, 2f, 3f, 0f, 1_700_000_000, 1),
        TaxiNodesMask = [2u],
    };
}
