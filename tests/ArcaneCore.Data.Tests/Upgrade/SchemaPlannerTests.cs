using System.Text.RegularExpressions;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// The read-only planner: it must classify a database and predict what the bootstrapper does,
/// without creating, locking or writing anything. MariaDB/PostgreSQL theories run only where
/// ARCANECORE_TEST_MARIADB / ARCANECORE_TEST_POSTGRES are set (hosted CI).
/// </summary>
public sealed class SchemaPlannerTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private static async Task<DatabaseConnectionOptions> NewLegacyCharactersV1Async(TestDatabases databases, DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await databases.CreateAsync(provider);
        await using CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(connection);
        await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
        return connection;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LegacyV1Database_IsBehind_ListsExactlyThePendingSteps_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersV1Async(_databases, provider);
        bool sqlite = provider == DatabaseProvider.Sqlite;
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        string before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);
        string? fileBefore = sqlite ? UpgradeTestSupport.SettledFileFingerprint(connection) : null;

        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);

        Assert.Equal(SchemaState.Behind, plan.State);
        Assert.Equal(1, plan.DatabaseVersion);
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, plan.CodeVersion);
        Assert.Equal(CharacterDbContext.Schema.Steps.Where(s => s.Version > 1).Select(s => s.Version), plan.PendingVersions);
        Assert.NotEmpty(plan.PendingVersions); // a planner that plans nothing must not pass vacuously
        Assert.True(plan.CreateCount > 10, $"the legacy characters database lacks many tables, the plan creates {plan.CreateCount}");
        Assert.False(plan.IsRefused, plan.FirstRefusal);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema));
        if (sqlite)
        {
            Assert.Equal(fileBefore, UpgradeTestSupport.SettledFileFingerprint(connection));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NonexistentDatabase_IsMissing_AndIsNotCreatedByPlanning(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);

        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);

        Assert.Equal(SchemaState.Missing, plan.State);
        Assert.Null(plan.DatabaseVersion);
        Assert.Equal([CharacterDbContext.Schema.CurrentVersion], plan.PendingVersions);
        Assert.True(plan.CreateCount > 10);
        Assert.All(plan.Actions.Where(a => a.Object == ChangeObject.Table), a => Assert.Equal(ChangeDecision.Create, a.Decision));
        Assert.False(await ((IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>()).ExistsAsync());
        if (provider == DatabaseProvider.Sqlite)
        {
            Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(connection)));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmptyExistingDatabase_IsFresh_AndStaysEmpty(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await ((IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>()).CreateAsync();
        string before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);

        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);

        Assert.Equal(SchemaState.Fresh, plan.State);
        Assert.True(plan.CreateCount > 10);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema));
        Assert.False(await SchemaCatalog.TableExistsAsync(db, CharacterDbContext.Schema.VersionTable));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreM5Database_IsAdoptV1_AndTheVersionTableIsNotCreated(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharactersPreM5Context legacy = TestContexts.Create<CharactersPreM5Context>(connection))
        {
            await TestContexts.CreateTablesAsync(legacy);
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);

        Assert.Equal(SchemaState.AdoptV1, plan.State);
        Assert.Equal(1, plan.PendingVersions[0]);
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, plan.PendingVersions[^1]);
        Assert.False(await SchemaCatalog.TableExistsAsync(db, CharacterDbContext.Schema.VersionTable));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CurrentNewerCreatingAndRowlessDatabases_AreClassified_AndTheirVersionRowIsNeverWritten(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        SchemaDefinition schema = CharacterDbContext.Schema;

        SchemaPlan current = await SchemaPlanner.PlanAsync(db, schema);
        Assert.Equal(SchemaState.Current, current.State);
        Assert.Empty(current.Steps);
        Assert.False(current.NeedsApply);

        await UpgradeTestSupport.SetVersionAsync(db, "characters", schema.CurrentVersion + 1);
        SchemaPlan newer = await SchemaPlanner.PlanAsync(db, schema);
        Assert.Equal(SchemaState.Newer, newer.State);
        Assert.True(newer.IsRefused);
        Assert.Contains((schema.CurrentVersion + 1).ToString(), newer.Refusal, StringComparison.Ordinal);
        Assert.Equal(schema.CurrentVersion + 1, await UpgradeTestSupport.ReadVersionAsync(db, "characters"));

        await UpgradeTestSupport.SetVersionAsync(db, "characters", SchemaBootstrapper.CreatingVersion);
        SchemaPlan creating = await SchemaPlanner.PlanAsync(db, schema);
        Assert.Equal(SchemaState.Creating, creating.State);
        Assert.Equal([schema.CurrentVersion], creating.PendingVersions);
        Assert.Equal(SchemaBootstrapper.CreatingVersion, await UpgradeTestSupport.ReadVersionAsync(db, "characters")); // the marker is not rewritten

        await SchemaProbe.ExecuteAsync(db, $"DELETE FROM {TestContexts.Quote(db, schema.VersionTable)}");
        SchemaPlan rowless = await SchemaPlanner.PlanAsync(db, schema);
        Assert.Equal(SchemaState.NoVersionRow, rowless.State);
        Assert.Null(await UpgradeTestSupport.ReadVersionAsync(db, "characters")); // the table stays rowless: the planner never wrote the 0 marker
        Assert.Equal(0, await UpgradeTestSupport.CountRowsAsync(db, schema.VersionTable));

        await SchemaProbe.ExecuteAsync(db, $"DROP TABLE {TestContexts.Quote(db, "item_text")}");
        SchemaPlan partial = await SchemaPlanner.PlanAsync(db, schema);
        Assert.Equal(SchemaState.Unknown, partial.State);
        Assert.True(partial.IsRefused);
        Assert.Equal(0, await UpgradeTestSupport.CountRowsAsync(db, schema.VersionTable));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task IndexRepairOnATableAPendingStepCreates_IsPlannedAsSatisfied_NotAsAnError(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersV1Async(_databases, provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        SchemaDefinition schema = CharacterDbContext.Schema;

        // Tables created after version 1 and repaired by a later EnsureIndexes step of the same upgrade.
        string[] createdThenRepaired = [.. schema.Steps.Where(s => s.Version > 1)
            .SelectMany(s => s.Changes.OfType<CreateTableChange>().Select(c => (s.Version, c.Table)))
            .Where(c => schema.Steps.Any(s => s.Version > c.Version && s.Changes.OfType<EnsureIndexesChange>().Any(e => e.Table == c.Table)))
            .Select(c => c.Table).Distinct()];
        Assert.NotEmpty(createdThenRepaired);

        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, schema);

        Assert.Empty(plan.Blockers);
        foreach (string table in createdThenRepaired)
        {
            PlannedAction[] indexActions = [.. plan.Actions.Where(a => a.Object == ChangeObject.Index && a.Table == table)];
            Assert.NotEmpty(indexActions);
            // Created with the table by its own step; the repair later finds them there.
            Assert.Equal(1, indexActions.Count(a => a.Decision == ChangeDecision.Create && a.Name == indexActions[0].Name));
            Assert.Contains(indexActions, a => a.Decision == ChangeDecision.Satisfied);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PlanEqualsReality_ForALegacyV1Database(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersV1Async(_databases, provider);
        await AssertPlanMatchesApplyAsync<CharacterDbContext>(connection, CharacterDbContext.Schema);
    }

    [Theory]
    [MemberData(nameof(CandidateBaseline.Variants), MemberType = typeof(CandidateBaseline))]
    public async Task PlanEqualsReality_ForEveryComponentOfTheFrozenBaseline(CandidateBaseline.Variant variant)
    {
        DatabaseConnectionOptions connection = await UpgradeTestSupport.NewBaselineAsync(_databases, variant);
        int planned = 0;
        planned += await AssertPlanMatchesApplyAsync<AuthDbContext>(connection, AuthDbContext.Schema);
        planned += await AssertPlanMatchesApplyAsync<CharacterDbContext>(connection, CharacterDbContext.Schema);
        planned += await AssertPlanMatchesApplyAsync<WorldDbContext>(connection, WorldDbContext.Schema);
        Assert.True(planned > 0, "the frozen baseline is behind the code, so something must have been planned");
    }

    [Fact]
    public async Task DuplicateGuildMembers_AreBlocked_WithTheTextTheApplyFailsWith_AndRowsAreKept()
    {
        DatabaseConnectionOptions connection = await UpgradeTestSupport.NewBaselineAsync(_databases, CandidateBaseline.Variant.HistoricUpgraded);
        await using (var raw = new SqliteConnection($"Data Source={UpgradeTestSupport.SqlitePath(connection)};Pooling=False"))
        {
            await raw.OpenAsync();
            await using SqliteCommand command = raw.CreateCommand();
            command.CommandText =
                "INSERT INTO \"guild_member\" SELECT 50, \"CharacterId\", \"Rank\", \"PublicNote\", \"OfficerNote\", \"Level\", \"ZoneId\", \"LogoutTime\" " +
                "FROM \"guild_member\" WHERE \"GuildId\" = 1";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        string before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);

        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);

        PlannedAction blocker = Assert.Single(plan.Blockers, b => b.Table == "guild_member");
        Assert.Equal(ChangeDecision.Blocked, blocker.Decision);
        Assert.Equal(ChangeObject.Index, blocker.Object);
        Assert.True(blocker.DuplicateGroups >= 1);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema));
        Assert.Equal(2, (await SchemaProbe.SnapshotAsync(db, "guild_member", ["GuildId", "CharacterId"])).Length);

        // The apply fails with the very text the plan reported.
        SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaProbe.EnsureCurrentAsync("characters", connection));
        Assert.Equal(blocker.Detail, ex.Message);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DuplicateAuctionItems_AreBlockedByAPendingIndexRepair_NamingTableIndexAndGroups(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaProbe.DropIndexesAsync(db, "auction");
        await SchemaProbe.ExecuteAsync(db, UpgradeTestSupport.AuctionInsert(db, 1, 777));
        await SchemaProbe.ExecuteAsync(db, UpgradeTestSupport.AuctionInsert(db, 2, 777));
        SchemaDefinition next = UpgradeTestSupport.WithIndexRepairStep(CharacterDbContext.Schema, "auction");

        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, next);

        Assert.Equal(SchemaState.Behind, plan.State);
        PlannedAction blocker = Assert.Single(plan.Blockers);
        Assert.Equal("auction", blocker.Table);
        Assert.Equal(ChangeDecision.Blocked, blocker.Decision);
        Assert.Equal(1, blocker.DuplicateGroups);
        Assert.Contains("item_guid", blocker.Detail, StringComparison.Ordinal);
        Assert.Equal(2, await UpgradeTestSupport.CountRowsAsync(db, "auction"));

        SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaBootstrapper.EnsureAsync(db, next));
        Assert.Equal(blocker.Detail, ex.Message);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task IndexUnderAnotherName_IsSatisfied_AndSameNameOtherShape_IsAConflict(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        SchemaDefinition next = UpgradeTestSupport.WithIndexRepairStep(CharacterDbContext.Schema, "guild_member");

        await SchemaProbe.DropIndexesAsync(db, "guild_member");
        await SchemaProbe.ExecuteAsync(db,
            $"CREATE UNIQUE INDEX {TestContexts.Quote(db, "dba_member_once")} ON {TestContexts.Quote(db, "guild_member")} ({TestContexts.Quote(db, "CharacterId")})");
        SchemaPlan renamed = await SchemaPlanner.PlanAsync(db, next);
        Assert.Empty(renamed.Blockers);
        Assert.DoesNotContain(renamed.Actions, a => a.Decision == ChangeDecision.Create);
        Assert.Contains(renamed.Actions, a => a.Decision == ChangeDecision.SatisfiedUnderOtherName && a.Table == "guild_member");

        await SchemaProbe.DropIndexesAsync(db, "guild_member");
        await SchemaProbe.ExecuteAsync(db,
            $"CREATE INDEX {TestContexts.Quote(db, "IX_guild_member_CharacterId")} ON {TestContexts.Quote(db, "guild_member")} ({TestContexts.Quote(db, "CharacterId")})");
        SchemaPlan conflict = await SchemaPlanner.PlanAsync(db, next);
        PlannedAction blocker = Assert.Single(conflict.Blockers);
        Assert.Equal(ChangeDecision.Conflict, blocker.Decision);
        Assert.Contains("IX_guild_member_CharacterId", blocker.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExistingTableWithAStrayColumn_IsAModelMismatch_NotAnAdoption(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("world", connection);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaProbe.ExecuteAsync(db, $"ALTER TABLE {TestContexts.Quote(db, "creature_spawn")} ADD COLUMN {TestContexts.Quote(db, "Stray")} INTEGER NULL");
        await UpgradeTestSupport.SetVersionAsync(db, "world", 1);

        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, WorldDbContext.Schema);

        PlannedAction blocker = Assert.Single(plan.Blockers, b => b.Table == "creature_spawn");
        Assert.Equal(ChangeDecision.ModelMismatch, blocker.Decision);
        Assert.Contains("Stray", blocker.Detail, StringComparison.Ordinal);
        SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaProbe.EnsureCurrentAsync("world", connection));
        Assert.Equal(blocker.Detail, ex.Message);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Script_RendersTheDdlOfEveryCreate_AndTheVersionStatements(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersV1Async(_databases, provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);

        SchemaPlan withoutScript = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);
        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema, includeScript: true);

        Assert.All(withoutScript.Actions, a => Assert.Null(a.Script));
        Assert.Equal(withoutScript.CreateCount, plan.CreateCount);
        foreach (PlannedAction create in plan.Actions.Where(a => a.Decision == ChangeDecision.Create))
        {
            string text = string.Concat(Assert.IsAssignableFrom<IReadOnlyList<string>>(create.Script));
            Assert.Contains(TestContexts.Quote(db, create.Name ?? create.Table), text, StringComparison.Ordinal);
        }

        foreach (PlannedStep step in plan.Steps)
        {
            Assert.Contains(TestContexts.Quote(db, CharacterDbContext.Schema.VersionTable), step.VersionStatement, StringComparison.Ordinal);
            Assert.Contains($"= {step.Version}", step.VersionStatement, StringComparison.Ordinal);
        }
    }

    /// <summary>Plan, then apply under a command tap: the DDL the apply issues must be exactly what the plan said. Returns the planned creates.</summary>
    private static async Task<int> AssertPlanMatchesApplyAsync<T>(DatabaseConnectionOptions connection, SchemaDefinition schema) where T : DbContext
    {
        SchemaPlan plan;
        await using (T planning = TestContexts.Create<T>(connection))
        {
            plan = await SchemaPlanner.PlanAsync(planning, schema);
        }

        Assert.False(plan.IsRefused, plan.FirstRefusal);
        var tap = new CommandTap();
        await using (T apply = SchemaProbe.CreateWith<T>(connection, tap))
        {
            await SchemaBootstrapper.EnsureAsync(apply, schema);
        }

        string[] ddl = [.. tap.Commands.Where(CommandTap.IsDdl)];
        int tables = plan.Actions.Count(a => a.Object == ChangeObject.Table && a.Decision == ChangeDecision.Create);
        int indexes = plan.Actions.Count(a => a.Object == ChangeObject.Index && a.Decision == ChangeDecision.Create);
        int columns = plan.Actions.Count(a => a.Object == ChangeObject.Column && a.Decision == ChangeDecision.Create);
        Assert.Equal(tables, UpgradeTestSupport.CountStatements(ddl, UpgradeTestSupport.CreateTable()));
        Assert.Equal(indexes, UpgradeTestSupport.CountStatements(ddl, UpgradeTestSupport.CreateIndex()));
        Assert.Equal(columns, UpgradeTestSupport.CountStatements(ddl, UpgradeTestSupport.AlterTable()));
        Assert.Equal(tables + indexes + columns, ddl.Length);

        string[] createdTables = [.. ddl.Select(c => Regex.Match(c, "^\\s*CREATE\\s+TABLE\\s+[`\"]?(\\w+)", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal)];
        Assert.Equal(
            plan.Actions.Where(a => a.Object == ChangeObject.Table && a.Decision == ChangeDecision.Create).Select(a => a.Table).Order(StringComparer.Ordinal),
            createdTables);

        // Converged: planning again says nothing is left.
        await using (T after = TestContexts.Create<T>(connection))
        {
            SchemaPlan again = await SchemaPlanner.PlanAsync(after, schema);
            Assert.Equal(SchemaState.Current, again.State);
        }

        return tables + indexes + columns;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
