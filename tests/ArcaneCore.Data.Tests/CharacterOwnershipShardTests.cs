using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Multi-scope character ownership (writer-lock design C1): worker shard rows beside the realm 'world' row.</summary>
public sealed class CharacterOwnershipShardTests : IAsyncLifetime
{
    private const string ShardA = "worker:a";
    private const string ShardB = "worker:b";

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void ShardModule_AddsColumnsAndSeedsRows_AfterTheOwnershipModule()
    {
        var module = new CharacterOwnershipShardDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.True(module.SchemaVersion > CharacterOwnershipDataModule.Version);
        Assert.Contains(CharacterDataCleanups.All, c => c.GetType() == module.GetType());
        Assert.Contains(module.SchemaChanges, c => c is AddColumnChange { Table: CharacterOwnershipDataModule.Table, Column: "owned_maps" });
        Assert.Contains(module.SchemaChanges, c => c is AddColumnChange { Table: CharacterOwnershipDataModule.Table, Column: "heartbeat_at_ms" });
        foreach (string scope in CharacterOwnershipShardDataModule.NewIdRangeScopes)
            Assert.Contains(module.SchemaChanges, c => c is EnsureRowChange { Table: "character_id_ranges", KeyColumn: "scope" } e && (string)e.KeyValue == scope);
        foreach (string scope in CharacterOwnershipShardDataModule.NewLeaseScopes)
            Assert.Contains(module.SchemaChanges, c => c is EnsureRowChange { Table: "realm_authority_lease", KeyColumn: "scope" } e && (string)e.KeyValue == scope);
    }

    [Theory]
    [InlineData("world", true)]
    [InlineData("worker:a", true)]
    [InlineData("worker:node-1.b_2:x", true)]
    [InlineData("worker:", false)]
    [InlineData("worker:has space", false)]
    [InlineData("worker:semi;colon", false)]
    [InlineData("realm", false)]
    [InlineData("", false)]
    public void Scopes_AcceptWorldAndWellFormedWorkerScopes(string scope, bool valid)
        => Assert.Equal(valid, CharacterOwnershipScopes.IsValid(scope));

    [Fact]
    public void Scopes_ShardIdLengthIsCappedAtTheColumnWidth()
    {
        Assert.True(CharacterOwnershipScopes.IsValid(CharacterOwnershipScopes.ForShard(new string('a', 57))));
        Assert.False(CharacterOwnershipScopes.IsValid(CharacterOwnershipScopes.WorkerPrefix + new string('a', 58)));
        Assert.Throws<ArgumentException>(() => CharacterOwnershipScopes.ForShard(new string('a', 58)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TwoWorkerScopes_CoexistWithDisjointMaps(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        CharacterOwnershipToken a = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [0u, 1u])))!;
        CharacterOwnershipToken b = (await Run(connection, s => s.TryAcquireAsync(ShardB, "node-b", Guid.NewGuid(), [30u, 36u])))!;
        Assert.Equal(ShardA, a.Scope);
        Assert.Equal(ShardB, b.Scope);
        Assert.Equal(1, a.Epoch);
        Assert.Equal(1, b.Epoch);
        Assert.True(await Run(connection, s => s.IsOwnedAsync(ShardA)));
        Assert.True(await Run(connection, s => s.IsOwnedAsync(ShardB)));
        Assert.False(await Run(connection, s => s.IsOwnedAsync("world")));
        Assert.True(await Run(connection, s => s.IsAnyOwnedAsync()));
        Assert.Equal(a, await Run(connection, s => s.GetCurrentAsync(ShardA)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorkerClaim_IsIdempotentForTheSameIncarnationAndRefusedForAnother(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        Guid incarnation = Guid.NewGuid();
        CharacterOwnershipToken first = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", incarnation, [1u])))!;
        CharacterOwnershipToken again = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", incarnation, [1u])))!;
        Assert.Equal(first, again);
        Assert.Null(await Run(connection, s => s.TryAcquireAsync(ShardA, "node-x", Guid.NewGuid(), [1u])));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OverlappingMaps_AreRefused_AndNullOwnsEveryMap(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        Assert.NotNull(await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u, 2u, 3u])));
        Assert.Null(await Run(connection, s => s.TryAcquireAsync(ShardB, "node-b", Guid.NewGuid(), [3u, 9u])));
        Assert.Null(await Run(connection, s => s.TryAcquireAsync(ShardB, "node-b", Guid.NewGuid(), ownedMaps: null)));
        Assert.False(await Run(connection, s => s.IsOwnedAsync(ShardB)));
        Assert.NotNull(await Run(connection, s => s.TryAcquireAsync(ShardB, "node-b", Guid.NewGuid(), [9u])));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AWorkerThatOwnsEveryMap_RefusesAnotherActiveWorker(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        Assert.NotNull(await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), ownedMaps: null)));
        Assert.Null(await Run(connection, s => s.TryAcquireAsync(ShardB, "node-b", Guid.NewGuid(), [5u])));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorldThenWorker_AreExcluded(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        CharacterOwnershipToken world = (await Run(connection, s => s.TryAcquireAsync("world", "combined", Guid.NewGuid())))!;
        Assert.Null(await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])));
        await Run(connection, s => s.ReleaseAsync(world));
        Assert.NotNull(await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorkerThenWorld_AreExcluded(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        CharacterOwnershipToken worker = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])))!;
        Assert.Null(await Run(connection, s => s.TryAcquireAsync("world", "combined", Guid.NewGuid())));
        Assert.False(await Run(connection, s => s.IsOwnedAsync("world")));
        await Run(connection, s => s.ReleaseAsync(worker));
        Assert.NotNull(await Run(connection, s => s.TryAcquireAsync("world", "combined", Guid.NewGuid())));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TokenlessWrite_IsRefusedWhileAWorkerIsActive_AndAllowedOnceItReleases(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        await using CharacterDbContext probe = TestContexts.Create<CharacterDbContext>(connection);
        await using (IDbContextTransaction idle = await probe.Database.BeginTransactionAsync())
            await CharacterOwnershipGuard.VerifyAsync(probe, null, default); // nothing active: a tokenless writer passes

        CharacterOwnershipToken worker = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])))!;
        await using (IDbContextTransaction refused = await probe.Database.BeginTransactionAsync())
            await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => CharacterOwnershipGuard.VerifyAsync(probe, null, default));

        await using (IDbContextTransaction current = await probe.Database.BeginTransactionAsync())
            await CharacterOwnershipGuard.VerifyAsync(probe, worker, default); // the worker's own token passes

        await Run(connection, s => s.ReleaseAsync(worker));
        await using (IDbContextTransaction after = await probe.Database.BeginTransactionAsync())
            await CharacterOwnershipGuard.VerifyAsync(probe, null, default);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TokenlessWrite_BlockedBehindAClaimsGate_IsRefusedWhenTheClaimCommits(DatabaseProvider provider)
    {
        // SQLite has one writer, so the interleaving cannot occur there; the sequential test above covers it.
        if (provider == DatabaseProvider.Sqlite)
            return;

        DatabaseConnectionOptions connection = await SetupAsync(provider);
        await using CharacterDbContext claim = TestContexts.Create<CharacterDbContext>(connection);
        await using CharacterDbContext writer = TestContexts.Create<CharacterDbContext>(connection);
        await using IDbContextTransaction claimTx = await claim.Database.BeginTransactionAsync();
        using (CharacterWriteFence.Exempt())
        {
            // The worker claim's gate: lock the inactive 'world' row, then insert and activate the shard row, all still uncommitted.
            int gated = await claim.Set<CharacterOwnershipRow>()
                .Where(r => r.Scope == "world" && !r.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1));
            Assert.Equal(1, gated);
            claim.Set<CharacterOwnershipRow>().Add(new CharacterOwnershipRow
            {
                Scope = ShardA,
                Epoch = 1,
                WriteSequence = 1,
                Active = true,
                OwnerNode = "node-a",
                OwnerIncarnation = Guid.NewGuid().ToString("D"),
                OwnedMaps = "1",
            });
            await claim.SaveChangesAsync();
        }

        // A tokenless writer starts now: it must wait on the gate's row lock rather than pass.
        await using IDbContextTransaction writerTx = await writer.Database.BeginTransactionAsync();
        Task verify = Task.Run(() => CharacterOwnershipGuard.VerifyAsync(writer, null, default));
        await Task.Delay(1500);
        Assert.False(verify.IsCompleted, "the tokenless writer passed while a worker claim held the gate");

        await claimTx.CommitAsync();
        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => verify.WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorkerToken_SavesAreFencedByItsOwnRow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        CharacterOwnershipToken a = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])))!;
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "ShardSave", Race = 1, Class = 1, Level = 1 });
        await new EfCharacterStore(db).SaveStateAsync(State(character.Id, 5, a));
        Assert.Equal(5u, (await new EfCharacterStore(db).GetByIdAsync(character.Id))!.MapId);

        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => new EfCharacterStore(db).SaveStateAsync(State(character.Id, 6, a with { Epoch = a.Epoch + 1 })));
        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => new EfCharacterStore(db).SaveStateAsync(State(character.Id, 6, a with { Scope = ShardB })));
        Assert.Equal(5u, (await new EfCharacterStore(db).GetByIdAsync(character.Id))!.MapId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Reclaim_CasesTheOldEpoch_AndFencesTheOldIncarnation(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        CharacterOwnershipToken old = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])))!;
        Guid next = Guid.NewGuid();
        CharacterOwnershipToken reclaimed = (await Run(connection, s => s.TryReclaimAsync(old, "node-a2", next, [1u, 2u])))!;
        Assert.Equal(old.Epoch + 1, reclaimed.Epoch);
        Assert.Equal(next, reclaimed.Incarnation);

        // The same observation cannot reclaim twice.
        Assert.Null(await Run(connection, s => s.TryReclaimAsync(old, "node-a3", Guid.NewGuid(), [1u])));

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Reclaimed", Race = 1, Class = 1, Level = 1 });
        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => new EfCharacterStore(db).SaveStateAsync(State(character.Id, 7, old)));
        await new EfCharacterStore(db).SaveStateAsync(State(character.Id, 8, reclaimed));
        Assert.Equal(8u, (await new EfCharacterStore(db).GetByIdAsync(character.Id))!.MapId);

        // The stale token cannot release the reclaimed shard.
        await Run(connection, s => s.ReleaseAsync(old));
        Assert.True(await Run(connection, s => s.IsOwnedAsync(ShardA)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Reclaim_IsRefusedForTheWorldScope_AndWhenMapsOverlapAnotherWorker(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        CharacterOwnershipToken a = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])))!;
        await Run(connection, s => s.TryAcquireAsync(ShardB, "node-b", Guid.NewGuid(), [2u]));
        Assert.Null(await Run(connection, s => s.TryReclaimAsync(a, "node-a2", Guid.NewGuid(), [2u])));
        Assert.Equal(a, await Run(connection, s => s.GetCurrentAsync(ShardA)));

        var world = new CharacterOwnershipToken("world", 1, Guid.NewGuid());
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await Assert.ThrowsAsync<ArgumentException>(() => new EfCharacterOwnershipStore(db).TryReclaimAsync(world, "n", Guid.NewGuid(), null));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Reap_RequiresAnOldHeartbeat_AndKillsTheOwnersToken(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        CharacterOwnershipToken a = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])))!;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long Age = 150_000;

        Assert.True(await Run(connection, s => s.HeartbeatAsync(a, now)));
        Assert.False(await Run(connection, s => s.ReapAsync(a, now + 1000, Age))); // alive a second ago
        Assert.False(await Run(connection, s => s.ReapAsync(a with { Epoch = a.Epoch + 1 }, now + Age, Age))); // wrong epoch

        Assert.True(await Run(connection, s => s.ReapAsync(a, now + Age, Age)));
        Assert.False(await Run(connection, s => s.IsOwnedAsync(ShardA)));
        Assert.False(await Run(connection, s => s.HeartbeatAsync(a, now + Age))); // the reaped owner lost its row
        Assert.False(await Run(connection, s => s.ReapAsync(a, now + Age, Age))); // already reaped

        CharacterOwnershipToken reacquired = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u])))!;
        Assert.Equal(a.Epoch + 2, reacquired.Epoch); // acquire +1, reap +1, acquire +1
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Takeover_MovesAWorkerShardAndKeepsItsMaps(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        CharacterOwnershipToken a = (await Run(connection, s => s.TryAcquireAsync(ShardA, "node-a", Guid.NewGuid(), [1u, 2u])))!;
        CharacterOwnershipToken moved = (await Run(connection, s => s.TryTakeoverAsync(a, "standby", Guid.NewGuid())))!;
        Assert.Equal(a.Epoch + 1, moved.Epoch);
        Assert.Equal(ShardA, moved.Scope);
        // Still owns maps 1 and 2 after the move.
        Assert.Null(await Run(connection, s => s.TryAcquireAsync(ShardB, "node-b", Guid.NewGuid(), [2u])));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentClaimsOfOverlappingShards_HaveOneWinner(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        await using CharacterDbContext left = TestContexts.Create<CharacterDbContext>(connection);
        await using CharacterDbContext right = TestContexts.Create<CharacterDbContext>(connection);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CharacterOwnershipToken?> a = Task.Run(async () =>
        {
            await start.Task;
            return await new EfCharacterOwnershipStore(left).TryAcquireAsync(ShardA, "left", Guid.NewGuid(), [4u]);
        });
        Task<CharacterOwnershipToken?> b = Task.Run(async () =>
        {
            await start.Task;
            return await new EfCharacterOwnershipStore(right).TryAcquireAsync(ShardB, "right", Guid.NewGuid(), [4u]);
        });
        start.SetResult();
        CharacterOwnershipToken?[] results = await Task.WhenAll(a, b);
        Assert.Equal(1, results.Count(r => r is not null));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InvalidScopesAndMapSets_AreRejected(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SetupAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        var store = new EfCharacterOwnershipStore(db);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAcquireAsync("worker:bad shard", "n", Guid.NewGuid(), [1u]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAcquireAsync("realm", "n", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAcquireAsync(ShardA, "n", Guid.NewGuid(), []));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAcquireAsync("world", "n", Guid.NewGuid(), [1u]));
    }

    private async Task<DatabaseConnectionOptions> SetupAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
        return connection;
    }

    /// <summary>Runs one store call on a fresh context, as a separate process would.</summary>
    private static async Task<T> Run<T>(DatabaseConnectionOptions connection, Func<EfCharacterOwnershipStore, Task<T>> call)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await call(new EfCharacterOwnershipStore(db));
    }

    private static async Task Run(DatabaseConnectionOptions connection, Func<EfCharacterOwnershipStore, Task> call)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await call(new EfCharacterOwnershipStore(db));
    }

    private static CharacterState State(int id, uint map, CharacterOwnershipToken? ownership)
        => new(id, map, 1, 1, 2, 3, 0, 1, 1, Ownership: ownership);
}
