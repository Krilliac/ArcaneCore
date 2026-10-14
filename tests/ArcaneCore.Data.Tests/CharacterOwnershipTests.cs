using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class CharacterOwnershipTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void OwnershipModule_IsACharactersModuleWithCleanup()
    {
        var module = new ArcaneCore.Data.Characters.Ownership.CharacterOwnershipDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(ArcaneCore.Data.Characters.Ownership.CharacterOwnershipDataModule.Version, module.SchemaVersion);
        Assert.Contains(CharacterDataCleanups.All, c => c.GetType() == module.GetType());
        Assert.Contains(module.SchemaChanges, c => c is EnsureRowChange { Table: ArcaneCore.Data.Characters.Ownership.CharacterOwnershipDataModule.Table, KeyColumn: "scope" });
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= module.SchemaVersion);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AcquireReleaseAndTakeover_UsesMonotonicEpoch(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);

        Guid firstIncarnation = Guid.NewGuid();
        CharacterOwnershipToken first;
        await using (CharacterDbContext firstDb = TestContexts.Create<CharacterDbContext>(connection))
        {
            first = (await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(firstDb)
                .TryAcquireAsync("world", "first", firstIncarnation))!;
            Assert.Equal(1, first.Epoch);
        }

        await using (CharacterDbContext deniedDb = TestContexts.Create<CharacterDbContext>(connection))
            Assert.Null(await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(deniedDb)
                .TryAcquireAsync("world", "second", Guid.NewGuid()));

        Guid secondIncarnation = Guid.NewGuid();
        CharacterOwnershipToken second;
        await using (CharacterDbContext takeoverDb = TestContexts.Create<CharacterDbContext>(connection))
        {
            second = (await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(takeoverDb)
                .TryTakeoverAsync(first, "second", secondIncarnation))!;
            Assert.Equal(2, second.Epoch);
        }

        await using (CharacterDbContext releaseDb = TestContexts.Create<CharacterDbContext>(connection))
            await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(releaseDb).ReleaseAsync(second);

        await using CharacterDbContext reacquireDb = TestContexts.Create<CharacterDbContext>(connection);
        CharacterOwnershipToken third = (await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(reacquireDb)
            .TryAcquireAsync("world", "third", Guid.NewGuid()))!;
        Assert.Equal(3, third.Epoch);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ActiveFence_RejectsMissingAndStaleSnapshots(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            CharacterRecord character = await new EfCharacterStore(setup).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Fence", Race = 1, Class = 1, Level = 1 });
            await using var claimDb = TestContexts.Create<CharacterDbContext>(connection);
            CharacterOwnershipToken token = (await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(claimDb)
                .TryAcquireAsync("world", "owner", Guid.NewGuid()))!;

            // A world with fencing off writes untokened and is not checked per save (it watches the row instead, see WorldHost), so the
            // fence-off context accepts an untokened save; the stale-token save is the one a tokened (fenced) world is refused.

            CharacterState stale = State(character.Id, 20, token with { Epoch = token.Epoch - 1 });
            await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => new EfCharacterStore(setup).SaveStateAsync(stale));
            Assert.Equal(0u, (await new EfCharacterStore(setup).GetByIdAsync(character.Id))!.MapId);
            await new EfCharacterStore(setup).SaveStateAsync(State(character.Id, 10, null));
            Assert.Equal(10u, (await new EfCharacterStore(setup).GetByIdAsync(character.Id))!.MapId);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CurrentTokenSaves_AndStaleReleaseCannotClearTakeover(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            CharacterRecord character = await new EfCharacterStore(setup).CreateAsync(new CharacterRecord { AccountId = 1, Name = "CurrentOwner", Race = 1, Class = 1, Level = 1 });
            await using var claimDb = TestContexts.Create<CharacterDbContext>(connection);
            CharacterOwnershipToken first = (await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(claimDb)
                .TryAcquireAsync("world", "first", Guid.NewGuid()))!;
            await new EfCharacterStore(setup).SaveStateAsync(State(character.Id, 7, first));
            Assert.Equal(7u, (await new EfCharacterStore(setup).GetByIdAsync(character.Id))!.MapId);

            await using var takeoverDb = TestContexts.Create<CharacterDbContext>(connection);
            CharacterOwnershipToken second = (await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(takeoverDb)
                .TryTakeoverAsync(first, "second", Guid.NewGuid()))!;
            await using var staleReleaseDb = TestContexts.Create<CharacterDbContext>(connection);
            await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(staleReleaseDb).ReleaseAsync(first);
            Assert.True(await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(staleReleaseDb).IsOwnedAsync("world"));
            await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(staleReleaseDb).ReleaseAsync(second);
            Assert.False(await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(staleReleaseDb).IsOwnedAsync("world"));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentAcquisitionHasOneWinner(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
        await using CharacterDbContext left = TestContexts.Create<CharacterDbContext>(connection);
        await using CharacterDbContext right = TestContexts.Create<CharacterDbContext>(connection);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CharacterOwnershipToken?> a = Task.Run(async () =>
        {
            await start.Task;
            return await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(left).TryAcquireAsync("world", "left", Guid.NewGuid());
        });
        Task<CharacterOwnershipToken?> b = Task.Run(async () =>
        {
            await start.Task;
            return await new ArcaneCore.Data.Characters.Ownership.EfCharacterOwnershipStore(right).TryAcquireAsync("world", "right", Guid.NewGuid());
        });
        start.SetResult();
        CharacterOwnershipToken?[] results = await Task.WhenAll(a, b);
        Assert.Equal(1, results.Count(r => r is not null));
    }

    private static CharacterState State(int id, uint map, CharacterOwnershipToken? ownership)
        => new(id, map, 1, 1, 2, 3, 0, 1, 1, Ownership: ownership);
}
