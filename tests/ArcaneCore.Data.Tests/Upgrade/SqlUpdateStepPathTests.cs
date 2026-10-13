using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

internal sealed class ProbeBaseRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class ProbeTableRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int C { get; set; }
}

/// <summary>
/// A stand-in component whose steps exercise the real step path: schema 1 has probe_base; step 2 creates probe_t; step 3 adds
/// the column probe_t.c. The model is the final one; the level says how much of it a context carries.
/// </summary>
internal abstract class StepProbeContext(DbContextOptions options, int level, bool indexOnC = false, bool keyOnC = false) : DbContext(options)
{
    public static readonly SchemaDefinition Final = new()
    {
        Component = "stepprobe",
        CurrentVersion = 3,
        Version1Tables = ["probe_base"],
        Steps = [new SchemaStep(2, [new CreateTableChange("probe_t")]), new SchemaStep(3, [new AddColumnChange("probe_t", "c")])],
    };

    public static readonly SchemaDefinition AtTwo = new()
    {
        Component = "stepprobe",
        CurrentVersion = 2,
        Version1Tables = ["probe_base"],
        Steps = [new SchemaStep(2, [new CreateTableChange("probe_t")])],
    };

    public static readonly SchemaDefinition Base = new()
    {
        Component = "stepprobe",
        CurrentVersion = 1,
        Version1Tables = ["probe_base"],
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Final);
        modelBuilder.Entity<ProbeBaseRow>(entity =>
        {
            entity.ToTable("probe_base");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(20).IsRequired();
        });

        if (level >= 2)
        {
            modelBuilder.Entity<ProbeTableRow>(entity =>
            {
                entity.ToTable("probe_t");
                if (keyOnC)
                {
                    entity.HasKey(r => new { r.Id, r.C });
                }
                else
                {
                    entity.HasKey(r => r.Id);
                }

                entity.Property(r => r.Id).ValueGeneratedNever();
                entity.Property(r => r.Name).HasMaxLength(20).IsRequired();
                if (level >= 3)
                {
                    entity.Property(r => r.C).HasColumnName("c");
                    if (indexOnC)
                    {
                        entity.HasIndex(r => r.C);
                    }
                }
                else
                {
                    entity.Ignore(r => r.C);
                }
            });
        }
    }
}

internal sealed class StepProbeV1(DbContextOptions<StepProbeV1> options) : StepProbeContext(options, 1);

internal sealed class StepProbeV2(DbContextOptions<StepProbeV2> options) : StepProbeContext(options, 2);

internal sealed class StepProbeV3(DbContextOptions<StepProbeV3> options) : StepProbeContext(options, 3);

internal sealed class StepProbeIndexed(DbContextOptions<StepProbeIndexed> options) : StepProbeContext(options, 3, indexOnC: true);

internal sealed class StepProbeKeyed(DbContextOptions<StepProbeKeyed> options) : StepProbeContext(options, 3, keyOnC: true);

/// <summary>
/// The real step path of the update-file writer: a table created by step N whose column arrives with step N+1, from a snapshot,
/// replayed against a bootstrap; and the refusal when the held-back column is in the table's key or an index.
/// </summary>
public sealed class SqlUpdateStepPathTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static UpdateComponent Probe<T>(SchemaDefinition definition, Func<DbContextOptions<T>, T> create) where T : DbContext
        => new("stepprobe", definition, DatabaseComponent.Auth, p => UpdateComponent.Offline(p, create), v => ($"probe step {v}", $"ProbeStep{v}"));

    private static async Task<(UpdateSet Baseline, UpdateSet Steps)> GenerateAsync()
    {
        UpdateSet baseline = await UpdateScriptWriter.GenerateAsync(Probe<StepProbeV1>(StepProbeContext.Base, o => new StepProbeV1(o)), null);
        UpdateSet steps = await UpdateScriptWriter.GenerateAsync(Probe<StepProbeV3>(StepProbeContext.Final, o => new StepProbeV3(o)), baseline.Snapshot);
        return (baseline, steps);
    }

    private static string Quoted(string folder, string name) => folder == "mariadb" ? $"`{name}`" : $"\"{name}\"";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("mariadb")]
    [InlineData("postgresql")]
    public async Task TheCreatingStep_LacksTheColumnALaterStepAdds(string folder)
    {
        (_, UpdateSet steps) = await GenerateAsync();
        UpdateFile create = steps.Files.Single(f => f.Path == $"stepprobe/{folder}/0002_ProbeStep2.sql");
        UpdateFile add = steps.Files.Single(f => f.Path == $"stepprobe/{folder}/0003_ProbeStep3.sql");

        Assert.Contains("probe_t", create.Content);
        Assert.Contains(Quoted(folder, "Name"), create.Content);
        Assert.DoesNotMatch(@"(?m)^\s+[`""]?c[`""]?\s", create.Content);
        Assert.Contains("ALTER TABLE", add.Content);
        Assert.Matches(@"ADD\s+[`""]?c[`""]?\s", add.Content);
        Assert.DoesNotContain("CREATE TABLE", add.Content);

        ProviderSnapshot snapshot = steps.Snapshot.Providers[folder];
        Assert.Equal(3, steps.Snapshot.Version);
        Assert.DoesNotContain(snapshot.Previous!.Single(t => t.Name == "probe_t").Columns, c => c.Name == "c");
        Assert.Contains(snapshot.Tables.Single(t => t.Name == "probe_t").Columns, c => c.Name == "c");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task BaselineAndBothSteps_BuildWhatABootstrapBuilds(DatabaseProvider provider)
    {
        (UpdateSet baseline, UpdateSet steps) = await GenerateAsync();
        string folder = SqlUpdateSupport.Folder(provider);
        string[] files =
        [
            baseline.Files.Single(f => f.Path.Contains("/" + folder + "/")).Content,
            .. steps.Files.Where(f => f.Path.Contains("/" + folder + "/")).OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f.Content),
        ];
        Assert.Equal(3, files.Length);

        DatabaseConnectionOptions replayed = await _databases.CreateAsync(provider);
        await using StepProbeV3 db = TestContexts.Create<StepProbeV3>(replayed);
        await ((IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>()).CreateAsync();
        foreach (string file in files)
        {
            await SqlUpdateSupport.ExecuteAsync(db, provider, file);
        }

        Assert.Equal(3, await UpgradeTestSupport.ReadVersionAsync(db, "stepprobe"));
        DriftReport drift = await SchemaDriftChecker.CheckAsync(db, StepProbeContext.Final);
        Assert.True(drift.IsClean, string.Join("; ", drift.Errors));

        // The reference is a bootstrapper UPGRADE (schema 2, then 3, as the history ran): an added NOT NULL column gets a DEFAULT 0 there, a fresh create has none.
        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);
        await using (StepProbeV2 v2 = TestContexts.Create<StepProbeV2>(fresh))
        {
            await SchemaBootstrapper.EnsureAsync(v2, StepProbeContext.AtTwo);
        }

        await using StepProbeV3 freshDb = TestContexts.Create<StepProbeV3>(fresh);
        await SchemaBootstrapper.EnsureAsync(freshDb, StepProbeContext.Final);

        string expected = await SqlUpdateSupport.CatalogDumpAsync(freshDb, StepProbeContext.Final);
        Assert.Contains("  column c|", expected);
        Assert.Equal(expected, await SqlUpdateSupport.CatalogDumpAsync(db, StepProbeContext.Final));
    }

    [Fact]
    public async Task AHeldBackColumn_InAnIndex_IsRefused()
    {
        UpdateSet baseline = await UpdateScriptWriter.GenerateAsync(Probe<StepProbeV1>(StepProbeContext.Base, o => new StepProbeV1(o)), null);
        UpdateExportException ex = await Assert.ThrowsAsync<UpdateExportException>(
            () => UpdateScriptWriter.GenerateAsync(Probe<StepProbeIndexed>(StepProbeContext.Final, o => new StepProbeIndexed(o)), baseline.Snapshot));
        Assert.Contains("later step", ex.Message);
    }

    [Fact]
    public async Task AHeldBackColumn_InTheKey_IsRefused()
    {
        UpdateSet baseline = await UpdateScriptWriter.GenerateAsync(Probe<StepProbeV1>(StepProbeContext.Base, o => new StepProbeV1(o)), null);
        UpdateExportException ex = await Assert.ThrowsAsync<UpdateExportException>(
            () => UpdateScriptWriter.GenerateAsync(Probe<StepProbeKeyed>(StepProbeContext.Final, o => new StepProbeKeyed(o)), baseline.Snapshot));
        Assert.Contains("later step", ex.Message);
    }
}
