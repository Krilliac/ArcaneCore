using System.Globalization;
using ArcaneCore.Data.Content.Chr;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>A failure with the exit code it maps to.</summary>
internal sealed class CliException(int exitCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// <c>arcane-content-importer</c>: reads cmangos classic-db / vmangos world dumps (plain or gzip)
/// and client DBCs into the world database, with the checks of <see cref="ContentScanner"/>
/// first. The logic lives here, in the data library, so the tests drive it in-process; the tool
/// project is a one-line host. Commands: <c>plan</c> (read-only report), <c>import</c>,
/// <c>import-dbc</c> and <c>verify</c>. See <see cref="ExitCodes"/> for the exit codes.
/// </summary>
public static class ContentImporterCli
{
    /// <summary>Environment variable read when no connection string is given (keeps the password off the command line).</summary>
    public const string ConnectionVariable = "ARCANECORE_CONTENT_CONNECTION";

    public const string Usage = """
        usage: arcane-content-importer <command> [options]

        commands:
          plan <dump>...        read the dump(s) and print, per table, the dialect, row and key
                                counts and which columns an importer reads; writes no database
          import <dump>...      import the creature, game object, loot, item, quest, kill-reputation and
                                new-character (start position, starting spell, teleport target) and
                                location (portal, GM teleport) tables
                                (and the starting outfit, playercreateinfo_item)
          import-dbc <dir>      import the five spell DBCs from a client DBFilesClient directory
          verify                count the imported tables and check references

        a <dump> is a .sql or .sql.gz file (the gzip magic number decides, not the name); several
        dumps are read in order as one, later rows replacing earlier ones with the same key.

        options:
          --dialect auto|cmangos|vmangos   expected source layout (default auto: detected per table)
          --database <file>                SQLite database file (outside the repository)
          --provider <sqlite|mariadb|mysql|postgresql> --connection-string <cs>
                                           any other engine; or set ARCANECORE_CONTENT_CONNECTION
          --dbc-dir <DBFilesClient>        import: also read Lock.dbc (game object locks)
          --quest-xp derived|none          import: a source without quest_template.RewXP (cmangos) gets
                                           RewXP derived from RewMoneyMaxLevel like cmangos' Quest::XPValue
                                           (default derived), or none (RewXP 0: quests give no XP)
          --level-stats-file <file>        import: write the race/class/level base stats file that
                                           Progression:LevelStatsPath reads (outside the repository)
          --replace                        import: empty the importers' tables first
          --dry-run                        import: read and count everything, write nothing
          --report <file>                  write the JSON report (outside the repository)
          --verbose                        plan/import: also list empty tables

        exit codes: 0 ok, 1 unreadable input, 2 usage, 3 wrong source schema or dialect,
        4 database error (nothing changed), 5 path inside a git work tree and not ignored,
        6 verify found missing references
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0)
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        if (args.Any(a => a is "-h" or "--help" or "help"))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCodes.Ok;
        }

        try
        {
            CliArguments arguments = CliArguments.Parse(args);
            return arguments.Command switch
            {
                "plan" => await PlanAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "import" => await ImportAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "import-dbc" => await ImportDbcAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "verify" => await VerifyAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                _ => throw new UsageException($"unknown command '{arguments.Command}'"),
            };
        }
        catch (UsageException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            await error.WriteLineAsync("run with --help for usage").ConfigureAwait(false);
            return ExitCodes.Usage;
        }
        catch (ImportSchemaException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Schema;
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("error: cancelled; nothing was changed").ConfigureAwait(false);
            return ExitCodes.Io;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            await error.WriteLineAsync($"error: cannot read the input: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Io;
        }
    }

    // --- plan --------------------------------------------------------------------------------

    private static Task<int> PlanAsync(CliArguments a, TextWriter o, CancellationToken ct)
    {
        string dialect = ParseDialect(a);
        RequireInputs(a);
        string? reportPath = a.Value("--report");
        GuardPath(reportPath);

        (IReadOnlyList<DumpInput> inputs, IReadOnlyList<SourceFileInfo> files) = OpenInputs(a.Positional);
        ct.ThrowIfCancellationRequested();
        ScanResult scan = ContentScanner.Scan(inputs);
        CheckDialect(scan, dialect);

        o.WriteLine("plan: reads the dumps and writes nothing to any database");
        PrintScan(o, files, scan, a.Flag("--verbose"));
        List<string> warnings = MixedDialectWarning(scan);
        PrintWarnings(o, warnings);
        WriteReport(reportPath, ContentImportReport.Create("plan", files, scan, warnings, dryRun: false));
        return Task.FromResult(ExitCodes.Ok);
    }

    // --- import ------------------------------------------------------------------------------

    private static async Task<int> ImportAsync(CliArguments a, TextWriter o, CancellationToken ct)
    {
        string dialect = ParseDialect(a);
        RequireInputs(a);
        bool dryRun = a.Flag("--dry-run");
        bool replace = a.Flag("--replace");
        Target? target = dryRun && a.Value("--database") is null && a.Value("--provider") is null ? null : ResolveTarget(a);
        string? reportPath = a.Value("--report");
        GuardPath(reportPath);
        string? levelStatsPath = a.Value("--level-stats-file");
        GuardPath(levelStatsPath);
        if (!dryRun && target?.FilePath is not null)
        {
            GuardPath(target.FilePath);
        }

        if (!dryRun && target is null)
        {
            throw new UsageException("import needs a target: --database <file>, or --provider with --connection-string");
        }

        (IReadOnlyList<DumpInput> inputs, IReadOnlyList<SourceFileInfo> files) = OpenInputs(a.Positional);
        ScanResult scan = ContentScanner.Scan(inputs);
        CheckDialect(scan, dialect);

        o.WriteLine(dryRun ? "import (dry run): reads and counts everything, writes nothing" : "import");
        PrintScan(o, files, scan, a.Flag("--verbose"));
        List<string> warnings = MixedDialectWarning(scan);

        var creatures = new CreatureDumpImporter();
        var objects = new GameObjectLootDumpImporter();
        var itemsAndQuests = new ItemQuestDumpImporter { QuestXp = ParseQuestXp(a) };
        var onKill = new OnKillReputationDumpImporter();
        var playerCreate = new PlayerCreateDumpImporter();
        var startActions = new PlayerCreateActionDumpImporter();
        var locations = new LocationDumpImporter();
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            creatures.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            objects.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            itemsAndQuests.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            onKill.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            playerCreate.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            startActions.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            locations.Read(reader);
        }

        string? dbcDirectory = a.Value("--dbc-dir");
        if (dbcDirectory is not null)
        {
            string lockPath = Path.Combine(dbcDirectory, "Lock.dbc");
            if (!File.Exists(lockPath))
            {
                throw new CliException(ExitCodes.Io, $"Lock.dbc not found in '{dbcDirectory}'");
            }

            try
            {
                objects.ReadLocks(DbcFile.Load(lockPath));
            }
            catch (InvalidDataException ex)
            {
                throw new CliException(ExitCodes.Io, $"Lock.dbc: {ex.Message}", ex);
            }
        }
        else
        {
            warnings.Add(replace
                ? "no --dbc-dir: Lock.dbc was not read, and --replace empties lock_template, so no game object lock data will remain"
                : "no --dbc-dir: Lock.dbc was not read, so lock_template is not imported (locked chests and doors will not open correctly)");
        }

        CreatureImportReport creatureReport = creatures.BuildReport();
        GameObjectLootImportReport objectReport = objects.BuildReport();
        ItemQuestImportReport itemQuestReport = itemsAndQuests.BuildReport();
        ReputationOnKillImportReport onKillReport = onKill.BuildReport();
        PlayerCreateImportReport playerReport = playerCreate.BuildReport();
        PlayerCreateActionImportReport startActionReport = startActions.BuildReport();
        LocationImportReport locationReport = locations.BuildReport();
        if (!dryRun)
        {
            o.WriteLine($"target: {target!.Describe}");
            try
            {
                await using WorldDbContext db = OpenWorld(target);
                await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema, cancellationToken: ct).ConfigureAwait(false);
                await ImportTransaction.RunAsync(db, async token =>
                {
                    creatureReport = await creatures.WriteAsync(db, replace, token).ConfigureAwait(false);
                    objectReport = await objects.WriteAsync(db, replace, token).ConfigureAwait(false);
                    itemQuestReport = await itemsAndQuests.WriteAsync(db, replace, token).ConfigureAwait(false);
                    onKillReport = await onKill.WriteAsync(db, replace, token).ConfigureAwait(false);
                    playerReport = await playerCreate.WriteAsync(db, replace, token).ConfigureAwait(false);
                    startActionReport = await startActions.WriteAsync(db, replace, token).ConfigureAwait(false);
                    locationReport = await locations.WriteAsync(db, replace, token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or CliException))
            {
                throw DatabaseError(ex, target);
            }
        }

        warnings.AddRange(creatureReport.Warnings);
        warnings.AddRange(objectReport.Warnings);
        warnings.AddRange(itemQuestReport.Warnings);
        warnings.AddRange(onKillReport.Warnings);
        warnings.AddRange(playerReport.Warnings);
        warnings.AddRange(startActionReport.Warnings);
        warnings.AddRange(locationReport.Warnings);
        if (itemQuestReport.DerivedQuestXp > 0)
        {
            warnings.Add(
                $"quest_template.RewXP was derived from RewMoneyMaxLevel for {itemQuestReport.DerivedQuestXp} quest(s) " +
                "(the source has no RewXP; cmangos Quest::XPValue); XP reduced for grey quests can differ from cmangos by 1");
        }

        (Dictionary<string, long> imported, Dictionary<string, long> skipped) = Counts(creatureReport, objectReport, itemQuestReport, onKillReport, playerReport, startActionReport, locationReport);
        o.WriteLine(dryRun ? "would import:" : "imported:");
        foreach ((string table, long count) in imported)
        {
            o.WriteLine($"  {table}  {count.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach ((string table, long count) in skipped.Where(s => s.Value > 0))
        {
            o.WriteLine($"  {table}  {count.ToString(CultureInfo.InvariantCulture)} source row(s) skipped");
        }

        if (levelStatsPath is not null)
        {
            if (dryRun)
            {
                o.WriteLine($"level stats file: would write {playerReport.LevelStatRows} row(s) to the --level-stats-file path");
            }
            else
            {
                WriteLevelStatsFile(levelStatsPath, playerCreate);
                o.WriteLine($"level stats file: wrote {playerReport.LevelStatRows} row(s) to {Path.GetFullPath(levelStatsPath)}; set Progression:LevelStatsPath to it");
            }
        }
        else if (playerReport.LevelStatRows > 0)
        {
            warnings.Add($"{playerReport.LevelStatRows} level-stats row(s) were read but no --level-stats-file was given, so level-ups will not change base health, mana or stats");
        }

        PrintWarnings(o, warnings);
        WriteReport(reportPath, ContentImportReport.Create("import", files, scan, warnings, dryRun) with { Imported = imported, Skipped = skipped });
        return ExitCodes.Ok;
    }

    private static (Dictionary<string, long> Imported, Dictionary<string, long> Skipped) Counts(
        CreatureImportReport creatures, GameObjectLootImportReport objects, ItemQuestImportReport itemsAndQuests, ReputationOnKillImportReport onKill,
        PlayerCreateImportReport playerCreate, PlayerCreateActionImportReport startActions, LocationImportReport locations)
    {
        var imported = new Dictionary<string, long>
        {
            ["creature_template"] = creatures.Templates,
            ["creature_spawn"] = creatures.Spawns,
            ["creature_movement"] = creatures.Waypoints,
            ["creature_model_info"] = creatures.Models,
            ["creature_addon"] = creatures.Addons,
            ["creature_ai_scripts"] = creatures.AiEvents,
            ["creature_ai_texts"] = creatures.AiTexts,
            ["gameobject_template"] = objects.Templates,
            ["gameobject_spawn"] = objects.Spawns,
            ["gameobject_questrelation"] = objects.QuestStarters,
            ["gameobject_involvedrelation"] = objects.QuestEnders,
            ["lock_template"] = objects.Locks,
            ["loot_template_rows"] = objects.LootRows,
            ["creature_loot_info"] = objects.CreatureLootInfos,
            ["item_template"] = itemsAndQuests.Items,
            ["quest_template"] = itemsAndQuests.Quests,
            ["creature_questrelation"] = itemsAndQuests.QuestStarters,
            ["creature_involvedrelation"] = itemsAndQuests.QuestEnders,
            ["playercreateinfo_item"] = itemsAndQuests.StartingItems,
            ["creature_onkill_reputation"] = onKill.Entries,
            ["player_create_info"] = playerCreate.StartPositions,
            ["playercreateinfo_spell"] = playerCreate.CreateSpells,
            ["playercreateinfo_action"] = startActions.Rows,
            ["spell_target_position"] = playerCreate.SpellTargetPositions,
            ["level_stats_rows"] = playerCreate.LevelStatRows,
            ["areatrigger_teleport"] = locations.Portals,
            ["game_tele"] = locations.Teleports,
        };
        var skipped = new Dictionary<string, long>
        {
            ["creature_spawn"] = creatures.SkippedSpawns,
            ["gameobject_and_loot_rows"] = objects.SkippedRows,
            ["item_and_quest_rows"] = itemsAndQuests.SkippedRows,
            ["creature_onkill_reputation"] = onKill.SkippedRows,
            ["player_create_rows"] = playerCreate.SkippedRows,
            ["playercreateinfo_action_rows"] = startActions.SkippedRows,
            ["areatrigger_teleport_rows"] = locations.SkippedRows,
        };
        return (imported, skipped);
    }

    // --- import-dbc --------------------------------------------------------------------------------

    private static async Task<int> ImportDbcAsync(CliArguments a, TextWriter o, CancellationToken ct)
    {
        if (a.Positional.Count != 1)
        {
            throw new UsageException("import-dbc takes exactly one argument: the DBFilesClient directory");
        }

        Target target = ResolveTarget(a);
        string directory = a.Positional[0];
        if (!Directory.Exists(directory))
        {
            throw new CliException(ExitCodes.Io, $"directory '{directory}' does not exist");
        }

        GuardPath(target.FilePath);
        SpellDbcContent content;
        try
        {
            content = SpellDbcImporter.ReadDirectory(directory);
        }
        catch (FileNotFoundException ex)
        {
            throw new CliException(ExitCodes.Io, $"cannot read the DBCs: {ex.Message}", ex);
        }

        o.WriteLine($"target: {target.Describe}");
        try
        {
            await using WorldDbContext db = OpenWorld(target);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema, cancellationToken: ct).ConfigureAwait(false);
            await new EfSpellContentStore(db).ReplaceDbcTablesAsync(content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CliException))
        {
            throw DatabaseError(ex, target);
        }

        o.WriteLine(
            $"Imported {content.Spells.Count} spells, {content.CastTimes.Count} cast times, {content.Durations.Count} durations, " +
            $"{content.Ranges.Count} ranges and {content.Radii.Count} radii.");
        return ExitCodes.Ok;
    }

    // --- verify -------------------------------------------------------------------------------------

    private static async Task<int> VerifyAsync(CliArguments a, TextWriter o, CancellationToken ct)
    {
        if (a.Positional.Count != 0)
        {
            throw new UsageException("verify takes no arguments");
        }

        Target target = ResolveTarget(a);
        if (target.FilePath is not null && !File.Exists(target.FilePath))
        {
            throw new CliException(ExitCodes.Io, $"database '{target.FilePath}' does not exist");
        }

        o.WriteLine($"target: {target.Describe}");
        var problems = new List<string>();
        try
        {
            await using WorldDbContext db = OpenWorld(target);
            var counts = new (string Table, int Count)[]
            {
                ("creature_template", await db.Set<CreatureTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_spawn", await db.Set<CreatureSpawnRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_movement", await db.Set<CreatureMovementRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_model_info", await db.Set<CreatureModelInfoRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_addon", await db.Set<CreatureAddonRow>().CountAsync(ct).ConfigureAwait(false)),
                ("gameobject_template", await db.Set<GameObjectTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("gameobject_spawn", await db.Set<GameObjectSpawnRow>().CountAsync(ct).ConfigureAwait(false)),
                ("lock_template", await db.Set<LockTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_loot_template", await db.Set<CreatureLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("gameobject_loot_template", await db.Set<GameObjectLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("item_loot_template", await db.Set<ItemLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("skinning_loot_template", await db.Set<SkinningLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("reference_loot_template", await db.Set<ReferenceLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_loot_info", await db.Set<CreatureLootInfoRow>().CountAsync(ct).ConfigureAwait(false)),
                ("item_template", await db.Set<ItemTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("quest_template", await db.Set<QuestTemplate>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_questrelation", await db.Set<CreatureQuestStarterRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_involvedrelation", await db.Set<CreatureQuestEnderRow>().CountAsync(ct).ConfigureAwait(false)),
                ("playercreateinfo_item", await db.Set<PlayerCreateInfoItemRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_onkill_reputation", await db.Set<CreatureOnKillReputationRow>().CountAsync(ct).ConfigureAwait(false)),
                ("player_create_info", await db.PlayerCreateInfo.CountAsync(ct).ConfigureAwait(false)),
                ("playercreateinfo_spell", await db.Set<PlayerCreateSpellRow>().CountAsync(ct).ConfigureAwait(false)),
                ("playercreateinfo_action", await db.Set<PlayerCreateActionRow>().CountAsync(ct).ConfigureAwait(false)),
                ("spell_target_position", await db.Set<SpellTargetPositionRow>().CountAsync(ct).ConfigureAwait(false)),
                ("areatrigger_teleport", await db.Set<AreaTriggerTeleportRow>().CountAsync(ct).ConfigureAwait(false)),
                ("game_tele", await db.Set<GameTeleRow>().CountAsync(ct).ConfigureAwait(false)),
                ("spell_template", await db.Set<SpellTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
            };
            foreach ((string table, int count) in counts)
            {
                o.WriteLine($"  {table}  {count.ToString(CultureInfo.InvariantCulture)}");
            }

            IQueryable<CreatureTemplateRow> templates = db.Set<CreatureTemplateRow>();
            int missingCreatures = await db.Set<CreatureSpawnRow>()
                .CountAsync(s => s.Entry != 0 && !templates.Any(t => t.Entry == s.Entry), ct).ConfigureAwait(false);
            int randomEntryCreatures = await db.Set<CreatureSpawnRow>().CountAsync(s => s.Entry == 0, ct).ConfigureAwait(false);
            IQueryable<GameObjectTemplateRow> objectTemplates = db.Set<GameObjectTemplateRow>();
            int missingObjects = await db.Set<GameObjectSpawnRow>()
                .CountAsync(s => s.Entry != 0 && !objectTemplates.Any(t => t.Entry == s.Entry), ct).ConfigureAwait(false);
            int randomEntryObjects = await db.Set<GameObjectSpawnRow>().CountAsync(s => s.Entry == 0, ct).ConfigureAwait(false);

            int giversWithoutTemplate = await db.Set<CreatureQuestStarterRow>()
                .CountAsync(r => !templates.Any(t => t.Entry == r.Id), ct).ConfigureAwait(false);
            int endersWithoutTemplate = await db.Set<CreatureQuestEnderRow>()
                .CountAsync(r => !templates.Any(t => t.Entry == r.Id), ct).ConfigureAwait(false);
            IQueryable<ItemTemplateRow> itemTemplates = db.Set<ItemTemplateRow>();
            int startingItemsMissing = await db.Set<PlayerCreateInfoItemRow>()
                .CountAsync(r => !itemTemplates.Any(i => i.Entry == r.ItemId), ct).ConfigureAwait(false);
            if (giversWithoutTemplate + endersWithoutTemplate > 0)
            {
                o.WriteLine(
                    $"note: {giversWithoutTemplate} creature_questrelation and {endersWithoutTemplate} creature_involvedrelation row(s) name a creature " +
                    "that has no creature_template (the loaders keep these and only log them)");
            }

            int onKillWithoutTemplate = await db.Set<CreatureOnKillReputationRow>()
                .CountAsync(r => !templates.Any(t => t.Entry == r.CreatureId), ct).ConfigureAwait(false);
            if (onKillWithoutTemplate > 0)
            {
                o.WriteLine(
                    $"note: {onKillWithoutTemplate} creature_onkill_reputation row(s) name a creature that has no creature_template (vmangos skips them at load)");
            }

            if (startingItemsMissing > 0)
            {
                problems.Add($"{startingItemsMissing} playercreateinfo_item row(s) name an item that has no item_template");
            }

            if (missingCreatures > 0)
            {
                problems.Add($"{missingCreatures} creature spawn(s) reference a creature_template that does not exist");
            }

            if (missingObjects > 0)
            {
                problems.Add($"{missingObjects} gameobject spawn(s) reference a gameobject_template that does not exist");
            }

            if (randomEntryCreatures > 0)
            {
                o.WriteLine($"note: {randomEntryCreatures} creature spawn(s) have entry 0 (cmangos resolves these through creature_spawn_entry, which is not imported yet)");
            }

            if (randomEntryObjects > 0)
            {
                o.WriteLine($"note: {randomEntryObjects} gameobject spawn(s) have entry 0 (cmangos resolves these through gameobject_spawn_entry, which is not imported yet)");
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CliException))
        {
            throw DatabaseError(ex, target);
        }

        foreach (string problem in problems)
        {
            o.WriteLine($"problem: {problem}");
        }

        o.WriteLine(problems.Count == 0 ? "verify: no missing references" : $"verify: {problems.Count} problem(s)");
        return problems.Count == 0 ? ExitCodes.Ok : ExitCodes.Verify;
    }

    // --- shared ------------------------------------------------------------------------------------------

    private sealed record Target(DatabaseProvider Provider, string ConnectionString, string? FilePath)
    {
        public string Describe => FilePath is not null
            ? $"sqlite file '{FilePath}'"
            : $"{Provider} ({ConnectionStringRedactor.Redact(ConnectionString)})";
    }

    private static Target ResolveTarget(CliArguments a)
    {
        string? database = a.Value("--database");
        string? provider = a.Value("--provider");
        string? connection = a.Value("--connection-string");
        if (database is not null)
        {
            if (provider is not null || connection is not null)
            {
                throw new UsageException("give either --database <file> or --provider with --connection-string, not both");
            }

            string full = Path.GetFullPath(database);
            return new Target(DatabaseProvider.Sqlite, $"Data Source={full};Pooling=False", full);
        }

        if (provider is null)
        {
            throw new UsageException("no target database: give --database <file>, or --provider with --connection-string");
        }

        DatabaseProvider parsed = provider.ToLowerInvariant() switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "mariadb" => DatabaseProvider.MariaDb,
            "mysql" => DatabaseProvider.MySql,
            "postgresql" or "postgres" => DatabaseProvider.PostgreSql,
            _ => throw new UsageException($"unknown provider '{provider}' (sqlite, mariadb, mysql, postgresql)"),
        };
        connection ??= Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new UsageException($"--provider needs --connection-string (or the {ConnectionVariable} variable)");
        }

        return new Target(parsed, connection, null);
    }

    private static WorldDbContext OpenWorld(Target target)
    {
        var builder = new DbContextOptionsBuilder<WorldDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(
            builder, new DatabaseConnectionOptions { Provider = target.Provider, ConnectionString = target.ConnectionString });
        return new WorldDbContext(builder.Options);
    }

    private static CliException DatabaseError(Exception ex, Target target)
    {
        string message = ConnectionStringRedactor.Scrub(ex.GetBaseException().Message, target.ConnectionString);
        return new CliException(ExitCodes.Database, $"database error ({ex.GetBaseException().GetType().Name}): {message}; nothing was changed", ex);
    }

    private static string ParseDialect(CliArguments a)
    {
        string dialect = (a.Value("--dialect") ?? "auto").ToLowerInvariant();
        return dialect is "auto" or "cmangos" or "vmangos"
            ? dialect
            : throw new UsageException($"unknown dialect '{dialect}' (auto, cmangos, vmangos)");
    }

    private static QuestXpSource ParseQuestXp(CliArguments a)
        => (a.Value("--quest-xp") ?? "derived").ToLowerInvariant() switch
        {
            "derived" => QuestXpSource.Derived,
            "none" => QuestXpSource.None,
            string other => throw new UsageException($"unknown --quest-xp '{other}' (derived, none)"),
        };

    private static void RequireInputs(CliArguments a)
    {
        if (a.Positional.Count == 0)
        {
            throw new UsageException($"{a.Command} needs at least one dump file");
        }
    }

    private static (IReadOnlyList<DumpInput> Inputs, IReadOnlyList<SourceFileInfo> Files) OpenInputs(IReadOnlyList<string> paths)
    {
        var inputs = new List<DumpInput>();
        var files = new List<SourceFileInfo>();
        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                throw new CliException(ExitCodes.Io, $"input file '{path}' not found");
            }

            inputs.Add(DumpInput.File(path));
            files.Add(DumpFiles.Describe(path));
        }

        return (inputs, files);
    }

    private static void GuardPath(string? path)
    {
        if (path is null)
        {
            return;
        }

        string? refusal = RepositoryPathGuard.Check(path);
        if (refusal is not null)
        {
            throw new CliException(ExitCodes.RepositoryPath, refusal);
        }
    }

    private static void CheckDialect(ScanResult scan, string requested)
    {
        if (requested == "auto")
        {
            return;
        }

        foreach (TableScan table in scan.Tables.Values)
        {
            if (table.Dialect != ContentDialect.Unknown && table.Dialect.Family() != requested)
            {
                throw new ImportSchemaException(
                    table.Table, null,
                    $"table `{table.Table}` is in the {table.Dialect.Family()} layout ({table.Dialect}) but --dialect {requested} was given");
            }
        }
    }

    private static List<string> MixedDialectWarning(ScanResult scan)
    {
        var warnings = new List<string>();
        if (scan.Families.Count > 1)
        {
            warnings.Add("the dump mixes dialects (" + string.Join(", ", scan.Families.Order(StringComparer.Ordinal)) + "); each table is read in its own layout");
        }

        return warnings;
    }

    private static void WriteLevelStatsFile(string path, PlayerCreateDumpImporter importer)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        importer.WriteLevelStats(writer);
    }

    private static void WriteReport(string? path, ContentImportReport report)
    {
        if (path is null)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, report.ToJson());
    }

    private static void PrintWarnings(TextWriter o, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        o.WriteLine($"warnings ({warnings.Count}):");
        foreach (string warning in warnings.Take(10))
        {
            o.WriteLine($"  {warning}");
        }

        if (warnings.Count > 10)
        {
            o.WriteLine($"  ... {warnings.Count - 10} more (see --report)");
        }
    }

    private static void PrintScan(TextWriter o, IReadOnlyList<SourceFileInfo> files, ScanResult scan, bool verbose)
    {
        o.WriteLine($"inputs ({files.Count}):");
        foreach (SourceFileInfo file in files)
        {
            o.WriteLine($"  {file.Name}  {file.Bytes.ToString(CultureInfo.InvariantCulture)} bytes  sha256 {file.Sha256}{(file.Gzip ? "  gzip" : string.Empty)}");
        }

        if (scan.DbVersion is not null)
        {
            o.WriteLine($"db_version: {scan.DbVersion}");
        }

        o.WriteLine("tables an importer reads:");
        foreach (TableSpec spec in ContentTableSpecs.All)
        {
            if (!scan.Tables.TryGetValue(spec.Table, out TableScan? table) || (table.Rows == 0 && !verbose))
            {
                continue;
            }

            string dialect = table.Dialect == ContentDialect.Unknown ? "-" : table.Dialect.ToString();
            o.WriteLine($"  {table.Table}  {dialect}  rows {table.Rows}  keys {table.DistinctKeys}  duplicates {table.DuplicateKeys}");
            o.WriteLine($"      mapped: {string.Join(", ", table.MappedColumns)}");
            o.WriteLine($"      not imported: {(table.UnmappedColumns.Count == 0 ? "(none)" : string.Join(", ", table.UnmappedColumns))}");
        }

        TableScan[] others = [.. scan.Tables.Values.Where(t => !t.HasSpec)];
        long otherRows = others.Sum(t => t.Rows);
        o.WriteLine($"{others.Length} other table(s) with {otherRows} row(s) are not read by any importer yet:");
        o.WriteLine("  " + string.Join(", ", others.Where(t => verbose || t.Rows > 0).Select(t => $"{t.Table} ({t.Rows})")));
        if (scan.UnappliedStatements.Count > 0)
        {
            o.WriteLine("statements read but not applied (a dump is a snapshot, updates are not replayed):");
            foreach ((string statement, int count) in scan.UnappliedStatements.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                o.WriteLine($"  {statement} x{count}");
            }
        }
    }
}
