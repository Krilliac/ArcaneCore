using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The character-deletion ledger (<see cref="ICharacterDeletionStore"/>, table
/// <c>character_deletion</c>) on every engine: it commits or rolls back with the deletion, resolves
/// a commit whose acknowledgement threw, and fences explicit-id creation while a deletion is
/// pending (docs/integration/character-delete.md). Version assertions use the module constant and
/// <see cref="CharacterDbContext.Schema"/>, never a literal, because the lead renumbers it.
/// </summary>
public sealed class CharacterDeletionLedgerTests : IAsyncLifetime
{
    private const int Account = 1;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void Schema_IncludesTheDeletionLedgerStepAtItsModuleVersion()
    {
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= CharacterDeletionDataModule.Version);
        SchemaStep step = Assert.Single(CharacterDbContext.Schema.Steps, s => s.Version == CharacterDeletionDataModule.Version);
        Assert.Equal(new CreateTableChange("character_deletion"), Assert.Single(step.Changes));
        Assert.Contains(CharacterDataCleanups.All, c => c is CharacterDeletionDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteAsync_CommitThenThrow_IsCommittedTrue_AndPendingIsListed(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int id) = await SeedAsync(provider);
        Guid operation = Guid.NewGuid();
        var thrower = new ThrowAfterCommit();
        DbContextOptionsBuilder<CharacterDbContext> builder = new();
        DataServiceCollectionExtensions.ConfigureProvider(builder, cs);
        builder.AddInterceptors(thrower);
        await using (var faulty = new CharacterDbContext(builder.Options))
        {
            await Assert.ThrowsAsync<IOException>(() => new EfCharacterStore(faulty)
                .DeleteAsync(operation, id, Account, CharacterDataCleanups.All, CancellationToken.None));
        }

        Assert.True(thrower.Fired);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        var store = new EfCharacterStore(db);
        Assert.Null(await store.GetByIdAsync(id)); // the commit really happened
        Assert.True(await store.IsCommittedAsync(operation));
        PendingCharacterDeletion pending = Assert.Single(await store.GetPendingAsync(Account));
        Assert.Equal(new PendingCharacterDeletion(operation, id, Account, "Doomed"), pending);
        Assert.Empty(await store.GetPendingAsync(Account + 1)); // listed per account only
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteAsync_RefusedOrRolledBack_LeavesNoLedgerRow(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int id) = await SeedAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfSocialStore(db).SaveGuildAsync(Guild(id));
        }

        Guid refused = Guid.NewGuid();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.False(await new EfCharacterStore(db).DeleteAsync(refused, id, Account, CharacterDataCleanups.All, CancellationToken.None));
        }

        Guid failed = Guid.NewGuid();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new EfCharacterStore(db)
                .DeleteAsync(failed, id, Account, [new FailingCleanup()], CancellationToken.None));
        }

        Guid wrongAccount = Guid.NewGuid();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.False(await new EfCharacterStore(db).DeleteAsync(wrongAccount, id, Account + 1, CharacterDataCleanups.All, CancellationToken.None));
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        var reader = new EfCharacterStore(read);
        Assert.NotNull(await reader.GetByIdAsync(id));
        foreach (Guid operation in new[] { refused, failed, wrongAccount })
        {
            Assert.False(await reader.IsCommittedAsync(operation));
        }

        Assert.Empty(await reader.GetPendingAsync(Account));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteAsync_InACallerTransaction_TheLedgerFollowsTheCallersCommitAndRollback(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int id) = await SeedAsync(provider);
        Guid rolledBack = Guid.NewGuid();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.True(await new EfCharacterStore(db).DeleteAsync(rolledBack, id, Account, CharacterDataCleanups.All, CancellationToken.None));
            await transaction.RollbackAsync();
        }

        await using (CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs))
        {
            var reader = new EfCharacterStore(read);
            Assert.NotNull(await reader.GetByIdAsync(id));
            Assert.False(await reader.IsCommittedAsync(rolledBack));
        }

        Guid committed = Guid.NewGuid();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.True(await new EfCharacterStore(db).DeleteAsync(committed, id, Account, CharacterDataCleanups.All, CancellationToken.None));
            await transaction.CommitAsync();
        }

        await using CharacterDbContext after = TestContexts.Create<CharacterDbContext>(cs);
        var store = new EfCharacterStore(after);
        Assert.Null(await store.GetByIdAsync(id));
        Assert.True(await store.IsCommittedAsync(committed));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CreateAsync_ExplicitIdWithAPendingDeletion_Throws_AndInsertsNothing(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int id) = await SeedAsync(provider);
        Guid operation = Guid.NewGuid();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(operation, id, Account, CharacterDataCleanups.All, CancellationToken.None));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await Assert.ThrowsAsync<CharacterIdPendingDeletionException>(() => new EfCharacterStore(db)
                .CreateAsync(new CharacterRecord { Id = id, AccountId = Account, Name = "Reborn" }));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.Null(await new EfCharacterStore(db).GetByIdAsync(id));
            Assert.False(await new EfCharacterStore(db).IsNameTakenAsync("Reborn"));
            await new EfCharacterStore(db).CompleteAsync(operation); // the finalizers ran: the id is free again
        }

        await using CharacterDbContext after = TestContexts.Create<CharacterDbContext>(cs);
        CharacterRecord reborn = await new EfCharacterStore(after)
            .CreateAsync(new CharacterRecord { Id = id, AccountId = Account, Name = "Reborn" });
        Assert.Equal(id, reborn.Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CreateAsync_GeneratedIds_AreNeverFencedByAPendingDeletion(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int id) = await SeedAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(Guid.NewGuid(), id, Account, CharacterDataCleanups.All, CancellationToken.None));
        }

        await using CharacterDbContext after = TestContexts.Create<CharacterDbContext>(cs);
        CharacterRecord created = await new EfCharacterStore(after).CreateAsync(new CharacterRecord { AccountId = Account, Name = "Fresh" });
        Assert.NotEqual(0, created.Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task GeneratedIds_AreNotReusedAfterDeletingTheHighest(DatabaseProvider provider)
    {
        // Evidence for the create fence's scope: it covers explicit ids only because generated ids
        // are not reused. This settles it per engine instead of assuming it.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        await store.CreateAsync(new CharacterRecord { AccountId = Account, Name = "First" });
        int highest = (await store.CreateAsync(new CharacterRecord { AccountId = Account, Name = "Second" })).Id;
        Assert.True(await store.DeleteAsync(highest, Account));

        int next = (await store.CreateAsync(new CharacterRecord { AccountId = Account, Name = "Third" })).Id;

        Assert.NotEqual(highest, next);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CompleteAsync_IsIdempotent_AndForgetsTheOperation(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int id) = await SeedAsync(provider);
        Guid operation = Guid.NewGuid();
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        var store = new EfCharacterStore(db);
        Assert.True(await store.DeleteAsync(operation, id, Account, CharacterDataCleanups.All, CancellationToken.None));
        Assert.True(await store.IsCommittedAsync(operation));

        await store.CompleteAsync(operation);
        await store.CompleteAsync(operation);
        await store.CompleteAsync(Guid.NewGuid());

        Assert.False(await store.IsCommittedAsync(operation));
        Assert.Empty(await store.GetPendingAsync(Account));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Cleanup_RefusesALiveCharacterWhoseIdStillHasAPendingEarlierDeletion(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int id) = await SeedAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(Guid.NewGuid(), id, Account, CharacterDataCleanups.All, CancellationToken.None));
        }

        // The id is recreated outside the store's fence (a raw import).
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            db.Characters.Add(new CharacterRecord { Id = id, AccountId = Account, Name = "Imported" });
            await db.SaveChangesAsync();
        }

        await using CharacterDbContext attempt = TestContexts.Create<CharacterDbContext>(cs);
        Assert.False(await new EfCharacterStore(attempt).DeleteAsync(id, Account)); // refused: protects the earlier lifetime's finalization

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.NotNull(await new EfCharacterStore(read).GetByIdAsync(id));
    }

    [Fact]
    public async Task TheLedgerlessDelete_WritesNoLedgerRow()
    {
        (DatabaseConnectionOptions cs, int id) = await SeedAsync(DatabaseProvider.Sqlite);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        var store = new EfCharacterStore(db);
        Assert.True(await store.DeleteAsync(id, Account));
        Assert.Empty(await store.GetPendingAsync(Account));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<(DatabaseConnectionOptions, int)> SeedAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        int id = (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = Account, Name = "Doomed" })).Id;
        return (cs, id);
    }

    private static GuildData Guild(int leader) => new(1, "Arcane", leader, "motd", "info", 1_700_000_000, 1, 2, 3, 4, 5,
        [
            new GuildRankData(0, "Guild Master", 0x1FF),
            new GuildRankData(1, "Officer", 0x1FF),
            new GuildRankData(2, "Veteran", 0x43),
            new GuildRankData(3, "Member", 0x43),
            new GuildRankData(4, "Initiate", 0x43),
        ],
        [new GuildMemberData(leader, 0, string.Empty, string.Empty, 1, 12, 0)]);

    /// <summary>Throws once after the first transaction commit: the commit is real, its acknowledgement is lost.</summary>
    private sealed class ThrowAfterCommit : DbTransactionInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) != 0;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                throw new IOException("injected: commit acknowledgement lost");
            }

            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private sealed class FailingCleanup : ICharacterDataCleanup
    {
        public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("injected cleanup failure");
    }
}
