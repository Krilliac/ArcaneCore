using System.Text.Json;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The fail-closed guard of reserved schema placeholders (<see cref="IReservedSchemaGap"/>, Schema/ReservedSchemaGaps.cs).
/// The bootstrapper and planner track one version number, so a database that is created or upgraded through a placeholder's
/// empty step is recorded at a version past it and the owning lane's real step would never run. Outside the test projects
/// (whose runtimeconfig turns <see cref="ReservedSchemaGaps.AllowSwitch"/> on) that must be refused before anything is written.
/// </summary>
public sealed class ReservedSchemaGapGuardTests
{
    private static readonly SchemaUpgradeOptions ServerProcess = new() { AllowReservedSchemaGaps = false };

    /// <summary>The real characters schema with one version (the one below the top) pretending to be held by a placeholder.</summary>
    private static int GapVersion => CharacterDbContext.Schema.CurrentVersion - 1;

    private static SchemaDefinition WithGapAt(int gap) => new()
    {
        Component = CharacterDbContext.Schema.Component,
        CurrentVersion = CharacterDbContext.Schema.CurrentVersion,
        Version1Tables = CharacterDbContext.Schema.Version1Tables,
        Steps = CharacterDbContext.Schema.Steps,
        ReservedGapVersions = [gap],
    };

    [Fact]
    public void Compose_RecordsTheVersionsStillHeldByPlaceholders_AndForgetsOnesAnOwnerClaims()
    {
        SchemaDefinition held = DataModules.Compose(DatabaseComponent.Characters, "synthetic", [], [], [new Owner(2), new Gap(3), new Gap(4), new Owner(5)]);
        Assert.Equal([3, 4], held.ReservedGapVersions);
        Assert.Equal(5, held.CurrentVersion);

        SchemaDefinition owned = DataModules.Compose(DatabaseComponent.Characters, "synthetic", [], [], [new Owner(2), new Gap(3), new Owner(3), new Gap(4), new Owner(4), new Owner(5)]);
        Assert.Empty(owned.ReservedGapVersions);
        Assert.Equal(5, owned.CurrentVersion);
    }

    [Fact]
    public async Task AServerProcess_RefusesToCreateADatabaseThroughAPlaceholder_AndWritesNoVersion()
    {
        await using Database database = await Database.OpenAsync();

        SchemaMismatchException refused = await Assert.ThrowsAsync<SchemaMismatchException>(
            () => SchemaBootstrapper.EnsureAsync(database.Db, WithGapAt(GapVersion), ServerProcess));

        Assert.Contains($"placeholder version(s) {GapVersion}", refused.Message, StringComparison.Ordinal);
        Assert.False(await database.TableExistsAsync(CharacterDbContext.Schema.VersionTable));
        Assert.False(await database.TableExistsAsync("characters"));
    }

    [Fact]
    public async Task AServerProcess_RefusesToUpgradeADatabaseAcrossAPlaceholder_AndLeavesItsVersion()
    {
        await using Database database = await Database.OpenAsync();
        await SchemaBootstrapper.EnsureAsync(database.Db, CharacterDbContext.Schema);
        await database.SetVersionAsync(GapVersion - 1);

        await Assert.ThrowsAsync<SchemaMismatchException>(
            () => SchemaBootstrapper.EnsureAsync(database.Db, WithGapAt(GapVersion), ServerProcess));

        Assert.Equal(GapVersion - 1, await database.VersionAsync());
    }

    [Fact]
    public async Task ADatabaseAlreadyPastThePlaceholder_IsUpgradedNormally()
    {
        // Only applying a placeholder is refused: a database the owning build already brought past it upgrades as usual.
        await using Database database = await Database.OpenAsync();
        await SchemaBootstrapper.EnsureAsync(database.Db, CharacterDbContext.Schema);
        await database.SetVersionAsync(GapVersion);

        await SchemaBootstrapper.EnsureAsync(database.Db, WithGapAt(GapVersion), ServerProcess);

        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await database.VersionAsync());
    }

    [Fact]
    public async Task TheCreateOnlyStartupPolicy_StillRefusesAFreshCreateThroughAPlaceholder()
    {
        await using Database database = await Database.OpenAsync();
        var createOnly = new SchemaUpgradeOptions { AllowReservedSchemaGaps = false, Policy = SchemaPolicy.CreateOnly };

        await Assert.ThrowsAsync<SchemaMismatchException>(
            () => SchemaBootstrapper.EnsureAsync(database.Db, WithGapAt(GapVersion), createOnly));

        Assert.False(await database.TableExistsAsync(CharacterDbContext.Schema.VersionTable));
    }

    [Fact]
    public async Task ThePlanner_RefusesThePlansTheApplyRefuses()
    {
        await using Database database = await Database.OpenAsync();
        SchemaDefinition gapped = WithGapAt(GapVersion);

        SchemaPlan fresh = await SchemaPlanner.PlanAsync(database.Db, gapped, includeScript: false, allowReservedGaps: false);
        Assert.True(fresh.IsRefused);
        Assert.Equal(SchemaState.Fresh, fresh.State);
        Assert.Contains("placeholder", fresh.Refusal, StringComparison.Ordinal);

        await SchemaBootstrapper.EnsureAsync(database.Db, CharacterDbContext.Schema);
        await database.SetVersionAsync(GapVersion - 1);
        SchemaPlan behind = await SchemaPlanner.PlanAsync(database.Db, gapped, includeScript: false, allowReservedGaps: false);
        Assert.True(behind.IsRefused);
        Assert.Empty(behind.Steps);

        await database.SetVersionAsync(GapVersion);
        SchemaPlan past = await SchemaPlanner.PlanAsync(database.Db, gapped, includeScript: false, allowReservedGaps: false);
        Assert.False(past.IsRefused);
        Assert.Equal([CharacterDbContext.Schema.CurrentVersion], past.PendingVersions);
    }

    [Fact]
    public async Task ATestProcess_IsAllowedThroughByItsRuntimeConfig()
    {
        // The switch reaches this test host through the test project's runtimeconfig (Directory.Build.props) ...
        Assert.True(ReservedSchemaGaps.AllowedInThisProcess);
        Assert.True(new SchemaUpgradeOptions().AllowReservedSchemaGaps);

        // ... so the default options and the default plan let a test database through the placeholder.
        await using Database database = await Database.OpenAsync();
        Assert.False((await SchemaPlanner.PlanAsync(database.Db, WithGapAt(GapVersion))).IsRefused);
        await SchemaBootstrapper.EnsureAsync(database.Db, WithGapAt(GapVersion));
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await database.VersionAsync());
    }

    [Fact]
    public void AShippedProgram_DoesNotCarryTheSwitch()
    {
        // The account tool (arcane-account) is an executable this project references, so its runtimeconfig is copied next to the tests.
        string path = Path.Combine(AppContext.BaseDirectory, "arcane-account.runtimeconfig.json");
        Assert.True(File.Exists(path), path + " is missing: the check below would prove nothing");
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement options = config.RootElement.GetProperty("runtimeOptions");
        Assert.False(
            options.TryGetProperty("configProperties", out JsonElement properties) && properties.TryGetProperty(ReservedSchemaGaps.AllowSwitch, out _),
            "a non-test program must never be allowed through a reserved schema placeholder");

        string self = Path.Combine(AppContext.BaseDirectory, "ArcaneCore.Data.Tests.runtimeconfig.json");
        Assert.Contains(ReservedSchemaGaps.AllowSwitch, File.ReadAllText(self), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThisBuildsOwnCharactersSchema_IsRefusedToAServerProcessExactlyWhileItHoldsPlaceholders()
    {
        await using Database database = await Database.OpenAsync();
        if (CharacterDbContext.Schema.ReservedGapVersions.Count == 0)
        {
            await SchemaBootstrapper.EnsureAsync(database.Db, CharacterDbContext.Schema, ServerProcess);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await database.VersionAsync());
        }
        else
        {
            await Assert.ThrowsAsync<SchemaMismatchException>(
                () => SchemaBootstrapper.EnsureAsync(database.Db, CharacterDbContext.Schema, ServerProcess));
            Assert.False(await database.TableExistsAsync(CharacterDbContext.Schema.VersionTable));
        }
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Database(SqliteConnection connection)
        {
            _connection = connection;
            Db = new CharacterDbContext(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options);
        }

        public CharacterDbContext Db { get; }

        public static async Task<Database> OpenAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            return new Database(connection);
        }

        public async Task<bool> TableExistsAsync(string table)
        {
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", table);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        }

        public Task SetVersionAsync(int version)
            => Db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, version));

        public Task<int> VersionAsync() => Db.Set<SchemaVersionRow>().AsNoTracking().Select(r => r.Version).SingleAsync();

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class Gap(int version) : ReservedSchemaGap
    {
        public override DatabaseComponent Component => DatabaseComponent.Characters;

        public override int SchemaVersion => version;
    }

    private sealed class Owner(int version) : IDataModule
    {
        public DatabaseComponent Component => DatabaseComponent.Characters;

        public int SchemaVersion => version;

        public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

        public void ConfigureModel(ModelBuilder modelBuilder)
        {
        }

        public void AddServices(IServiceCollection services)
        {
        }
    }
}
