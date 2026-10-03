using System.Data.Common;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// An upgraded database must hold the same indexes as a freshly created one. The historic
/// bootstrapper kept only CreateTableOperations of the model differ, so every index of a table
/// created by an upgrade step was silently lost (handoff item 3). These tests compare the
/// database's catalog with the EF model and with a fresh database, and check the behaviour the
/// missing indexes were guarding (one guild per character, one auction per item).
/// </summary>
public sealed class SchemaIndexParityTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LegacyStepwiseUpgrade_IndexesMatchFreshAndModel(DatabaseProvider provider)
    {
        DatabaseConnectionOptions upgraded = await _databases.CreateAsync(provider);
        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);

        await using (AuthM5Context legacy = TestContexts.Create<AuthM5Context>(upgraded))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, AuthM5Context.Schema);
        }

        await using (CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(upgraded))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
        }

        await using (MapWorldV1Context legacy = TestContexts.Create<MapWorldV1Context>(upgraded))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, MapWorldV1Context.Schema);
        }

        await using AuthDbContext auth = TestContexts.Create<AuthDbContext>(upgraded);
        await using CharacterDbContext characters = TestContexts.Create<CharacterDbContext>(upgraded);
        await using WorldDbContext world = TestContexts.Create<WorldDbContext>(upgraded);
        foreach (SchemaStep step in AuthDbContext.Schema.Steps)
        {
            await SchemaBootstrapper.EnsureAsync(auth, SchemaProbe.ThroughVersion(AuthDbContext.Schema, step.Version));
        }

        foreach (SchemaStep step in CharacterDbContext.Schema.Steps)
        {
            await SchemaBootstrapper.EnsureAsync(characters, SchemaProbe.ThroughVersion(CharacterDbContext.Schema, step.Version));
        }

        foreach (SchemaStep step in WorldDbContext.Schema.Steps)
        {
            await SchemaBootstrapper.EnsureAsync(world, SchemaProbe.ThroughVersion(WorldDbContext.Schema, step.Version));
        }

        await SchemaProbe.AssertIndexParityAsync(auth, "auth upgraded");
        await SchemaProbe.AssertIndexParityAsync(characters, "characters upgraded");
        await SchemaProbe.AssertIndexParityAsync(world, "world upgraded");
        await SchemaProbe.AssertColumnParityAsync(characters, "characters upgraded");
        await SchemaProbe.AssertColumnParityAsync(world, "world upgraded");

        // The same databases, created fresh, are the second oracle.
        foreach (string component in new[] { "auth", "characters", "world" })
        {
            await SchemaProbe.EnsureCurrentAsync(component, fresh);
        }

        await using AuthDbContext freshAuth = TestContexts.Create<AuthDbContext>(fresh);
        await using CharacterDbContext freshCharacters = TestContexts.Create<CharacterDbContext>(fresh);
        await using WorldDbContext freshWorld = TestContexts.Create<WorldDbContext>(fresh);
        Assert.Equal(
            await SchemaProbe.ActualIndexesAsync(freshAuth, SchemaProbe.ModelTables(freshAuth)),
            await SchemaProbe.ActualIndexesAsync(auth, SchemaProbe.ModelTables(auth)));
        Assert.Equal(
            await SchemaProbe.ActualIndexesAsync(freshCharacters, SchemaProbe.ModelTables(freshCharacters)),
            await SchemaProbe.ActualIndexesAsync(characters, SchemaProbe.ModelTables(characters)));
        Assert.Equal(
            await SchemaProbe.ActualIndexesAsync(freshWorld, SchemaProbe.ModelTables(freshWorld)),
            await SchemaProbe.ActualIndexesAsync(world, SchemaProbe.ModelTables(world)));
    }

    [Theory]
    [MemberData(nameof(CandidateBaseline.Variants), MemberType = typeof(CandidateBaseline))]
    public async Task CandidateBaseline_UpgradesToCurrent_PreservesEveryRow_AndMatchesModel(CandidateBaseline.Variant variant)
    {
        (DatabaseConnectionOptions connection, string path) = await NewBaselineAsync(variant);
        IReadOnlyDictionary<string, string[]> tables = await CandidateBaseline.TablesAsync(path);
        Assert.True(tables.Count >= 40, $"the frozen baseline should hold dozens of tables, found {tables.Count}");

        var before = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using (var raw = new DbContext(TestContexts.Options<DbContext>(connection)))
        {
            foreach ((string table, string[] columns) in tables)
            {
                string[] rows = await SchemaProbe.SnapshotAsync(raw, table, columns);
                Assert.True(rows.Length > 0, $"baseline table {table} must be populated");
                before[table] = rows;
            }
        }

        foreach (string component in new[] { "auth", "characters", "world" })
        {
            await SchemaProbe.EnsureCurrentAsync(component, connection);
        }

        await using (var raw = new DbContext(TestContexts.Options<DbContext>(connection)))
        {
            foreach ((string table, string[] columns) in tables)
            {
                string[] after = await SchemaProbe.SnapshotAsync(raw, table, columns);
                Assert.True(Enumerable.SequenceEqual(before[table], after), $"rows of {table} changed by the upgrade");
            }
        }

        foreach (string component in new[] { "auth", "characters", "world" })
        {
            await using DbContext db = SchemaProbe.CreateContext(component, connection);
            Assert.Equal(SchemaProbe.SchemaOf(component).CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
            await SchemaProbe.AssertIndexParityAsync(db, $"{component} from {variant}");
            await SchemaProbe.AssertColumnParityAsync(db, $"{component} from {variant}");
        }
    }

    [Fact]
    public async Task UpgradedHistoricBaseline_EnforcesUniqueGuildMembership_AndAuctionItem()
    {
        (DatabaseConnectionOptions connection, _) = await NewBaselineAsync(CandidateBaseline.Variant.HistoricUpgraded);
        foreach (string component in new[] { "auth", "characters", "world" })
        {
            await SchemaProbe.EnsureCurrentAsync(component, connection);
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        string guildMember = TestContexts.Quote(db, "guild_member");
        await Assert.ThrowsAnyAsync<DbException>(() => SchemaProbe.ExecuteAsync(db,
            $"INSERT INTO {guildMember} SELECT 50, {Q(db, "CharacterId")}, {Q(db, "Rank")}, {Q(db, "PublicNote")}, {Q(db, "OfficerNote")}, " +
            $"{Q(db, "Level")}, {Q(db, "ZoneId")}, {Q(db, "LogoutTime")} FROM {guildMember} WHERE {Q(db, "GuildId")} = 1"));

        await SchemaProbe.ExecuteAsync(db, AuctionInsert(db, id: 1, itemGuid: 777));
        await Assert.ThrowsAnyAsync<DbException>(() => SchemaProbe.ExecuteAsync(db, AuctionInsert(db, id: 2, itemGuid: 777)));
    }

    [Fact]
    public async Task IndexRepair_WithDuplicateRows_FailsClosed_KeepsRows()
    {
        (DatabaseConnectionOptions connection, string path) = await NewBaselineAsync(CandidateBaseline.Variant.HistoricUpgraded);
        await using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await raw.OpenAsync();
            await using SqliteCommand command = raw.CreateCommand();
            command.CommandText =
                "INSERT INTO \"guild_member\" SELECT 50, \"CharacterId\", \"Rank\", \"PublicNote\", \"OfficerNote\", \"Level\", \"ZoneId\", \"LogoutTime\" " +
                "FROM \"guild_member\" WHERE \"GuildId\" = 1";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(
            () => SchemaProbe.EnsureCurrentAsync("characters", connection));
        Assert.Contains("guild_member", ex.Message, StringComparison.Ordinal);

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        // SQLite runs the whole bootstrap in one transaction, so the refused repair undoes the steps before it too;
        // an engine with non-transactional DDL would stay at CharacterDbContext.IndexRepairVersion - 1.
        Assert.Equal(CandidateBaseline.CharactersVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
        string[] rows = await SchemaProbe.SnapshotAsync(db, "guild_member", ["GuildId", "CharacterId"]);
        Assert.Equal(2, rows.Length);
    }

    [Fact]
    public void TableNames_AreDistinctAcrossComponents()
    {
        // Components may share one database; a table name claimed twice would be adopted or
        // clobbered silently once CreateTable becomes idempotent.
        string[] Tables(SchemaDefinition s) =>
        [
            s.VersionTable,
            .. s.Version1Tables,
            .. s.Steps.SelectMany(x => x.Changes).OfType<CreateTableChange>().Select(c => c.Table),
        ];

        string[] all = [.. Tables(AuthDbContext.Schema), .. Tables(CharacterDbContext.Schema), .. Tables(WorldDbContext.Schema)];
        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static string Q(DbContext db, string identifier) => TestContexts.Quote(db, identifier);

    private static string AuctionInsert(DbContext db, int id, int itemGuid) =>
        $"INSERT INTO {Q(db, "auction")} ({Q(db, "id")}, {Q(db, "house_id")}, {Q(db, "item_guid")}, {Q(db, "item_id")}, {Q(db, "item_count")}, " +
        $"{Q(db, "seller_guid")}, {Q(db, "start_bid")}, {Q(db, "buyout_price")}, {Q(db, "expire_time")}, {Q(db, "buyer_guid")}, " +
        $"{Q(db, "last_bid")}, {Q(db, "deposit")}) VALUES ({id}, 1, {itemGuid}, 1, 1, 1, 1, 1, 1, 0, 0, 0)";

    private async Task<(DatabaseConnectionOptions Connection, string Path)> NewBaselineAsync(CandidateBaseline.Variant variant)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        string path = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource;
        await CandidateBaseline.CreateAsync(path, variant);
        return (connection, path);
    }
}
