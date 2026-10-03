using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Chr;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.PlayerCreate;

/// <summary>
/// <c>playercreateinfo_action</c>: the world table, its importer and the EF source of the starting
/// action bar (vmangos ObjectMgr::LoadPlayerInfo, ObjectMgr.cpp:4737-4796). The dumps are
/// hand-written; the real classic-db table has 215 rows over the 40 race/class pairs.
/// </summary>
public sealed class PlayerCreateActionImporterTests : IAsyncLifetime
{
    // Human warrior: attack (6603) on 72, Heroic Strike rank 1 (78) on 73, an item (117) on 83.
    // Skipped by vmangos' load checks: button 120 (MAX_ACTION_BUTTONS), an action above 0xFFFFFF,
    // race 9 (not playable) and class 6 (no such class).
    private const string Dump = """
        CREATE TABLE `playercreateinfo_action` (`race` tinyint unsigned NOT NULL, `class` tinyint unsigned NOT NULL, `button` smallint unsigned NOT NULL, `action` smallint unsigned NOT NULL, `type` smallint unsigned NOT NULL, PRIMARY KEY (`race`,`class`,`button`));
        INSERT INTO `playercreateinfo_action` VALUES (1,1,72,6603,0),(1,1,73,78,0),(1,1,83,117,128),(1,1,120,5,0),(1,1,10,16777216,0),(9,1,0,1,0),(1,6,0,1,0),(2,1,72,6603,0);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Module_IsDiscovered_AtItsConstantVersion_AndRegistersTheSource()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.World), m => m is StartActionWorldModule);

        Assert.Equal(StartActionWorldModule.Version, module.SchemaVersion);
        Assert.Equal(["playercreateinfo_action"], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= StartActionWorldModule.Version);
        var services = new ServiceCollection();
        DataModules.AddServices(services, DatabaseComponent.World);
        Assert.Contains(services, d => d.ServiceType == typeof(IStartActionSource) && d.ImplementationType == typeof(EfStartActionSource));
    }

    [Fact]
    public void Rows_MapByName_AndTheLoadChecksSkipTheInvalidOnes()
    {
        PlayerCreateActionDumpImporter importer = Import(Dump);

        PlayerCreateActionRow[] rows = [.. importer.Snapshot().OrderBy(r => r.Race).ThenBy(r => r.Button)];
        Assert.Equal(
            [(1, 1, 72, 6603u, 0), (1, 1, 73, 78u, 0), (1, 1, 83, 117u, 128), (2, 1, 72, 6603u, 0)],
            rows.Select(r => ((int)r.Race, (int)r.Class, (int)r.Button, r.Action, (int)r.Type)));
        PlayerCreateActionImportReport report = importer.BuildReport();
        Assert.Equal(4, report.Rows);
        Assert.Equal(4, report.SkippedRows);
    }

    [Fact]
    public void ARenamedKeyColumn_IsASchemaError()
    {
        var importer = new PlayerCreateActionDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `playercreateinfo_action` (`race`,`class`,`slot`,`action`,`type`) VALUES (1,1,72,6603,0);")));

        Assert.Equal("playercreateinfo_action", ex.Table);
        Assert.Equal("button", ex.Column);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_ReplacesTheTable_AndTheSourceReturnsTheBarOrderedByButton(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new PlayerCreateActionRow { Race = 4, Class = 4, Button = 0, Action = 1, Type = 0 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            PlayerCreateActionImportReport report = await Import(Dump).WriteAsync(db, replace: true);

            Assert.Equal(4, report.Rows);
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        var source = new EfStartActionSource(verify);
        Assert.Equal(
            [new ActionButton(72, 6603, 0), new ActionButton(73, 78, 0), new ActionButton(83, 117, 128)],
            await source.GetAsync(1, 1));
        Assert.Equal([new ActionButton(72, 6603, 0)], await source.GetAsync(2, 1));
        Assert.Empty(await source.GetAsync(4, 4)); // replaced away
        Assert.Empty(await source.GetAsync(3, 1)); // a pair without rows
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAKeyConflict_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Import(Dump).WriteAsync(db, replace: false);
            await Assert.ThrowsAsync<DbUpdateException>(() => Import(Dump + "\nINSERT INTO `playercreateinfo_action` VALUES (5,5,0,585,0);").WriteAsync(db, replace: false));
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(4, await verify.Set<PlayerCreateActionRow>().CountAsync());
    }

    private static PlayerCreateActionDumpImporter Import(string dump)
    {
        var importer = new PlayerCreateActionDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }
}
