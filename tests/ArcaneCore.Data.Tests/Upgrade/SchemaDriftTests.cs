using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>A one-table component with a required column, to provoke nullability drift on every engine.</summary>
internal sealed class DriftProbeRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
}

internal sealed class DriftProbeContext(DbContextOptions<DriftProbeContext> options) : DbContext(options)
{
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "driftprobe",
        CurrentVersion = 1,
        Version1Tables = ["drift_probe"],
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);
        modelBuilder.Entity<DriftProbeRow>(entity =>
        {
            entity.ToTable("drift_probe");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(50).IsRequired();
            entity.Property(r => r.Note).HasMaxLength(50);
        });
    }
}

/// <summary>
/// The drift checker: every difference between a live database and the model is reported with the
/// right severity, nothing is changed, and a checker that examined nothing cannot pass.
/// </summary>
public sealed class SchemaDriftTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> ProviderComponents()
        => TestDatabases.AvailableProviders().SelectMany(p => SchemaProbe.Components().Select(c => new[] { p[0], c[0] }));

    [Theory]
    [MemberData(nameof(ProviderComponents))]
    public async Task FreshCurrentDatabase_HasNoErrors_AndTheCheckerLookedAtSomething(DatabaseProvider provider, string component)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync(component, connection);
        await using DbContext db = SchemaProbe.CreateContext(component, connection);

        DriftReport report = await SchemaDriftChecker.CheckAsync(db, SchemaProbe.SchemaOf(component));

        Assert.True(report.IsClean, string.Join("; ", report.Errors));
        Assert.Equal(SchemaProbe.ModelTables(db).Count, report.TablesExamined);
        Assert.Equal(SchemaProbe.ModelIndexes(db).Count, report.IndexesExamined);
        Assert.Equal(SchemaProbe.ModelColumns(db).Values.Sum(c => c.Length), report.ColumnsExamined);
        Assert.True(report.TablesExamined > 1 && report.ColumnsExamined > 5 && report.IndexesExamined > 0);
        Assert.DoesNotContain(report.Findings, f => f.Severity != DriftSeverity.Info); // a fresh database is clean, not merely error-free
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DroppedIndexes_AreMissingIndexErrors_AndTheRepairClearsThem(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        int modelIndexes = SchemaProbe.ModelIndexes(db).Count(i => i.Table == "guild_member");
        Assert.True(modelIndexes > 0);
        await SchemaProbe.DropIndexesAsync(db, "guild_member");
        string before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);

        DriftReport report = await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema);

        Assert.False(report.IsClean);
        Assert.Equal(modelIndexes, report.Errors.Count(f => f.Kind == DriftKind.MissingIndex && f.Table == "guild_member"));
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema)); // read-only

        await UpgradeTestSupport.SetVersionAsync(db, "characters", CharacterDbContext.IndexRepairVersion - 1);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        Assert.True((await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema)).IsClean);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StrayAndDroppedColumns_AreErrors(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("world", connection);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaProbe.ExecuteAsync(db, $"ALTER TABLE {TestContexts.Quote(db, "creature_spawn")} ADD COLUMN {TestContexts.Quote(db, "Stray")} INTEGER NULL");

        DriftReport stray = await SchemaDriftChecker.CheckAsync(db, WorldDbContext.Schema);
        DriftFinding extra = Assert.Single(stray.Errors);
        Assert.Equal(DriftKind.UnexpectedColumn, extra.Kind);
        Assert.Equal("creature_spawn", extra.Table);

        await SchemaProbe.ExecuteAsync(db, $"ALTER TABLE {TestContexts.Quote(db, "creature_spawn")} DROP COLUMN {TestContexts.Quote(db, "Stray")}");
        string dropped = DroppableColumn(db, out string table);
        await SchemaProbe.ExecuteAsync(db, $"ALTER TABLE {TestContexts.Quote(db, table)} DROP COLUMN {TestContexts.Quote(db, dropped)}");
        DriftReport missing = await SchemaDriftChecker.CheckAsync(db, WorldDbContext.Schema);
        DriftFinding gone = Assert.Single(missing.Errors);
        Assert.Equal(DriftKind.MissingColumn, gone.Kind);
        Assert.Contains(dropped, gone.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NullabilityDifference_IsAnError_AndPrimaryKeysAreNotFlagged(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using DriftProbeContext db = TestContexts.Create<DriftProbeContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, DriftProbeContext.Schema);
        Assert.True((await SchemaDriftChecker.CheckAsync(db, DriftProbeContext.Schema)).IsClean);

        await SchemaProbe.ExecuteAsync(db, $"DROP TABLE {TestContexts.Quote(db, "drift_probe")}");
        await SchemaProbe.ExecuteAsync(db,
            $"CREATE TABLE {TestContexts.Quote(db, "drift_probe")} ({TestContexts.Quote(db, "Id")} INTEGER NOT NULL PRIMARY KEY, " +
            $"{TestContexts.Quote(db, "Name")} VARCHAR(50) NULL, {TestContexts.Quote(db, "Note")} VARCHAR(50) NOT NULL)");

        DriftReport report = await SchemaDriftChecker.CheckAsync(db, DriftProbeContext.Schema);

        Assert.Equal(2, report.Errors.Count);
        Assert.All(report.Errors, f => Assert.Equal(DriftKind.NullabilityMismatch, f.Kind));
        Assert.Contains(report.Errors, f => f.Detail.Contains("drift_probe.Name", StringComparison.Ordinal));
        Assert.Contains(report.Errors, f => f.Detail.Contains("drift_probe.Note", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RenamedIndex_IsInfo_ExtraIndex_IsAWarning_ConflictingIndex_IsAnError(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        string guildMember = TestContexts.Quote(db, "guild_member");

        await SchemaProbe.DropIndexesAsync(db, "guild_member");
        await SchemaProbe.ExecuteAsync(db, $"CREATE UNIQUE INDEX {TestContexts.Quote(db, "dba_member_once")} ON {guildMember} ({TestContexts.Quote(db, "CharacterId")})");
        DriftReport renamed = await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema);
        Assert.True(renamed.IsClean, string.Join("; ", renamed.Errors));
        Assert.Contains(renamed.Findings, f => f is { Severity: DriftSeverity.Info, Kind: DriftKind.IndexUnderOtherName, Table: "guild_member" });

        await SchemaProbe.ExecuteAsync(db, $"CREATE INDEX {TestContexts.Quote(db, "dba_rank")} ON {guildMember} ({TestContexts.Quote(db, "Rank")})");
        DriftReport extra = await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema);
        Assert.True(extra.IsClean);
        Assert.Contains(extra.Findings, f => f is { Severity: DriftSeverity.Warning, Kind: DriftKind.ExtraIndex, Table: "guild_member" } && f.Detail.Contains("dba_rank", StringComparison.Ordinal));

        await SchemaProbe.DropIndexesAsync(db, "guild_member");
        await SchemaProbe.ExecuteAsync(db, $"CREATE INDEX {TestContexts.Quote(db, "IX_guild_member_CharacterId")} ON {guildMember} ({TestContexts.Quote(db, "CharacterId")})");
        DriftReport conflict = await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema);
        Assert.Contains(conflict.Errors, f => f is { Kind: DriftKind.ConflictingIndex, Table: "guild_member" });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ForeignTables_AreWarnings_AndOtherComponentsTablesAreNotForeign(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        foreach (string component in new[] { "auth", "characters", "world" })
        {
            await SchemaProbe.EnsureCurrentAsync(component, connection);
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaProbe.ExecuteAsync(db, $"CREATE TABLE {TestContexts.Quote(db, "stranger")} ({TestContexts.Quote(db, "x")} INTEGER NOT NULL)");

        string[] union = [.. new[] { "auth", "characters", "world" }.SelectMany(c =>
        {
            using DbContext other = SchemaProbe.CreateContext(c, connection);
            return SchemaProbe.ModelTables(other);
        })];
        DriftReport report = await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema, union);

        Assert.True(report.IsClean);
        DriftFinding foreign = Assert.Single(report.Findings, f => f.Kind == DriftKind.ForeignTable);
        Assert.Equal("stranger", foreign.Table);
        Assert.Equal(DriftSeverity.Warning, foreign.Severity);

        // Knowing only this component's tables, the other components' tables look foreign: why the union is passed.
        DriftReport narrow = await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema, SchemaProbe.ModelTables(db));
        Assert.Contains(narrow.Findings, f => f.Kind == DriftKind.ForeignTable && f.Table == "account");

        // Null skips the check.
        Assert.DoesNotContain((await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema)).Findings, f => f.Kind == DriftKind.ForeignTable);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task VersionRow_MissingOrBehind_IsAnError_AndAnOldDatabaseReportsInsteadOfThrowing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);

        await UpgradeTestSupport.SetVersionAsync(db, "characters", CharacterDbContext.Schema.CurrentVersion - 1);
        Assert.Contains((await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema)).Errors, f => f.Kind == DriftKind.VersionBehind);

        await UpgradeTestSupport.SetVersionAsync(db, "characters", CharacterDbContext.Schema.CurrentVersion + 1);
        Assert.Contains((await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema)).Errors, f => f.Kind == DriftKind.VersionNewer);

        await SchemaProbe.ExecuteAsync(db, $"DELETE FROM {TestContexts.Quote(db, CharacterDbContext.Schema.VersionTable)}");
        Assert.Contains((await SchemaDriftChecker.CheckAsync(db, CharacterDbContext.Schema)).Errors, f => f.Kind == DriftKind.VersionMissing);

        // A version-1 legacy database: errors for everything later versions added, no exception.
        DatabaseConnectionOptions legacy = await _databases.CreateAsync(provider);
        await using (CharactersM5Context m5 = TestContexts.Create<CharactersM5Context>(legacy))
        {
            await SchemaBootstrapper.EnsureAsync(m5, CharactersM5Context.Schema);
        }

        await using CharacterDbContext old = TestContexts.Create<CharacterDbContext>(legacy);
        string before = await UpgradeTestSupport.SnapshotAsync(old, CharacterDbContext.Schema);
        DriftReport report = await SchemaDriftChecker.CheckAsync(old, CharacterDbContext.Schema);
        Assert.Contains(report.Errors, f => f.Kind == DriftKind.VersionBehind);
        Assert.True(report.Errors.Count(f => f.Kind == DriftKind.MissingTable) > 10);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(old, CharacterDbContext.Schema));
    }

    [Fact]
    public void SettingsRules_FlagNonInnoDbTablesLatin1DatabasesAndNonUtf8Encodings_PerEngine()
    {
        const string maria = "Pomelo.EntityFrameworkCore.MySql";
        const string postgres = "Npgsql.EntityFrameworkCore.PostgreSQL";
        var engines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a"] = "InnoDB", ["b"] = "MyISAM" };

        IReadOnlyList<DriftFinding> findings = SchemaDriftChecker.CheckSettings(maria, new DatabaseSettings("latin1", null, engines), ["a", "b", "c"]);
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f is { Kind: DriftKind.NonInnoDbTable, Table: "b", Severity: DriftSeverity.Warning });
        Assert.Contains(findings, f => f.Kind == DriftKind.DatabaseCharset && f.Detail.Contains("latin1", StringComparison.Ordinal));

        Assert.Empty(SchemaDriftChecker.CheckSettings(maria, new DatabaseSettings("utf8mb4", null, new Dictionary<string, string> { ["a"] = "innodb" }), ["a"]));

        IReadOnlyList<DriftFinding> pg = SchemaDriftChecker.CheckSettings(postgres, new DatabaseSettings(null, "LATIN1", new Dictionary<string, string>()), ["a"]);
        Assert.Equal(DriftKind.DatabaseEncoding, Assert.Single(pg).Kind);
        Assert.Empty(SchemaDriftChecker.CheckSettings(postgres, new DatabaseSettings(null, "UTF8", new Dictionary<string, string>()), ["a"]));

        // The other engine's setting does not apply.
        Assert.Empty(SchemaDriftChecker.CheckSettings("Microsoft.EntityFrameworkCore.Sqlite", new DatabaseSettings("latin1", "LATIN1", engines), ["a", "b"]));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MariaDbEngineAndCharsetDrift_AreWarnings_HostedOnly(DatabaseProvider provider)
    {
        if (provider != DatabaseProvider.MariaDb)
        {
            return; // engines and charsets exist on MariaDB/MySQL only; the rules themselves are unit-tested above
        }

        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
        await SchemaProbe.ExecuteAsync(db, $"ALTER TABLE {TestContexts.Quote(db, "realmlist")} ENGINE=MyISAM");
        DriftReport engine = await SchemaDriftChecker.CheckAsync(db, AuthDbContext.Schema);
        Assert.Contains(engine.Warnings, f => f is { Kind: DriftKind.NonInnoDbTable, Table: "realmlist" });
        Assert.True(engine.IsClean);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    /// <summary>A model column of the world database that a plain DROP COLUMN can remove: not in the key, not indexed, not required by a later add.</summary>
    private static string DroppableColumn(DbContext db, out string table)
    {
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        foreach (ITable t in model.Tables.Where(t => t.Name != "world_schema"))
        {
            var indexed = new HashSet<string>(t.Indexes.SelectMany(i => i.Columns.Select(c => c.Name)), StringComparer.OrdinalIgnoreCase);
            var key = new HashSet<string>(t.PrimaryKey?.Columns.Select(c => c.Name) ?? [], StringComparer.OrdinalIgnoreCase);
            IColumn? column = t.Columns.FirstOrDefault(c => !indexed.Contains(c.Name) && !key.Contains(c.Name));
            if (column is not null)
            {
                table = t.Name;
                return column.Name;
            }
        }

        throw new InvalidOperationException("no droppable column in the world model");
    }
}
