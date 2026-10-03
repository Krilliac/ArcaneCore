using System.Data.Common;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The integrated fleet's schema allocation, fresh shared-database bootstrap, and every
/// additive feature upgrade. The existing engine matrix runs SQLite locally and MariaDB
/// and PostgreSQL when their test connection strings are available.
/// </summary>
public sealed class IntegratedSchemaTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void FeatureModules_HaveAssignedVersions_AndDistinctTables()
    {
        (Type Type, DatabaseComponent Component, int Version)[] expected =
        [
            (typeof(CreatureDataModule), DatabaseComponent.World, 2),
            (typeof(MapDataModule), DatabaseComponent.World, 3),
            (typeof(ItemWorldDataModule), DatabaseComponent.World, 4),
            (typeof(SpellWorldDataModule), DatabaseComponent.World, 5),
            (typeof(QuestNpcWorldModule), DatabaseComponent.World, 6),
            (typeof(GameObjectLootDataModule), DatabaseComponent.World, 7),
            (typeof(ItemCharacterDataModule), DatabaseComponent.Characters, 3),
            (typeof(CharacterSpellDataModule), DatabaseComponent.Characters, 4),
            (typeof(QuestNpcCharactersModule), DatabaseComponent.Characters, 5),
            (typeof(SocialDataModule), DatabaseComponent.Characters, 6),
        ];

        Assert.Equal(expected.OrderBy(m => m.Component).ThenBy(m => m.Version),
            DataModules.All.OrderBy(m => m.Component).ThenBy(m => m.SchemaVersion)
                .Select(m => (m.GetType(), m.Component, m.SchemaVersion)));
        Assert.Equal(2, AuthDbContext.Schema.CurrentVersion);
        Assert.Equal(6, CharacterDbContext.Schema.CurrentVersion);
        Assert.Equal(6, WorldDbContext.Schema.CurrentVersion);
        Assert.Equal([2, 3, 4, 5, 6], CharacterDbContext.Schema.Steps.Select(s => s.Version));
        Assert.Equal([2, 3, 4, 5, 6], WorldDbContext.Schema.Steps.Select(s => s.Version));

        foreach (DatabaseComponent component in new[] { DatabaseComponent.Characters, DatabaseComponent.World })
        {
            string[] tables = [.. DataModules.For(component).SelectMany(m => m.SchemaChanges)
                .OfType<CreateTableChange>().Select(c => c.Table)];
            Assert.Equal(tables.Length, tables.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshSharedDatabase_CreatesEveryFeatureTable_AndSecondBootstrapIsIdempotent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        for (int pass = 0; pass < 2; pass++)
        {
            await using AuthDbContext auth = TestContexts.Create<AuthDbContext>(connection);
            await using CharacterDbContext characters = TestContexts.Create<CharacterDbContext>(connection);
            await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
            await EnsureAndInspectAsync(auth, AuthDbContext.Schema);
            await EnsureAndInspectAsync(characters, CharacterDbContext.Schema);
            await EnsureAndInspectAsync(world, WorldDbContext.Schema);
            Assert.Empty(await characters.Characters.ToListAsync());
            Assert.Empty(await world.ClassInfo.ToListAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LegacySchemas_UpgradeEachFeatureStep_KeepRows_AndAllowRepeatedStartup(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
            legacy.Characters.Add(new CharacterV1Row { AccountId = 7, Name = "Existing", PlayedTime = 123 });
            await legacy.SaveChangesAsync();
        }

        await using (MapWorldV1Context legacy = TestContexts.Create<MapWorldV1Context>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, MapWorldV1Context.Schema);
            legacy.Set<ClassInfoRow>().Add(new ClassInfoRow { Class = 1, BaseHealth = 60, PowerType = 1 });
            await legacy.SaveChangesAsync();
        }

        await using CharacterDbContext characters = TestContexts.Create<CharacterDbContext>(connection);
        await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
        foreach (SchemaStep step in CharacterDbContext.Schema.Steps)
        {
            // The database starts with the historical v1 model. Only this step's DDL runs;
            // using the current context merely supplies the exact provider model for it.
            SchemaDefinition prefix = ThroughVersion(CharacterDbContext.Schema, step.Version);
            await EnsureAndInspectAsync(characters, prefix);
            await EnsureAndInspectAsync(characters, prefix);
            Assert.Equal("Existing", (await characters.Characters.SingleAsync()).Name);
            Assert.Equal(123u, (await characters.Characters.SingleAsync()).PlayedTime);
        }

        foreach (SchemaStep step in WorldDbContext.Schema.Steps)
        {
            SchemaDefinition prefix = ThroughVersion(WorldDbContext.Schema, step.Version);
            await EnsureAndInspectAsync(world, prefix);
            await EnsureAndInspectAsync(world, prefix);
            Assert.Equal(60u, (await world.ClassInfo.SingleAsync()).BaseHealth);
        }

        await EnsureAndInspectAsync(characters, CharacterDbContext.Schema);
        await EnsureAndInspectAsync(world, WorldDbContext.Schema);
        Assert.Equal(0u, (await characters.Characters.SingleAsync()).Money);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static SchemaDefinition ThroughVersion(SchemaDefinition schema, int version) => new()
    {
        Component = schema.Component,
        CurrentVersion = version,
        Version1Tables = schema.Version1Tables,
        Steps = [.. schema.Steps.Where(s => s.Version <= version)],
    };

    private static async Task EnsureAndInspectAsync(DbContext db, SchemaDefinition schema)
    {
        await SchemaBootstrapper.EnsureAsync(db, schema);
        Assert.Equal(schema.CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);

        string[] expectedTables = [schema.VersionTable, .. schema.Version1Tables,
            .. schema.Steps.SelectMany(s => s.Changes).OfType<CreateTableChange>().Select(c => c.Table)];
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        await db.Database.OpenConnectionAsync();
        try
        {
            foreach (string name in expectedTables.Distinct(StringComparer.Ordinal))
            {
                ITable table = Assert.Single(model.Tables, t => t.Name == name);
                string columns = string.Join(", ", table.Columns.Select(c => sql.DelimitIdentifier(c.Name)));
                await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = $"SELECT {columns} FROM {sql.DelimitIdentifier(table.Name, table.Schema)} WHERE 1 = 0";
                await using DbDataReader reader = await command.ExecuteReaderAsync();
                Assert.Equal(table.Columns.Count(), reader.FieldCount);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
