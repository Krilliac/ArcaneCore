using System.Globalization;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;
using Codex = ArcaneCore.Data.Schema.Upgrade.CodexLine;

namespace ArcaneCore.Data.Tests.Upgrade.CodexLine;

/// <summary>
/// Databases the Codex line created (its schema numbers before the 2026-10-07 merge renumbered them,
/// docs/integration/codex-merge-20261007.md) are recognised and migrated once to this build's numbering, at startup and
/// through arcane-db migrate-codex, keeping every row; a database that matches neither line is refused untouched. The
/// Codex databases are built from that commit's own schema (<see cref="CodexLineDatabase"/>); SQLite only, the engine
/// the Codex line ran on.
/// </summary>
public sealed class CodexLineMigrationTests : IDisposable
{
    private static readonly Guid BotA = Guid.Parse("6f2b1c1e-0d55-4c4e-9a43-0b6f7c2d9a01");
    private static readonly Guid BotB = Guid.Parse("6f2b1c1e-0d55-4c4e-9a43-0b6f7c2d9a02");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-codexline-" + Guid.NewGuid().ToString("N"));

    public CodexLineMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing; the temp directory is harmless.
        }
    }

    // --- the line definition ------------------------------------------------------------------

    [Fact]
    public void TheCodexLine_MapsEachStepToTheMergedVersionOfTheMergeRecord_AndTheSameObjects()
    {
        // docs/integration/codex-merge-20261007.md, "Schema renumbering".
        Assert.Equal(
            [(21, (int?)29), (22, 30), (23, 31), (24, 32), (25, 33)],
            Codex.Characters.Steps.Select(s => (s.Version, s.MergedVersion)));
        Assert.Equal(
            [(21, (int?)32), (22, 33), (23, 34), (24, 35), (25, 36), (26, null), (27, 37)],
            Codex.World.Steps.Select(s => (s.Version, s.MergedVersion)));

        foreach ((ForeignLine line, SchemaDefinition schema) in new[]
                 {
                     (Codex.Characters, CharacterDbContext.Schema),
                     (Codex.World, WorldDbContext.Schema),
                 })
        {
            Assert.Contains(line, schema.ForeignLines);
            Assert.Equal(Enumerable.Range(line.DivergedAfter + 1, line.Steps.Count), line.Steps.Select(s => s.Version));
            int[] merged = [.. line.Steps.Where(s => s.MergedVersion is not null).Select(s => s.MergedVersion!.Value)];
            Assert.Equal(merged.Order(), merged);
            foreach (ForeignLineStep step in line.Steps.Where(s => s.MergedVersion is not null))
            {
                // The kept steps are unchanged by the merge: the same tables and columns under the new number.
                SchemaStep mergedStep = schema.Steps.Single(s => s.Version == step.MergedVersion);
                Assert.Equal(
                    step.Objects.Select(o => o.ToString()).Order(StringComparer.Ordinal),
                    ForeignLineDetector.ObjectsOf(mergedStep).Select(o => o.ToString()).Order(StringComparer.Ordinal));
            }

            // Every step of this build in the overlap names objects (the own-line proof needs them).
            foreach (SchemaStep step in schema.Steps.Where(s => s.Version > line.DivergedAfter && s.Version <= line.LastVersion))
            {
                Assert.NotEmpty(ForeignLineDetector.ObjectsOf(step));
            }

            Assert.All(line.DataMoves, m => Assert.InRange(m.AfterMergedVersion, line.DivergedAfter + 1, line.MergedVersionAt(m.ForeignVersion)));
        }

        Assert.Empty(AuthDbContextSchema().ForeignLines);
    }

    // --- startup migration ------------------------------------------------------------------------

    [Fact]
    public async Task CharactersAtTheCodexTip_MigrateAtStartup_KeepEveryRow_AndEndCurrentWithoutDrift()
    {
        string path = await NewCodexCharactersAsync(CodexLineDatabase.CharactersVersion);
        var logger = new CapturingLogger();

        await using (CharacterDbContext db = Characters(path))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema, logger);
        }

        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await VersionAsync(path, "characters"));
        Assert.Contains(logger.Lines, l => l.Contains("created by the Codex (0e07c29b) line (schema version 25)", StringComparison.Ordinal)
                                          && l.Contains("schema version 33", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.StartsWith("Migrated the characters database from Codex (0e07c29b) schema version 25 to schema version 33", StringComparison.Ordinal));
        await AssertSeededCharacterRowsAsync(path);

        // Through this build's stores: the managed bots, ready for the world to load.
        await using (CharacterDbContext db = Characters(path))
        {
            IReadOnlyList<ManagedPlayerbot> bots = await new EfManagedPlayerbotStore(db).LoadAllAsync();
            Assert.Equal([BotA, BotB], bots.Select(b => b.BotId).Order());
            ManagedPlayerbot a = bots.Single(b => b.BotId == BotA);
            Assert.Equal((7, 1, "PBCODEXA", true, 4L), (a.AccountId, a.CharacterId, a.AccountName, a.DesiredEnabled, a.Revision));
        }

        await AssertNoDriftAsync(path, "characters");
    }

    [Fact]
    public async Task WorldAtTheCodexTip_MigratesAtStartup_MovingTheNpcMetadataIntoCreatureTemplate()
    {
        string path = await NewCodexWorldAsync(CodexLineDatabase.WorldVersion);

        await using (WorldDbContext db = World(path))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        Assert.Equal(WorldDbContext.Schema.CurrentVersion, await VersionAsync(path, "world"));
        await using (WorldDbContext db = World(path))
        {
            CreatureTemplateRow trainer = await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync(t => t.Entry == 5113);
            Assert.Equal((4105u, 0u, 1u, 0u, 0u), (trainer.GossipMenuId, trainer.TrainerType, trainer.TrainerClass, trainer.TrainerRace, trainer.TrainerSpell));
            Assert.Equal(1.25f, trainer.DisplayScale2);
            CreatureTemplateRow plain = await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync(t => t.Entry == 6);
            Assert.Equal((0u, 0u, 0u, 0u, 0u), (plain.GossipMenuId, plain.TrainerType, plain.TrainerClass, plain.TrainerRace, plain.TrainerSpell));
        }

        // The Codex table stays (an orphan row included): nothing is deleted.
        Assert.Equal(2L, await CountAsync(path, Codex.NpcMetadataTable));
        Assert.Equal(1L, await CountAsync(path, "reserved_name"));
        Assert.Equal(1L, await CountAsync(path, "creature_ai_text_template"));
        Assert.Equal(2L, await CountAsync(path, "creature_template"));
        await AssertNoDriftAsync(path, "world", foreignTables: [Codex.NpcMetadataTable]);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    public async Task EveryCodexCharactersVersion_IsPlannedAndMigratedToItsMergedNumber(int codexVersion)
    {
        string path = await NewCodexCharactersAsync(codexVersion);
        int merged = codexVersion + 8;

        await using (CharacterDbContext db = Characters(path))
        {
            SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);
            Assert.Equal(SchemaState.Behind, plan.State);
            Assert.Null(plan.FirstRefusal);
            Assert.Equal(codexVersion, plan.DatabaseVersion);
            Assert.Equal(merged, plan.ForeignLine!.MergedVersion);
            Assert.Equal(Enumerable.Range(merged, CharacterDbContext.Schema.CurrentVersion - merged + 1), plan.PendingVersions);
        }

        var progress = new List<int>();
        await using (CharacterDbContext db = Characters(path))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema,
                new SchemaUpgradeOptions { Progress = new SyncProgress(p => progress.Add(p.Version)) });
        }

        Assert.Equal(Enumerable.Range(merged, CharacterDbContext.Schema.CurrentVersion - merged + 1), progress);
        await AssertSeededCharacterRowsAsync(path, codexVersion);
        await AssertNoDriftAsync(path, "characters");
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    public async Task EveryCodexWorldVersion_IsPlannedAndMigratedToItsMergedNumber(int codexVersion)
    {
        string path = await NewCodexWorldAsync(codexVersion);
        int merged = Codex.World.MergedVersionAt(codexVersion);

        await using (WorldDbContext db = World(path))
        {
            SchemaPlan plan = await SchemaPlanner.PlanAsync(db, WorldDbContext.Schema);
            Assert.Equal(SchemaState.Behind, plan.State);
            Assert.Null(plan.FirstRefusal);
            Assert.Equal(merged, plan.ForeignLine!.MergedVersion);
            Assert.Equal(codexVersion >= 26, plan.ForeignLine.DataMoves.Count == 1);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        Assert.Equal(WorldDbContext.Schema.CurrentVersion, await VersionAsync(path, "world"));
        Assert.Equal(codexVersion >= 26 ? 4105L : 0L,
            Convert.ToInt64(await CodexLineDatabase.ScalarAsync(path, "SELECT \"GossipMenuId\" FROM \"creature_template\" WHERE \"Entry\" = 5113"), CultureInfo.InvariantCulture));
        await AssertNoDriftAsync(path, "world", foreignTables: codexVersion >= 26 ? [Codex.NpcMetadataTable] : []);
    }

    [Fact]
    public async Task TheMigrationIsOneShot_ASecondStartFindsACurrentDatabase_AndChangesNothing()
    {
        string path = await NewCodexCharactersAsync(CodexLineDatabase.CharactersVersion);
        await using (CharacterDbContext db = Characters(path))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        string before;
        await using (CharacterDbContext db = Characters(path))
        {
            before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);
            Assert.Equal(ForeignLineDetection.None,
                await ForeignLineDetector.DetectAsync(db, CharacterDbContext.Schema, CharacterDbContext.Schema.CurrentVersion));
            Assert.Null(await SchemaBootstrapper.MigrateForeignLineAsync(db, CharacterDbContext.Schema, new SchemaUpgradeOptions()));
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);
            Assert.Equal(SchemaState.Current, plan.State);
            Assert.Null(plan.ForeignLine);
        }

        await using (CharacterDbContext db = Characters(path))
        {
            Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema));
        }

        await AssertSeededCharacterRowsAsync(path);
    }

    [Fact]
    public async Task AMigrationInterruptedAfterSomeOfThisBuildsSteps_ConvergesOnTheNextStart()
    {
        // MariaDB commits DDL implicitly: a migration that died midway leaves some of this build's tables beside the
        // Codex ones with the Codex version row. Reproduced here with this build's own DDL for two of the steps.
        string path = await NewCodexCharactersAsync(CodexLineDatabase.CharactersVersion);
        string reference = Path.Combine(_directory, "reference.db");
        await using (CharacterDbContext db = Characters(reference))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        string taxi = (string)(await CodexLineDatabase.ScalarAsync(reference, "SELECT sql FROM sqlite_master WHERE name = 'character_taxi_flight'"))!;
        await CodexLineDatabase.ExecuteAsync(path, taxi);
        await CodexLineDatabase.ExecuteAsync(path, "ALTER TABLE \"characters\" ADD COLUMN \"bank_bag_slots\" INTEGER NOT NULL DEFAULT 0");

        await using (CharacterDbContext db = Characters(path))
        {
            SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);
            Assert.Equal(33, plan.ForeignLine!.MergedVersion);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await VersionAsync(path, "characters"));
        await AssertSeededCharacterRowsAsync(path);
        await AssertNoDriftAsync(path, "characters");
    }

    [Fact]
    public async Task ThisBuildsOwnDatabaseInTheOverlappingNumbers_IsNotTakenForTheCodexLine()
    {
        // A database of this build recording 25 (its own game_event_status step) holds every object of its steps
        // 21-25; that it also holds tables of later steps (here: all, from a current create) does not make it Codex.
        string path = Path.Combine(_directory, "own.db");
        await using (CharacterDbContext db = Characters(path))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        await CodexLineDatabase.ExecuteAsync(path, "UPDATE \"characters_schema\" SET \"Version\" = 25");
        await using (CharacterDbContext db = Characters(path))
        {
            Assert.Equal(ForeignLineDetection.None, await ForeignLineDetector.DetectAsync(db, CharacterDbContext.Schema, 25));
            SchemaPlan plan = await SchemaPlanner.PlanAsync(db, CharacterDbContext.Schema);
            Assert.Null(plan.ForeignLine);
            Assert.Equal(Enumerable.Range(26, CharacterDbContext.Schema.CurrentVersion - 25), plan.PendingVersions);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await VersionAsync(path, "characters"));
    }

    // --- refusals -----------------------------------------------------------------------------------

    public static TheoryData<string, int, string, string> Damaged() => new()
    {
        // A recorded Codex step whose table is gone.
        { "characters", 25, "DROP TABLE \"character_pet_cooldown\"", "step 23 (PetCooldownDataModule) is recorded but absent" },
        // A Codex step that is only half there.
        { "characters", 24, "ALTER TABLE \"character_pet\" DROP COLUMN \"RenameAllowed\"", "step 24 (PetNamingDataModule) is incomplete, missing character_pet.RenameAllowed" },
        // A Codex table the recorded version does not account for.
        { "characters", 23, "CREATE TABLE \"managed_playerbot\" (\"BotId\" TEXT NOT NULL PRIMARY KEY)", "step 25 (ManagedPlayerbotDataModule) is present but not recorded" },
        // Neither line's tables at all.
        { "characters", 21, "DROP TABLE \"character_pet\"", "holds neither line's tables" },
        { "world", 26, "DROP TABLE \"reserved_name\"", "step 21 (ReservedNameWorldDataModule) is recorded but absent" },
    };

    [Theory]
    [MemberData(nameof(Damaged))]
    public async Task ADatabaseMatchingNeitherLine_IsRefusedByThePlanTheStartupAndTheTool_AndLeftUntouched(
        string component, int codexVersion, string damage, string expected)
    {
        string path = component == "characters" ? await NewCodexCharactersAsync(codexVersion) : await NewCodexWorldAsync(codexVersion);
        await CodexLineDatabase.ExecuteAsync(path, damage);
        SqliteConnection.ClearAllPools();
        string fingerprint = UpgradeTestSupport.FileFingerprint(path);
        SchemaDefinition schema = component == "characters" ? CharacterDbContext.Schema : WorldDbContext.Schema;

        await using (DbContext db = component == "characters" ? Characters(path) : (DbContext)World(path))
        {
            SchemaPlan plan = await SchemaPlanner.PlanAsync(db, schema);
            Assert.Equal(SchemaState.Unknown, plan.State);
            Assert.Contains(expected, plan.FirstRefusal, StringComparison.Ordinal);
            Assert.Null(plan.ForeignLine);

            SchemaMismatchException startup = await Assert.ThrowsAnyAsync<SchemaMismatchException>(() => SchemaBootstrapper.EnsureAsync(db, schema));
            Assert.Contains(expected, startup.Message, StringComparison.Ordinal);
            SchemaMismatchException tool = await Assert.ThrowsAnyAsync<SchemaMismatchException>(
                () => SchemaBootstrapper.MigrateForeignLineAsync(db, schema, new SchemaUpgradeOptions()));
            Assert.Contains("nothing was changed", tool.Message, StringComparison.OrdinalIgnoreCase);
        }

        (int code, _, string error) = await UpgradeTestSupport.RunCliAsync(Single(path), "migrate-codex", "--component", component, "--apply", "--confirm-backup");
        Assert.Equal(DbUpgradeExitCodes.Refused, code);
        Assert.Contains(expected, error, StringComparison.Ordinal);
        Assert.Equal(codexVersion, await VersionAsync(path, component));
        SqliteConnection.ClearAllPools();
        Assert.Equal(fingerprint, UpgradeTestSupport.FileFingerprint(path));
    }

    // --- arcane-db migrate-codex ---------------------------------------------------------------------

    [Fact]
    public async Task MigrateCodex_DryRunReportsAndWritesNothing_ApplyMigratesOnly_ThenUpgradeFinishes()
    {
        string auth = Path.Combine(_directory, "auth.db");
        await CodexLineDatabase.CreateAsync(auth, "auth");
        string characters = await NewCodexCharactersAsync(CodexLineDatabase.CharactersVersion);
        string world = await NewCodexWorldAsync(CodexLineDatabase.WorldVersion);
        var options = new DatabaseOptions
        {
            Provider = DatabaseProvider.Sqlite,
            Auth = new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = CodexLineDatabase.ConnectionString(auth) },
            Characters = new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = CodexLineDatabase.ConnectionString(characters) },
            World = new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = CodexLineDatabase.ConnectionString(world) },
        };
        SqliteConnection.ClearAllPools();
        string[] fingerprints = [.. new[] { auth, characters, world }.Select(UpgradeTestSupport.FileFingerprint)];

        (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(options, "migrate-codex");
        Assert.Equal(DbUpgradeExitCodes.UpgradePending, code);
        Assert.Equal(string.Empty, error);
        Assert.Contains("auth: not a Codex-line database", output, StringComparison.Ordinal);
        Assert.Contains("characters: Codex-line database: migrate the characters database from the Codex (0e07c29b) numbering (schema version 25) to this build's numbering (schema version 33)", output, StringComparison.Ordinal);
        Assert.Contains("found Codex (0e07c29b) step 25 (ManagedPlayerbotDataModule) -> version 33", output, StringComparison.Ordinal);
        Assert.Contains("found Codex (0e07c29b) step 26 (NpcTemplateServiceMetadataModule) -> dropped by the merge", output, StringComparison.Ordinal);
        Assert.Contains("(1 rows)", output, StringComparison.Ordinal);
        Assert.Contains("dry run, nothing was changed", output, StringComparison.Ordinal);
        SqliteConnection.ClearAllPools();
        Assert.Equal(fingerprints, new[] { auth, characters, world }.Select(UpgradeTestSupport.FileFingerprint));

        // plan and status show the same thing, read-only.
        (code, output, _) = await UpgradeTestSupport.RunCliAsync(options, "status");
        Assert.Equal(DbUpgradeExitCodes.UpgradePending, code);
        Assert.Matches(@"characters\s+state Behind, database version 25 \(Codex \(0e07c29b\) numbering\), code version \d+, pending: 33, ", output);

        (code, _, error) = await UpgradeTestSupport.RunCliAsync(options, "migrate-codex", "--apply");
        Assert.Equal(DbUpgradeExitCodes.BackupNotConfirmed, code);
        Assert.Contains("no backup was confirmed", error, StringComparison.Ordinal);
        Assert.Equal(25, await VersionAsync(characters, "characters"));

        string backups = Path.Combine(_directory, "backups");
        (code, output, error) = await UpgradeTestSupport.RunCliAsync(options, "migrate-codex", "--apply", "--backup-dir", backups);
        Assert.True(code == DbUpgradeExitCodes.Ok, output + error);
        Assert.Contains("characters: migrated from Codex (0e07c29b) schema version 25 to schema version 33", output, StringComparison.Ordinal);
        Assert.Contains("world: migrated from Codex (0e07c29b) schema version 27 to schema version 37", output, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(backups).Length);
        Assert.Equal(33, await VersionAsync(characters, "characters")); // only the migration: the step after it is the ordinary upgrade
        Assert.Equal(37, await VersionAsync(world, "world"));
        Assert.Equal(CodexLineDatabase.AuthVersion, await VersionAsync(auth, "auth"));

        (code, output, _) = await UpgradeTestSupport.RunCliAsync(options, "migrate-codex");
        Assert.Equal(DbUpgradeExitCodes.Ok, code);
        Assert.Contains("nothing to migrate", output, StringComparison.Ordinal);

        (code, output, error) = await UpgradeTestSupport.RunCliAsync(options, "upgrade", "--confirm-backup");
        Assert.True(code == DbUpgradeExitCodes.Ok, output + error);
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await VersionAsync(characters, "characters"));
        await AssertSeededCharacterRowsAsync(characters);
    }

    // --- helpers ------------------------------------------------------------------------------------

    private static SchemaDefinition AuthDbContextSchema() => ArcaneCore.Data.Auth.AuthDbContext.Schema;

    private static DatabaseConnectionOptions Single(string path)
        => new() { Provider = DatabaseProvider.Sqlite, ConnectionString = CodexLineDatabase.ConnectionString(path) };

    private static CharacterDbContext Characters(string path) => TestContexts.Create<CharacterDbContext>(Single(path));

    private static WorldDbContext World(string path) => TestContexts.Create<WorldDbContext>(Single(path));

    private static async Task<int> VersionAsync(string path, string component)
        => Convert.ToInt32(await CodexLineDatabase.ScalarAsync(path, $"SELECT \"Version\" FROM \"{component}_schema\" WHERE \"Id\" = 1"), CultureInfo.InvariantCulture);

    private static async Task<long> CountAsync(string path, string table)
        => Convert.ToInt64(await CodexLineDatabase.ScalarAsync(path, $"SELECT COUNT(*) FROM \"{table}\""), CultureInfo.InvariantCulture);

    /// <summary>A Codex characters database at <paramref name="version"/> with a character, items, spells, a quest, cooldowns and (when the version has them) a pet, item-cooldown owners and two managed bots.</summary>
    private async Task<string> NewCodexCharactersAsync(int version)
    {
        string path = Path.Combine(_directory, $"codex-characters-{version}-{Guid.NewGuid():N}.db");
        await CodexLineDatabase.CreateAsync(path, "characters", version);
        await CodexLineDatabase.InsertAsync(path, "characters", ("Id", 1), ("AccountId", 7), ("Name", "Codexbota"), ("Race", 1), ("Class", 1),
            ("Level", 12), ("MapId", 0), ("ZoneId", 12), ("X", -8949.95), ("Y", -132.49), ("Z", 83.53), ("Money", 4242));
        await CodexLineDatabase.InsertAsync(path, "characters", ("Id", 2), ("AccountId", 8), ("Name", "Codexbotb"), ("Race", 2), ("Class", 3), ("Level", 5));
        await CodexLineDatabase.InsertAsync(path, "item_instance", ("guid", 100), ("owner_guid", 1), ("item_id", 25), ("count", 1), ("durability", 20));
        await CodexLineDatabase.InsertAsync(path, "character_inventory", ("item_guid", 100), ("guid", 1), ("bag", 0), ("slot", 15), ("item_id", 25));
        await CodexLineDatabase.InsertAsync(path, "character_spell", ("CharacterId", 1), ("Spell", 78));
        await CodexLineDatabase.InsertAsync(path, "character_queststatus", ("guid", 1), ("quest", 783), ("status", 3), ("mob_count1", 4));
        await CodexLineDatabase.InsertAsync(path, "character_spell_cooldown", ("CharacterId", 1), ("Kind", 0), ("Id", 2457), ("EndsAtUnixMs", 1_900_000_000_000L));
        if (version >= 21)
        {
            await CodexLineDatabase.InsertAsync(path, "character_pet", ("CharacterId", 2), ("PetNumber", 1), ("Entry", 299), ("Level", 5), ("ActionBarJson", "[]"), ("SpellsJson", "[]"), ("IsCurrent", 1));
        }

        if (version >= 22)
        {
            await CodexLineDatabase.InsertAsync(path, "character_item_cooldown_owner", ("CharacterId", 1), ("SpellId", 439), ("ItemId", 118), ("Category", 4), ("SpellEndsAtUnixMs", 1_900_000_000_000L));
        }

        if (version >= 23)
        {
            await CodexLineDatabase.InsertAsync(path, "character_pet_cooldown", ("CharacterId", 2), ("PetNumber", 1), ("Kind", 0), ("SpellId", 17253), ("EndsAtUnixMs", 1_900_000_000_000L));
        }

        if (version >= 24)
        {
            await CodexLineDatabase.ExecuteAsync(path, "UPDATE \"character_pet\" SET \"Name\" = 'Fang', \"NameTimestamp\" = 77, \"RenameAllowed\" = 0");
        }

        if (version >= 25)
        {
            await CodexLineDatabase.InsertAsync(path, "managed_playerbot", ("BotId", BotA.ToString().ToUpperInvariant()), ("AccountId", 7), ("CharacterId", 1),
                ("AccountName", "PBCODEXA"), ("DesiredEnabled", 1), ("State", (int)ManagedPlayerbotState.Stopped), ("Revision", 4), ("CreatedUnix", 1_780_000_000L), ("UpdatedUnix", 1_780_000_100L));
            await CodexLineDatabase.InsertAsync(path, "managed_playerbot", ("BotId", BotB.ToString().ToUpperInvariant()), ("AccountId", 8), ("CharacterId", 2),
                ("AccountName", "PBCODEXB"), ("DesiredEnabled", 0), ("State", (int)ManagedPlayerbotState.Stopped), ("CreatedUnix", 1_780_000_000L), ("UpdatedUnix", 1_780_000_000L));
        }

        return path;
    }

    private async Task<string> NewCodexWorldAsync(int version)
    {
        string path = Path.Combine(_directory, $"codex-world-{version}-{Guid.NewGuid():N}.db");
        await CodexLineDatabase.CreateAsync(path, "world", version);
        (string, object)[] scale = version >= 23 ? [("DisplayScale2", 1.25)] : [];
        await CodexLineDatabase.InsertAsync(path, "creature_template", [("Entry", 5113), ("Name", "Kelv Sternhammer"), ("NpcFlags", 19), .. scale]);
        await CodexLineDatabase.InsertAsync(path, "creature_template", ("Entry", 6), ("Name", "Kobold Vermin"));
        if (version >= 21)
        {
            await CodexLineDatabase.InsertAsync(path, "reserved_name", ("name", "Thrall"));
        }

        if (version >= 26)
        {
            await CodexLineDatabase.InsertAsync(path, Codex.NpcMetadataTable, ("entry", 5113), ("gossip_menu_id", 4105), ("trainer_class", 1));
            await CodexLineDatabase.InsertAsync(path, Codex.NpcMetadataTable, ("entry", 99999), ("gossip_menu_id", 1)); // no template: kept, not moved
        }

        if (version >= 27)
        {
            await CodexLineDatabase.InsertAsync(path, "creature_ai_text_template", ("Id", 1), ("TargetId", 2), ("Chance", 50));
        }

        return path;
    }

    /// <summary>The rows <see cref="NewCodexCharactersAsync"/> wrote, value for value.</summary>
    private static async Task AssertSeededCharacterRowsAsync(string path, int version = CodexLineDatabase.CharactersVersion)
    {
        Assert.Equal("Codexbota|7|12|4242", await CodexLineDatabase.ScalarAsync(path,
            "SELECT \"Name\" || '|' || \"AccountId\" || '|' || \"Level\" || '|' || \"Money\" FROM \"characters\" WHERE \"Id\" = 1"));
        Assert.Equal(2L, await CountAsync(path, "characters"));
        Assert.Equal("1|0|15|25", await CodexLineDatabase.ScalarAsync(path,
            "SELECT i.\"owner_guid\" || '|' || c.\"bag\" || '|' || c.\"slot\" || '|' || c.\"item_id\" FROM \"item_instance\" i JOIN \"character_inventory\" c ON c.\"item_guid\" = i.\"guid\""));
        Assert.Equal(1L, await CountAsync(path, "character_spell"));
        Assert.Equal("3|4", await CodexLineDatabase.ScalarAsync(path, "SELECT \"status\" || '|' || \"mob_count1\" FROM \"character_queststatus\" WHERE \"quest\" = 783"));
        Assert.Equal(1L, await CountAsync(path, "character_spell_cooldown"));
        Assert.Equal(version >= 21 ? 1L : 0L, await CountAsync(path, "character_pet"));
        Assert.Equal(version >= 22 ? 1L : 0L, await CountAsync(path, "character_item_cooldown_owner"));
        Assert.Equal(version >= 23 ? 1L : 0L, await CountAsync(path, "character_pet_cooldown"));
        Assert.Equal(version >= 24 ? "Fang|77|0" : "|0|1", await CodexLineDatabase.ScalarAsync(path,
            "SELECT \"Name\" || '|' || \"NameTimestamp\" || '|' || \"RenameAllowed\" FROM \"character_pet\" WHERE \"CharacterId\" = 2") ?? "|0|1");
        Assert.Equal(version >= 25 ? 2L : 0L, await CountAsync(path, "managed_playerbot"));
    }

    private static async Task AssertNoDriftAsync(string path, string component, IReadOnlyList<string>? foreignTables = null)
    {
        await using DbContext db = component == "characters" ? Characters(path) : (DbContext)World(path);
        SchemaDefinition schema = component == "characters" ? CharacterDbContext.Schema : WorldDbContext.Schema;
        string[] known = [.. SchemaProbe.ModelTables(db), schema.VersionTable];
        DriftReport report = await SchemaDriftChecker.CheckAsync(db, schema, known);
        Assert.True(report.IsClean, string.Join(Environment.NewLine, report.Findings.Select(f => f.Detail)));
        Assert.True(report.TablesExamined > 20, $"only {report.TablesExamined} tables examined");
        Assert.Equal(foreignTables ?? [], report.Warnings.Where(w => w.Kind == DriftKind.ForeignTable).Select(w => w.Table!).Order(StringComparer.Ordinal));
    }

    private sealed class SyncProgress(Action<SchemaStepProgress> report) : IProgress<SchemaStepProgress>
    {
        public void Report(SchemaStepProgress value) => report(value);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}
