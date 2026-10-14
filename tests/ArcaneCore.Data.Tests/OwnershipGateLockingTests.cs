using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The ownership fence must not serialize ordinary saves on MariaDB/PostgreSQL (a server that never enables ownership
/// must persist as before) while still making a claim or takeover wait for in-flight saves. SQLite has a single
/// writer, so the multi-writer cases only run on the server providers.
/// </summary>
public sealed class OwnershipGateLockingTests : IAsyncLifetime
{
    private static readonly TimeSpan Blocked = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly TestDatabases _databases = new();

    // Without a server configured the theory still needs one row, so SQLite stays in the list and the tests return at once.
    public static IEnumerable<object[]> ServerProviders() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(ServerProviders))]
    public async Task UnfencedSavesRunConcurrently_ButAClaimWaitsForThem(DatabaseProvider provider)
    {
        if (provider == DatabaseProvider.Sqlite)
            return; // single writer: nothing to overlap

        DatabaseConnectionOptions connection = await Prepare(provider);
        await using CharacterDbContext first = TestContexts.Create<CharacterDbContext>(connection);
        await using CharacterDbContext second = TestContexts.Create<CharacterDbContext>(connection);
        await using IDbContextTransaction firstTx = await first.Database.BeginTransactionAsync();
        await CharacterOwnershipGuard.VerifyAsync(first, null, default);

        // A second unfenced save while the first is still open: shared locks, so it must not wait.
        await Task.Run(async () =>
        {
            await using IDbContextTransaction secondTx = await second.Database.BeginTransactionAsync();
            await CharacterOwnershipGuard.VerifyAsync(second, null, default);
            await secondTx.CommitAsync();
        }).WaitAsync(Patience);

        Task<CharacterOwnershipToken?> claim = Task.Run(async () =>
        {
            await using CharacterDbContext claimer = TestContexts.Create<CharacterDbContext>(connection);
            return await new EfCharacterOwnershipStore(claimer).TryAcquireAsync("world", "node", Guid.NewGuid());
        });
        await Task.Delay(Blocked);
        Assert.False(claim.IsCompleted, "a claim must wait for the in-flight unfenced save");

        await firstTx.CommitAsync();
        Assert.NotNull(await claim.WaitAsync(Patience));
    }

    [Theory]
    [MemberData(nameof(ServerProviders))]
    public async Task FencedSavesRunConcurrently_ATakeoverWaitsForThem_AndAStaleTokenIsRejected(DatabaseProvider provider)
    {
        if (provider == DatabaseProvider.Sqlite)
            return; // single writer: nothing to overlap

        DatabaseConnectionOptions connection = await Prepare(provider);
        CharacterOwnershipToken owner;
        await using (CharacterDbContext claim = TestContexts.Create<CharacterDbContext>(connection))
            owner = Assert.IsType<CharacterOwnershipToken>(await new EfCharacterOwnershipStore(claim).TryAcquireAsync("world", "first", Guid.NewGuid()));

        await using CharacterDbContext first = TestContexts.Create<CharacterDbContext>(connection);
        await using CharacterDbContext second = TestContexts.Create<CharacterDbContext>(connection);
        await using IDbContextTransaction firstTx = await first.Database.BeginTransactionAsync();
        await CharacterOwnershipGuard.VerifyAsync(first, owner, default);

        await Task.Run(async () =>
        {
            await using IDbContextTransaction secondTx = await second.Database.BeginTransactionAsync();
            await CharacterOwnershipGuard.VerifyAsync(second, owner, default);
            await secondTx.CommitAsync();
        }).WaitAsync(Patience);

        Task<CharacterOwnershipToken?> takeover = Task.Run(async () =>
        {
            await using CharacterDbContext taker = TestContexts.Create<CharacterDbContext>(connection);
            return await new EfCharacterOwnershipStore(taker).TryTakeoverAsync(owner, "second", Guid.NewGuid());
        });
        await Task.Delay(Blocked);
        Assert.False(takeover.IsCompleted, "a takeover must wait for the in-flight fenced save");

        await firstTx.CommitAsync();
        Assert.NotNull(await takeover.WaitAsync(Patience));

        await using CharacterDbContext stale = TestContexts.Create<CharacterDbContext>(connection);
        await using IDbContextTransaction staleTx = await stale.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => CharacterOwnershipGuard.VerifyAsync(stale, owner, default));
        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => CharacterOwnershipGuard.VerifyAsync(stale, null, default));
    }

    private async Task<DatabaseConnectionOptions> Prepare(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
        return connection;
    }
}
