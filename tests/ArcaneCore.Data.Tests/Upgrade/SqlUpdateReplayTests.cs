using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// Applies the committed update files, in order, to an empty database of each available provider (SQLite always; MariaDB and
/// PostgreSQL where ARCANECORE_TEST_MARIADB / ARCANECORE_TEST_POSTGRES are set) by executing the SQL text, and requires the
/// result to be what a bootstrap makes.
/// </summary>
public sealed class SqlUpdateReplayTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> ProviderComponents()
        => TestDatabases.AvailableProviders().SelectMany(p => SchemaProbe.Components().Select(c => new[] { p[0], c[0] }));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static async Task CreateEmptyAsync(DbContext db)
        => await ((IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>()).CreateAsync();

    [Theory]
    [MemberData(nameof(ProviderComponents))]
    public async Task BaselineAndSteps_BuildWhatABootstrapBuilds(DatabaseProvider provider, string component)
    {
        DatabaseConnectionOptions replayed = await _databases.CreateAsync(provider);
        await using DbContext db = SchemaProbe.CreateContext(component, replayed);
        SchemaDefinition schema = SchemaProbe.SchemaOf(component);
        await CreateEmptyAsync(db);

        IReadOnlyList<string> files = SqlUpdateSupport.OrderedFiles(component, provider);
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            await SqlUpdateSupport.ExecuteAsync(db, provider, File.ReadAllText(file));
        }

        Assert.Equal(schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(db, component));

        DriftReport drift = await SchemaDriftChecker.CheckAsync(db, schema);
        Assert.True(drift.IsClean, string.Join("; ", drift.Errors));
        Assert.True(drift.TablesExamined > 0 && drift.ColumnsExamined > 0 && drift.IndexesExamined > 0);

        string before = await SqlUpdateSupport.CatalogDumpAsync(db, schema);
        await SchemaBootstrapper.EnsureAsync(db, schema, new SchemaUpgradeOptions { Policy = SchemaPolicy.Never });
        Assert.Equal(before, await SqlUpdateSupport.CatalogDumpAsync(db, schema));

        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync(component, fresh);
        await using DbContext freshDb = SchemaProbe.CreateContext(component, fresh);
        string expected = await SqlUpdateSupport.CatalogDumpAsync(freshDb, schema);

        Assert.Contains("  column ", before);
        Assert.Contains("  index ", before);
        Assert.Equal(expected, before);

        string expectedSeeds = await SqlUpdateSupport.SeedRowsAsync(freshDb, schema);
        if (component == "characters")
        {
            Assert.Contains("seed table character_ownership", expectedSeeds);
        }

        Assert.Equal(expectedSeeds, await SqlUpdateSupport.SeedRowsAsync(db, schema));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AStepFile_AppliesAtItsPriorVersion_AndRefusesEveryOther_ChangingNothing(DatabaseProvider provider)
    {
        (UpdateComponent component, SchemaSnapshot snapshot) = SqlUpdateGoldenTests.StandInStep();
        UpdateSet set = await UpdateScriptWriter.GenerateAsync(component, snapshot);
        string step = set.Files.Single(f => f.Path.Contains("/" + SqlUpdateSupport.Folder(provider) + "/")).Content;
        SchemaDefinition schema = component.Definition;

        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using DbContext db = SchemaProbe.CreateContext("auth", connection);
        await CreateEmptyAsync(db);

        // An empty database has no version table: refused, nothing created.
        string empty = await SqlUpdateSupport.CatalogDumpAsync(db, schema, tolerateMissingVersionTable: true);
        await Assert.ThrowsAnyAsync<Exception>(() => SqlUpdateSupport.ExecuteAsync(db, provider, step));
        Assert.Equal(empty, await SqlUpdateSupport.CatalogDumpAsync(db, schema, tolerateMissingVersionTable: true));

        foreach (string file in SqlUpdateSupport.OrderedFiles("auth", provider))
        {
            await SqlUpdateSupport.ExecuteAsync(db, provider, File.ReadAllText(file));
        }

        // The account index is missing, as in a database whose index was lost; the step puts it back.
        await SchemaProbe.DropIndexesAsync(db, "account");

        // Recorded at the wrong version (4, not 5): refused, nothing changes.
        await UpgradeTestSupport.SetVersionAsync(db, "auth", 4);
        string wrong = await SqlUpdateSupport.CatalogDumpAsync(db, schema);
        await Assert.ThrowsAnyAsync<Exception>(() => SqlUpdateSupport.ExecuteAsync(db, provider, step));
        Assert.Equal(wrong, await SqlUpdateSupport.CatalogDumpAsync(db, schema));
        Assert.Empty(await SchemaProbe.ActualIndexesAsync(db, ["account"]));

        // At version 5: applies, creates the index, records 6.
        await UpgradeTestSupport.SetVersionAsync(db, "auth", 5);
        await SqlUpdateSupport.ExecuteAsync(db, provider, step);
        Assert.Equal(6, await UpgradeTestSupport.ReadVersionAsync(db, "auth"));
        Assert.Contains(await SchemaProbe.ActualIndexesAsync(db, ["account"]), i => i.Name == "IX_account_Username");

        // Again: now at 6, refused, nothing changes.
        string applied = await SqlUpdateSupport.CatalogDumpAsync(db, schema);
        await Assert.ThrowsAnyAsync<Exception>(() => SqlUpdateSupport.ExecuteAsync(db, provider, step));
        Assert.Equal(applied, await SqlUpdateSupport.CatalogDumpAsync(db, schema));
    }

    public static IEnumerable<object[]> NonPostgresProviders()
        => TestDatabases.AvailableProviders().Where(p => (DatabaseProvider)p[0] != DatabaseProvider.PostgreSql);

    /// <summary>
    /// A baseline cut off after its first K changes (a non-transactional engine's failure, or a lost connection) leaves what an
    /// interrupted bootstrap leaves: the baseline refuses to run again, and the bootstrapper (arcane-db upgrade) resumes it.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonPostgresProviders))]
    public async Task AnInterruptedBaseline_IsRefusedByTheBaseline_AndFinishedByArcaneDbUpgrade(DatabaseProvider provider)
    {
        const string component = "auth";
        SchemaDefinition schema = SchemaProbe.SchemaOf(component);
        string text = File.ReadAllText(SqlUpdateSupport.OrderedFiles(component, provider)[0]);
        string[] all = [.. MysqlClientSplitter.Split(text)];
        int first = Array.FindIndex(all, s => s.StartsWith("ALTER DATABASE", StringComparison.Ordinal) || s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        int last = Array.FindLastIndex(all, s => s.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.True(first >= 0 && last > first);
        string[] work = all[first..last];
        Assert.Contains(work, s => s.StartsWith("INSERT INTO", StringComparison.Ordinal) && s.Contains("VALUES (1, 0)", StringComparison.Ordinal));

        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync(component, fresh);
        await using DbContext freshDb = SchemaProbe.CreateContext(component, fresh);
        string expected = await SqlUpdateSupport.CatalogDumpAsync(freshDb, schema);

        foreach (int cut in new[] { 1, 2, 3, work.Length / 2, work.Length - 1 }.Distinct().Where(k => k >= 1 && k < work.Length))
        {
            DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
            await using DbContext db = SchemaProbe.CreateContext(component, connection);
            await CreateEmptyAsync(db);
            foreach (string statement in work[..cut])
            {
                await SqlUpdateSupport.ExecuteAsync(db, provider, statement);
            }

            if (await SchemaCatalog.TableExistsAsync(db, schema.VersionTable))
            {
                string interrupted = await SqlUpdateSupport.CatalogDumpAsync(db, schema, tolerateMissingVersionTable: true);
                await Assert.ThrowsAnyAsync<Exception>(() => SqlUpdateSupport.ExecuteAsync(db, provider, text));
                Assert.Equal(interrupted, await SqlUpdateSupport.CatalogDumpAsync(db, schema, tolerateMissingVersionTable: true));
            }

            await SchemaUpgrader.ApplyAsync(db, schema); // the README's recovery route: 'arcane-db upgrade' plans (and could refuse) first
            Assert.True(expected == await SqlUpdateSupport.CatalogDumpAsync(db, schema), $"a baseline interrupted after {cut} change(s) did not converge");
        }
    }

    /// <summary>A pre-M5 database (the component's tables, no version table) is refused by the baseline, which changes nothing.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheBaseline_RefusesATableSetWithoutAVersionTable(DatabaseProvider provider)
    {
        SchemaDefinition schema = SchemaProbe.SchemaOf("characters");
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharactersPreM5Context legacy = TestContexts.Create<CharactersPreM5Context>(connection))
        {
            await TestContexts.CreateTablesAsync(legacy);
        }

        await using DbContext db = SchemaProbe.CreateContext("characters", connection);
        string before = await SqlUpdateSupport.CatalogDumpAsync(db, schema, tolerateMissingVersionTable: true);
        Assert.Contains("table characters", before);
        string baseline = File.ReadAllText(SqlUpdateSupport.OrderedFiles("characters", provider)[0]);
        await Assert.ThrowsAnyAsync<Exception>(() => SqlUpdateSupport.ExecuteAsync(db, provider, baseline));
        Assert.Equal(before, await SqlUpdateSupport.CatalogDumpAsync(db, schema, tolerateMissingVersionTable: true));
    }
}
