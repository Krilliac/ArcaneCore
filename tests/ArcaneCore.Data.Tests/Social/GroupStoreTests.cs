using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Accounts;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Social;

/// <summary>
/// Group persistence (<see cref="GroupDataModule"/>, characters schema 37) and the account last-address table
/// (<see cref="AccountAddressDataModule"/>, 38) on the SQLite / MariaDB / PostgreSQL matrix (only SQLite executes locally),
/// plus the reserved-gap placeholders that hold 35 and 36 for other wave-2 lanes.
/// </summary>
public sealed class GroupStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static readonly ulong[] NoIcons = new ulong[8];

    private static GroupRecord Group(uint id, int leader, params int[] members)
        => new(id, leader, 3, leader, 2, false, NoIcons, [.. members.Select(m => new GroupMemberRecord(m, 0, false))]);

    [Fact]
    public void TheModules_TakeTheLanesReservedVersions_AndThePlaceholdersFillTheGapBelow()
    {
        Assert.Equal(37, GroupDataModule.Version);
        Assert.Equal(38, AccountAddressDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.Characters), m => m is GroupDataModule { SchemaVersion: 37 });
        Assert.Contains(DataModules.For(DatabaseComponent.Characters), m => m is AccountAddressDataModule { SchemaVersion: 38 });
        Assert.Contains(CharacterDbContext.Schema.Steps, s => s.Version == 37 && s.Changes.OfType<CreateTableChange>().Count() == 2);
        Assert.Contains(CharacterDbContext.Schema.Steps, s => s.Version == 38);
        Assert.Contains(CharacterDataCleanups.All, c => c is GroupDataModule);
    }

    [Fact]
    public void APlaceholder_YieldsToARealModuleOfTheSameVersion_AndHoldsTheVersionOtherwise()
    {
        IDataModule[] real = [.. DataModules.For(DatabaseComponent.Characters).Where(m => m is not IReservedSchemaGap)];
        var owner = new FakeModule(35);

        // With the placeholders only, the gap below 37 is filled and the schema composes.
        SchemaDefinition alone = DataModules.Compose(DatabaseComponent.Characters, "characters", [], CharacterInlineSteps(), DataModules.For(DatabaseComponent.Characters));
        Assert.Equal(Enumerable.Range(2, alone.CurrentVersion - 1), alone.Steps.Select(s => s.Version));

        // Once a real module claims 35, its step is the one composed, not the placeholder's empty one.
        IDataModule[] merged = [.. DataModules.For(DatabaseComponent.Characters), owner];
        SchemaDefinition withOwner = DataModules.Compose(DatabaseComponent.Characters, "characters", [], CharacterInlineSteps(), merged);
        Assert.Same(owner.SchemaChanges, withOwner.Steps.Single(s => s.Version == 35).Changes);
        Assert.Empty(withOwner.Steps.Single(s => s.Version == 36).Changes);

        // Without placeholders the gap fails, as before.
        Assert.Throws<InvalidOperationException>(() => DataModules.Compose(DatabaseComponent.Characters, "characters", [], CharacterInlineSteps(), real));
    }

    [Fact]
    public async Task Sqlite_ADatabaseFromBeforeTheModules_GainsBothTables()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, GroupDataModule.Version - 1));
        await db.Database.ExecuteSqlRawAsync("DROP TABLE character_group_member");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE character_group");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE account_last_ip");

        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);

        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
        foreach (string table in new[] { "character_group", "character_group_member", "account_last_ip" })
        {
            Assert.Equal(1, await db.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {table}").SingleAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Groups_RoundTrip_WithMembersInSlotOrder_IconsAndRaidState(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        ulong[] icons = [0, 0x0000_0000_0000_0042, 0, 0, 0, 0, 0, 0xF130_0000_0000_1234];
        var raid = new GroupRecord(7, 30, 2, 31, 4, true, icons,
        [
            new GroupMemberRecord(30, 0, false),
            new GroupMemberRecord(32, 1, true),
            new GroupMemberRecord(31, 0, false),
        ]);
        await Write(connection, s => s.SaveGroupAsync(raid));
        await Write(connection, s => s.SaveGroupAsync(Group(3, 40, 40, 41)));

        IReadOnlyList<GroupRecord> loaded = await Read(connection, s => s.LoadGroupsAsync());
        Assert.Equal([3u, 7u], loaded.Select(g => g.Id));
        Assert.True(raid.SameAs(loaded[1]), "the raid did not round-trip");
        Assert.True(Group(3, 40, 40, 41).SameAs(loaded[0]));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SavingAgain_ReplacesTheGroup_AMemberMovedToAnotherGroupLeavesTheOld_AndDeleteRemovesBoth(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        await Write(connection, s => s.SaveGroupAsync(Group(1, 10, 10, 11, 12)));
        await Write(connection, s => s.SaveGroupAsync(Group(1, 11, 11, 12) with { LootMethod = 0 }));   // 10 left, 11 leads now
        await Write(connection, s => s.SaveGroupAsync(Group(2, 20, 20, 12)));                         // 12 joined another group

        IReadOnlyList<GroupRecord> loaded = await Read(connection, s => s.LoadGroupsAsync());
        Assert.Equal([11], loaded[0].Members.Select(m => m.CharacterId));
        Assert.Equal((11, (byte)0), (loaded[0].LeaderId, loaded[0].LootMethod));
        Assert.Equal([20, 12], loaded[1].Members.Select(m => m.CharacterId));

        await Write(connection, s => s.DeleteGroupAsync(1));
        await Write(connection, s => s.DeleteGroupAsync(1));   // idempotent
        await Write(connection, s => s.DeleteGroupAsync(404));
        Assert.Equal([2u], (await Read(connection, s => s.LoadGroupsAsync())).Select(g => g.Id));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(2, await verify.Set<GroupMemberRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterDeletion_RemovesItsMemberRow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        int[] ids;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            var characters = new EfCharacterStore(db);
            ids = [(await characters.CreateAsync(new CharacterRecord { AccountId = 5, Name = "Leaver", Race = 1, Class = 1, Level = 10 })).Id,
                   (await characters.CreateAsync(new CharacterRecord { AccountId = 6, Name = "Stayer", Race = 1, Class = 1, Level = 10 })).Id];
        }

        await Write(connection, s => s.SaveGroupAsync(Group(9, ids[1], ids[1], ids[0])));
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(ids[0], accountId: 5));
        }

        Assert.Equal([ids[1]], (await Read(connection, s => s.LoadGroupsAsync())).Single().Members.Select(m => m.CharacterId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AccountAddresses_AreReplacedPerAccount_AndFoundByALiteralPrefix(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        await Address(connection, s => s.RecordAsync(3, "10.0.0.7", 100));
        await Address(connection, s => s.RecordAsync(1, "10.0.0.5", 100));
        await Address(connection, s => s.RecordAsync(2, "10.0.1.9", 100));
        await Address(connection, s => s.RecordAsync(3, "192.168.0.3", 200));   // account 3 moved: its row is replaced
        await Address(connection, s => s.RecordAsync(4, "10_0_0_1", 100));      // a literal underscore is not a wildcard

        IReadOnlyList<AccountAddressRecord> found = await ReadAddress(connection, s => s.FindByPrefixAsync("10.0.0."));
        Assert.Equal([new AccountAddressRecord(1, "10.0.0.5", 100)], found);
        Assert.Equal([1, 2], (await ReadAddress(connection, s => s.FindByPrefixAsync("10.0."))).Select(r => r.AccountId));
        Assert.Equal([new AccountAddressRecord(3, "192.168.0.3", 200)], await ReadAddress(connection, s => s.FindByPrefixAsync("192.")));
        Assert.Empty(await ReadAddress(connection, s => s.FindByPrefixAsync("10_0.")));
    }

    private static IReadOnlyList<SchemaStep> CharacterInlineSteps()
        => [.. CharacterDbContext.Schema.Steps.Where(s => !DataModules.For(DatabaseComponent.Characters).Any(m => m.SchemaVersion == s.Version))];

    private async Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return connection;
    }

    private static async Task Write(DatabaseConnectionOptions connection, Func<IGroupStore, Task> write)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await write(new EfGroupStore(db));
    }

    private static async Task<T> Read<T>(DatabaseConnectionOptions connection, Func<IGroupStore, Task<T>> read)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await read(new EfGroupStore(db));
    }

    private static async Task Address(DatabaseConnectionOptions connection, Func<IAccountAddressStore, Task> write)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await write(new EfAccountAddressStore(db));
    }

    private static async Task<T> ReadAddress<T>(DatabaseConnectionOptions connection, Func<IAccountAddressStore, Task<T>> read)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await read(new EfAccountAddressStore(db));
    }

    private sealed class FakeModule(int version) : IDataModule
    {
        public DatabaseComponent Component => DatabaseComponent.Characters;

        public int SchemaVersion => version;

        public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("fake_owner_table")];

        public void ConfigureModel(ModelBuilder modelBuilder)
        {
        }

        public void AddServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
        }
    }
}
