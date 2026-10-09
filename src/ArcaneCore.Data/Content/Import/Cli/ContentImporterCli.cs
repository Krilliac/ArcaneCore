using System.Globalization;
using ArcaneCore.Data.Content.Chr;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.Procs;
using ArcaneCore.Data.World.SpawnGroups;
using ArcaneCore.Data.World.SpecialLoot;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Data.World.PlayerStats;
using ArcaneCore.Data.World.Totems;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.PlayerStats;
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
public static partial class ContentImporterCli
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
                                location (portal, GM teleport) and totem spell tables
                                (and the starting outfit, playercreateinfo_item), and the world-state
                                tables game_weather (zone weather chances) and exploration_basexp, and the seven
                                game-event tables (game_event, game_event_time, game_event_creature, game_event_gameobject,
                                game_event_creature_data, game_event_quest, game_event_mail; both dialects)
          import-dbc <dir>      import the five spell DBCs from a client DBFilesClient directory
          import-map-dbc <Map.dbc> <AreaTable.dbc>
                                import every map and area (map_template, area_template) using the build-5875
                                layouts; --dump <file> takes the dungeon columns (player limit, reset delay, ghost
                                entrance, script) from a classic-db instance_template or vmangos map_template
          verify                count the imported tables and check references
          class-masks <dump>... read spell_affect (the 64-bit class masks of the talent modifier auras)
                                and write the overlay file Spells:Mods:ClassMaskFile reads
                                (--class-mask-file <file>, outside the repository; --dry-run writes nothing)
          proc-events <dump>... replace spell_proc_event (the proc conditions: flags, procEx, PPM, cooldown,
                                family masks) with the dump's rows for build 5875; vmangos rows outside
                                the build range are dropped (--cooldown-unit ms|seconds: classic-db dumps
                                before z2829 store seconds; --dry-run writes nothing)
          refresh <dump>...     replace, in one transaction, only the world tables a world built by an older importer
                                lacks: world_safe_locs and game_graveyard_zone, the four battleground tables,
                                exploration_basexp and game_weather, areatrigger_tavern, transports, spell_proc_event,
                                dbscripts_on_relay and dbscript_relay_template, dbscripts_on_quest_start/quest_end/gossip/event
                                and script_waypoint (plus the script ids of quest_template and gossip_menu, and the
                                gossip_menu_option rows that run a script), ScriptDev2's script_texts (and the carried
                                gossip_texts) in creature_ai_texts by entry and the script_waypoint/waypoint_path copies in
                                creature_movement_template (their path namespaces only), the ships' gameobject_template rows (type 15;
                                other objects are left alone), and from --dbc-dir: areatrigger_template (AreaTrigger.dbc),
                                taxi_nodes (TaxiNodes.dbc) and taxi_path (TaxiPath.dbc); WorldSafeLocs.dbc there adds the safe
                                locations the dump lacks, TaxiPathNode.dbc there checks every ship's route; Map.dbc and
                                AreaTable.dbc there (both or neither) replace map_template (every map, the dungeon columns
                                from the dump's instance_template) and area_template (every area).
                                The seven game-event tables are filled when the world has no game_event row (a world built
                                before the game-event importer); a world with events keeps its own.
                                creature_spawn_entry (the entries of the spawns whose creature.id is 0) is filled the same
                                way, when it is empty, for the world's own spawns whose entry is 0 or one of the dump's.
                                gameobject_spawn_entry likewise; the five spawn group tables when spawn_group is empty, for
                                the members the world has with the entry the dump gives them.
                                A table the inputs do not carry is left as it is, so running it again changes nothing.
                                (--cooldown-unit auto|ms|seconds, default auto: the classic-db db_version decides;
                                --dry-run writes nothing; --report <file>). A world whose schema is behind this
                                importer's is refused unless --migrate is given (refresh never migrates on its own).

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
          --player-stats-migrations-dir <dir>
                                           import: replay supported player-stat migrations in name order
                                           after base rows; source choice must be version checked
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
                "import-map-dbc" => await ImportMapDbcAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "verify" => await VerifyAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "class-masks" => ClassMasks(arguments, output),
                "proc-events" => await ProcEventsAsync(arguments, output, cancellationToken).ConfigureAwait(false),
                "refresh" => await RefreshAsync(arguments, output, cancellationToken).ConfigureAwait(false),
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

    // --- class-masks -------------------------------------------------------------------------

    private static int ClassMasks(CliArguments a, TextWriter o)
    {
        RequireInputs(a);
        string path = a.Value("--class-mask-file") ?? throw new UsageException("class-masks needs --class-mask-file <file>");
        GuardPath(path);
        (IReadOnlyList<DumpInput> inputs, _) = OpenInputs(a.Positional);
        var importer = new SpellAffectDumpImporter();
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            importer.Read(reader);
        }

        SpellAffectImportReport report = importer.BuildReport();
        if (report.Rows == 0)
        {
            throw new CliException(ExitCodes.Schema, "the dump has no spell_affect rows (is it a classic-db world dump?)");
        }

        o.WriteLine($"spell_affect: {report.Rows} row(s), {report.WideMasks} above 32 bits, {report.ZeroMasks} empty, {report.SkippedRows} skipped");
        foreach (string warning in report.Warnings)
        {
            o.WriteLine($"warning: {warning}");
        }

        if (a.Flag("--dry-run"))
        {
            o.WriteLine("class mask file: would write the overlay to the --class-mask-file path (dry run)");
            return ExitCodes.Ok;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false)))
        {
            importer.WriteOverlay(writer);
        }

        o.WriteLine($"class mask file: wrote {report.Rows} row(s) to {Path.GetFullPath(path)}; set Spells:Mods:ClassMaskFile to it");
        return ExitCodes.Ok;
    }

    // --- proc-events -------------------------------------------------------------------------

    /// <summary>
    /// <c>proc-events</c>: <see cref="SpellProcEventDumpImporter"/> from the command line. The whole dump is parsed (and the build filter applied)
    /// before any database is opened, and the table is replaced in one transaction, so a malformed dump changes nothing.
    /// </summary>
    private static async Task<int> ProcEventsAsync(CliArguments a, TextWriter o, CancellationToken ct)
    {
        RequireInputs(a);
        ProcCooldownUnit cooldownUnit = (a.Value("--cooldown-unit") ?? "ms").ToLowerInvariant() switch
        {
            "ms" or "milliseconds" => ProcCooldownUnit.Milliseconds,
            "s" or "seconds" => ProcCooldownUnit.Seconds,
            string other => throw new UsageException($"unknown --cooldown-unit '{other}' (ms, seconds)"),
        };
        bool dryRun = a.Flag("--dry-run");
        Target? target = dryRun && a.Value("--database") is null && a.Value("--provider") is null ? null : ResolveTarget(a);
        if (!dryRun)
        {
            GuardPath(target!.FilePath);
        }

        (IReadOnlyList<DumpInput> inputs, _) = OpenInputs(a.Positional);
        SpellProcEventParseResult result;
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            result = SpellProcEventDumpImporter.Parse(reader, cooldownUnit: cooldownUnit);
        }

        if (result.RowsRead == 0)
        {
            throw new CliException(ExitCodes.Schema, "the dump has no spell_proc_event rows (is it a classic-db or vmangos world dump?)");
        }

        string counts = $"spell_proc_event: {result.RowsRead} row(s) read, {result.RowsFilteredByBuild} outside build {SpellProcEventDumpImporter.SupportedBuild}";
        if (dryRun)
        {
            o.WriteLine($"{counts}, {result.Content.Count} to import (dry run: no database written)");
            return ExitCodes.Ok;
        }

        o.WriteLine($"target: {target!.Describe}");
        int written;
        try
        {
            await using WorldDbContext db = OpenWorld(target);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema, cancellationToken: ct).ConfigureAwait(false);
            written = await SpellProcEventDumpImporter.ImportAsync(db, result.Content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CliException))
        {
            throw DatabaseError(ex, target);
        }

        o.WriteLine($"{counts}, {written} imported; a running world picks them up with .reload spell_proc_event");
        return ExitCodes.Ok;
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
        var graveyards = new GraveyardDumpImporter();
        var totems = new TotemSpellDumpImporter();
        var playerStats = new PlayerStatsDumpImporter();
        var startingSkills = new StartingSkillDumpImporter();
        var npc = new NpcDumpImporter();
        var conditions = new ConditionsDumpImporter();
        var battlegrounds = new BattlegroundDumpImporter();
        var spawnGroups = new SpawnGroupDumpImporter();
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

        // The game-event tables (both dialects), by column name.
        var gameEvents = new GameEventDumpImporter();
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            gameEvents.Read(reader);
        }

        // game_weather and exploration_basexp (the world-state tables): read by column name, the same dumps as above.
        WorldStateContent worldState;
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            worldState = WorldStateDumpImporter.Parse(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            graveyards.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            totems.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            playerStats.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            startingSkills.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            npc.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            conditions.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            battlegrounds.Read(reader);
        }

        // gameobject_spawn_entry and the cmangos spawn group tables (world 44): read by column name.
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            spawnGroups.Read(reader);
        }

        IReadOnlyList<string> appliedStatsMigrations = [];
        if (a.Value("--player-stats-migrations-dir") is { } statsMigrationsDirectory)
        {
            if (!Directory.Exists(statsMigrationsDirectory))
            {
                throw new CliException(ExitCodes.Io, "player stats migration directory does not exist");
            }

            try
            {
                appliedStatsMigrations = playerStats.ApplyMigrationDirectory(statsMigrationsDirectory);
            }
            catch (NotSupportedException ex)
            {
                throw new CliException(ExitCodes.Schema, $"unsupported player stats migration: {ex.Message}", ex);
            }
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

            // vmangos keeps its safe locations in WorldSafeLocs.dbc; a cmangos dump has them in world_safe_locs (optional here).
            string safeLocsPath = Path.Combine(dbcDirectory, "WorldSafeLocs.dbc");
            if (File.Exists(safeLocsPath))
            {
                try
                {
                    graveyards.ReadSafeLocs(WorldSafeLocsDbcReader.Load(safeLocsPath));
                }
                catch (InvalidDataException ex)
                {
                    throw new CliException(ExitCodes.Io, $"WorldSafeLocs.dbc: {ex.Message}", ex);
                }
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
        GraveyardImportReport graveyardReport = graveyards.BuildReport();
        TotemSpellImportReport totemReport = totems.Resolve().Report;
        PlayerStatsImportReport statsReport = playerStats.BuildReport();
        StartingSkillImportReport startingSkillReport = startingSkills.BuildReport();
        NpcImportReport npcReport = npc.BuildReport();
        ConditionsImportReport conditionsReport = conditions.BuildReport();
        BattlegroundImportReport battlegroundReport = battlegrounds.BuildReport();
        SpawnGroupImportReport spawnGroupReport = spawnGroups.BuildReport();
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
                    await WorldStateDumpImporter.WriteAsync(db, worldState, replace, token).ConfigureAwait(false);
                    await gameEvents.WriteAsync(db, replace, token).ConfigureAwait(false);
                    graveyardReport = await graveyards.WriteAsync(db, replace, token).ConfigureAwait(false);
                    totemReport = await totems.WriteAsync(db, replace, token).ConfigureAwait(false);
                    statsReport = await playerStats.WriteAsync(db, replace, token).ConfigureAwait(false);
                    startingSkillReport = await startingSkills.WriteAsync(db, replace, token).ConfigureAwait(false);
                    npcReport = await npc.WriteAsync(db, replace, token).ConfigureAwait(false);
                    conditionsReport = await conditions.WriteAsync(db, replace, token).ConfigureAwait(false);
                    battlegroundReport = await battlegrounds.WriteAsync(db, replace, token).ConfigureAwait(false);
                    spawnGroupReport = await spawnGroups.WriteAsync(db, replace, token).ConfigureAwait(false);
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
        warnings.AddRange(graveyardReport.Warnings);
        warnings.AddRange(statsReport.Warnings);
        warnings.AddRange(startingSkillReport.Warnings);
        warnings.AddRange(npcReport.Diagnostics);
        warnings.AddRange(battlegroundReport.Warnings);
        warnings.AddRange(spawnGroupReport.Warnings);
        if (totemReport.SummonedWithoutRow.Count > 0)
        {
            warnings.Add($"{totemReport.SummonedWithoutRow.Count} summoned totem creature(s) have no spell mapping "
                + $"(this can be intentional, e.g. Sentry Totem): {string.Join(", ", totemReport.SummonedWithoutRow)}");
        }
        if (itemQuestReport.DerivedQuestXp > 0)
        {
            warnings.Add(
                $"quest_template.RewXP was derived from RewMoneyMaxLevel for {itemQuestReport.DerivedQuestXp} quest(s) " +
                "(the source has no RewXP; cmangos Quest::XPValue); XP reduced for grey quests can differ from cmangos by 1");
        }

        (Dictionary<string, long> imported, Dictionary<string, long> skipped) = Counts(creatureReport, objectReport, itemQuestReport, onKillReport, playerReport, startActionReport, locationReport, totemReport);
        GameEventImportReport gameEventReport = gameEvents.BuildReport();
        warnings.AddRange(gameEventReport.Warnings);
        imported["game_event"] = gameEventReport.Events;
        imported["game_event_time"] = gameEventReport.Times;
        imported["game_event_creature"] = gameEventReport.Creatures;
        imported["game_event_gameobject"] = gameEventReport.GameObjects;
        imported["game_event_creature_data"] = gameEventReport.CreatureData;
        imported["game_event_quest"] = gameEventReport.Quests;
        imported["game_event_mail"] = gameEventReport.Mails;
        skipped["game_event_rows"] = gameEventReport.SkippedRows;
        imported["game_weather"] = worldState.Weather.Count;
        imported["exploration_basexp"] = worldState.BaseXp.Count;
        imported["world_safe_locs"] = graveyardReport.SafeLocs;
        imported["game_graveyard_zone"] = graveyardReport.Links;
        skipped["game_graveyard_zone"] = graveyardReport.SkippedLinks;
        imported["player_classlevelstats"] = statsReport.ClassLevelStats;
        imported["player_levelstats"] = statsReport.LevelStats;
        imported["player_xp_for_level"] = statsReport.XpRows;
        imported["player_crit_per_agility"] = statsReport.CritRows;
        imported["player_dodge_per_agility"] = statsReport.DodgeRows;
        skipped["player_stats_rows"] = statsReport.SkippedRows;
        imported["playercreateinfo_skills"] = startingSkillReport.Rows;
        skipped["playercreateinfo_skills_rows"] = startingSkillReport.SkippedRows;
        imported["npc_gossip"] = npcReport.NpcGossips;
        imported["gossip_menu"] = npcReport.GossipMenus;
        imported["gossip_menu_option"] = npcReport.GossipOptions;
        imported["npc_text"] = npcReport.NpcTexts;
        imported["npc_vendor"] = npcReport.Vendors;
        imported["npc_trainer"] = npcReport.Trainers;
        imported["conditions"] = conditionsReport.Conditions;
        imported["battleground_template"] = battlegroundReport.Templates;
        imported["creature_battleground"] = battlegroundReport.CreatureEvents;
        imported["gameobject_battleground"] = battlegroundReport.GameObjectEvents;
        imported["battlemaster_entry"] = battlegroundReport.Battlemasters;
        skipped["battleground_rows"] = battlegroundReport.SkippedRows;
        imported[SpawnGroupDataModule.GameObjectSpawnEntryTable] = spawnGroupReport.GameObjectSpawnEntries;
        imported[SpawnGroupDataModule.GroupTable] = spawnGroupReport.Groups;
        imported[SpawnGroupDataModule.SpawnTable] = spawnGroupReport.Spawns;
        imported[SpawnGroupDataModule.EntryTable] = spawnGroupReport.Entries;
        imported[SpawnGroupDataModule.FormationTable] = spawnGroupReport.Formations;
        imported[SpawnGroupDataModule.LinkedGroupTable] = spawnGroupReport.LinkedGroups;
        skipped["npc_service_rows"] = npcReport.Skipped;
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
                int writtenRows = WriteLevelStatsFile(levelStatsPath, playerCreate,
                    appliedStatsMigrations.Count > 0 ? playerStats.ToContent() : null);
                o.WriteLine($"level stats file: wrote {writtenRows} row(s) to {Path.GetFullPath(levelStatsPath)}; set Progression:LevelStatsPath to it");
            }
        }
        else if (playerReport.LevelStatRows > 0)
        {
            warnings.Add($"{playerReport.LevelStatRows} level-stats row(s) were imported into the database; no external --level-stats-file was requested");
        }

        PrintWarnings(o, warnings);
        WriteReport(reportPath, ContentImportReport.Create("import", files, scan, warnings, dryRun) with
        {
            Imported = imported,
            Skipped = skipped,
            PlayerStatsMigrations = appliedStatsMigrations,
        });
        return ExitCodes.Ok;
    }

    private static (Dictionary<string, long> Imported, Dictionary<string, long> Skipped) Counts(
        CreatureImportReport creatures, GameObjectLootImportReport objects, ItemQuestImportReport itemsAndQuests, ReputationOnKillImportReport onKill,
        PlayerCreateImportReport playerCreate, PlayerCreateActionImportReport startActions, LocationImportReport locations, TotemSpellImportReport totems)
    {
        var imported = new Dictionary<string, long>
        {
            ["creature_template"] = creatures.Templates,
            ["creature_spawn"] = creatures.Spawns,
            ["creature_movement"] = creatures.Waypoints,
            ["creature_movement_template"] = creatures.MovementTemplates - creatures.ScriptWaypointCopies - creatures.WaypointPaths,
            // script_waypoint itself (world 42) is counted from DbScriptTables below; this is its copy in creature_movement_template.
            ["script_waypoint (creature_movement_template copy)"] = creatures.ScriptWaypointCopies,
            ["waypoint_path"] = creatures.WaypointPaths,
            ["creature_spawn_entry"] = creatures.SpawnEntries,
            ["creature_model_info"] = creatures.Models,
            ["creature_addon"] = creatures.Addons,
            ["creature_ai_scripts"] = creatures.AiEvents,
            ["creature_ai_texts"] = creatures.AiTexts - creatures.ScriptTexts,
            ["script_texts"] = creatures.ScriptTexts,
            ["dbscript_random_templates"] = creatures.AiTextTemplates,
            ["dbscripts_on_relay"] = creatures.RelayScriptSteps,
            ["dbscript_relay_template"] = creatures.RelayScriptTemplates,
            ["gameobject_template"] = objects.Templates,
            ["gameobject_spawn"] = objects.Spawns,
            ["gameobject_questrelation"] = objects.QuestStarters,
            ["gameobject_involvedrelation"] = objects.QuestEnders,
            ["lock_template"] = objects.Locks,
            ["loot_template_rows"] = objects.LootRows,
            ["creature_loot_info"] = objects.CreatureLootInfos,
            ["skill_fishing_base_level"] = objects.FishingBaseLevels,
            ["creature_pickpocket_loot"] = objects.PickpocketLootIds,
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
            ["spell_script_target"] = creatures.SpellScriptTargets,
            ["creature_linking"] = creatures.CreatureLinks,
            ["creature_linking_template"] = creatures.CreatureTemplateLinks,
            ["level_stats_rows"] = playerCreate.LevelStatRows,
            ["areatrigger_teleport"] = locations.Portals,
            ["areatrigger_involvedrelation"] = locations.QuestTriggers,
            ["game_tele"] = locations.Teleports,
            ["totem_spell"] = totems.Rows,
        };
        foreach ((string table, int rows) in creatures.DbScriptTables)
        {
            imported[table] = rows;
        }

        var skipped = new Dictionary<string, long>
        {
            ["creature_spawn"] = creatures.SkippedSpawns,
            ["gameobject_and_loot_rows"] = objects.SkippedRows,
            ["item_and_quest_rows"] = itemsAndQuests.SkippedRows,
            ["creature_onkill_reputation"] = onKill.SkippedRows,
            ["player_create_rows"] = playerCreate.SkippedRows,
            ["playercreateinfo_action_rows"] = startActions.SkippedRows,
            ["areatrigger_teleport_rows"] = locations.SkippedRows,
            ["totem_creatures_without_spell"] = totems.SkippedWithoutSpell,
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

    private static async Task<int> ImportMapDbcAsync(CliArguments a, TextWriter output, CancellationToken ct)
    {
        if (a.Positional.Count != 2)
            throw new UsageException("import-map-dbc takes exactly Map.dbc and AreaTable.dbc paths");
        Target target = ResolveTarget(a);
        string? reportPath = a.Value("--report");
        GuardPath(target.FilePath);
        GuardPath(reportPath);
        IReadOnlyDictionary<uint, MapInstanceData>? instances = null;
        IReadOnlyList<string> corrections = [];
        if (a.Value("--dump") is { } dumpPath)
        {
            (IReadOnlyList<DumpInput> inputs, _) = OpenInputs([dumpPath]);
            var importer = new InstanceTemplateDumpImporter();
            using (TextReader reader = ChainedTextReader.Create(inputs))
            {
                importer.Read(reader);
            }

            instances = importer.SawTable ? importer.Maps : null;
            corrections = importer.Corrections;
        }

        MapAreaDbcSnapshot snapshot;
        try
        {
            // Validate the complete admission before opening or bootstrapping a destination database.
            snapshot = MapAreaDbcImporter.ReadSnapshot(a.Positional[0], a.Positional[1], instances);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Io, "cannot read the map/area DBC inputs", ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            throw new CliException(ExitCodes.Schema, "map/area DBC layout or references were refused", ex);
        }

        MapAreaDbcImportReport report;
        try
        {
            await using WorldDbContext db = OpenWorld(target);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema, cancellationToken: ct).ConfigureAwait(false);
            report = await MapAreaDbcImporter.ImportSnapshotAsync(db, snapshot, a.Flag("--replace"), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CliException))
        {
            throw DatabaseError(ex, target);
        }
        if (reportPath is not null)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (directory is not null) Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(report,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        }
        output.WriteLine($"Imported {report.MappedMaps} maps and {report.MappedAreas} areas; {report.InstanceRows} map(s) took instance data from the dump.");
        foreach (string correction in corrections)
        {
            output.WriteLine($"  instance data: {correction}");
        }

        PrintWarnings(output, report.Warnings);
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
                ("creature_movement_template", await db.Set<CreatureMovementTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_spawn_entry", await db.Set<CreatureSpawnEntryRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_model_info", await db.Set<CreatureModelInfoRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_addon", await db.Set<CreatureAddonRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_ai_text_template", await db.Set<CreatureTextTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("dbscripts_on_relay", await db.Set<RelayScriptRow>().CountAsync(ct).ConfigureAwait(false)),
                ("dbscript_relay_template", await db.Set<RelayScriptTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                (DbScriptDataModule.QuestStartTable, await db.Set<QuestStartScriptRow>().CountAsync(ct).ConfigureAwait(false)),
                (DbScriptDataModule.QuestEndTable, await db.Set<QuestEndScriptRow>().CountAsync(ct).ConfigureAwait(false)),
                (DbScriptDataModule.GossipTable, await db.Set<GossipScriptRow>().CountAsync(ct).ConfigureAwait(false)),
                (DbScriptDataModule.EventTable, await db.Set<EventScriptRow>().CountAsync(ct).ConfigureAwait(false)),
                (DbScriptDataModule.CreatureMovementTable, await db.Set<CreatureMovementScriptRow>().CountAsync(ct).ConfigureAwait(false)),
                ("spell_script_target", await db.Set<SpellScriptTargetRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_linking", await db.Set<CreatureLinkRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_linking_template", await db.Set<CreatureTemplateLinkRow>().CountAsync(ct).ConfigureAwait(false)),
                (DbScriptDataModule.WaypointTable, await db.Set<ScriptWaypointRow>().CountAsync(ct).ConfigureAwait(false)),
                ("gameobject_template", await db.Set<GameObjectTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("gameobject_spawn", await db.Set<GameObjectSpawnRow>().CountAsync(ct).ConfigureAwait(false)),
                ("lock_template", await db.Set<LockTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_loot_template", await db.Set<CreatureLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("gameobject_loot_template", await db.Set<GameObjectLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("item_loot_template", await db.Set<ItemLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("skinning_loot_template", await db.Set<SkinningLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("reference_loot_template", await db.Set<ReferenceLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("fishing_loot_template", await db.Set<FishingLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("pickpocketing_loot_template", await db.Set<PickpocketingLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("disenchant_loot_template", await db.Set<DisenchantLootTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
                ("skill_fishing_base_level", await db.Set<SkillFishingBaseLevelRow>().CountAsync(ct).ConfigureAwait(false)),
                ("creature_pickpocket_loot", await db.Set<CreaturePickpocketLootRow>().CountAsync(ct).ConfigureAwait(false)),
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
                ("areatrigger_involvedrelation", await db.Set<AreaTriggerQuestRow>().CountAsync(ct).ConfigureAwait(false)),
                ("game_tele", await db.Set<GameTeleRow>().CountAsync(ct).ConfigureAwait(false)),
                ("totem_spell", await db.Set<TotemSpellRow>().CountAsync(ct).ConfigureAwait(false)),
                ("spell_template", await db.Set<SpellTemplateRow>().CountAsync(ct).ConfigureAwait(false)),
            };
            foreach ((string table, int count) in counts)
            {
                o.WriteLine($"  {table}  {count.ToString(CultureInfo.InvariantCulture)}");
            }

            IQueryable<CreatureTemplateRow> templates = db.Set<CreatureTemplateRow>();
            IQueryable<BroadcastTextRow> broadcastTexts = db.Set<BroadcastTextRow>();
            IQueryable<CreatureAiTextRow> aiTexts = db.Set<CreatureAiTextRow>();
            int missingTemplateTexts = await db.Set<CreatureTextTemplateRow>()
                .CountAsync(row => (row.TargetId > 0 && !broadcastTexts.Any(text => text.Id == (uint)row.TargetId))
                    || (row.TargetId < 0 && !aiTexts.Any(text => text.Entry == row.TargetId)), ct).ConfigureAwait(false);
            if (missingTemplateTexts > 0)
                problems.Add($"{missingTemplateTexts} creature_ai_text_template choice(s) reference missing broadcast_text or creature_ai_texts");
            int missingCreatures = await db.Set<CreatureSpawnRow>()
                .CountAsync(s => s.Entry != 0 && !templates.Any(t => t.Entry == s.Entry), ct).ConfigureAwait(false);
            IQueryable<CreatureSpawnEntryRow> spawnEntries = db.Set<CreatureSpawnEntryRow>();
            IQueryable<GameObjectTemplateRow> objectTemplates = db.Set<GameObjectTemplateRow>();
            int missingObjects = await db.Set<GameObjectSpawnRow>()
                .CountAsync(s => s.Entry != 0 && !objectTemplates.Any(t => t.Entry == s.Entry), ct).ConfigureAwait(false);

            // Spawns with entry 0: an entry comes from *_spawn_entry, else from the spawn_group_entry rows (with a template) of the spawn's group.
            IQueryable<GameObjectSpawnEntryRow> objectSpawnEntries = db.Set<GameObjectSpawnEntryRow>();
            IQueryable<SpawnGroupRow> groups = db.Set<SpawnGroupRow>();
            IQueryable<SpawnGroupSpawnRow> members = db.Set<SpawnGroupSpawnRow>();
            IQueryable<SpawnGroupEntryRow> groupEntries = db.Set<SpawnGroupEntryRow>();
            IQueryable<CreatureSpawnRow> zeroCreatures = db.Set<CreatureSpawnRow>().Where(s => s.Entry == 0);
            int creaturesBySpawnEntry = await zeroCreatures.CountAsync(s => spawnEntries.Any(e => e.SpawnGuid == s.Guid), ct).ConfigureAwait(false);
            int creaturesByGroup = await zeroCreatures.CountAsync(s => !spawnEntries.Any(e => e.SpawnGuid == s.Guid)
                && members.Any(m => m.Guid == s.Guid && groups.Any(g => g.Id == m.Id && g.Type == 0)
                    && groupEntries.Any(e => e.Id == m.Id && templates.Any(t => t.Entry == e.Entry))), ct).ConfigureAwait(false);
            int randomEntryCreatures = await zeroCreatures.CountAsync(ct).ConfigureAwait(false) - creaturesBySpawnEntry - creaturesByGroup;
            IQueryable<GameObjectSpawnRow> zeroObjects = db.Set<GameObjectSpawnRow>().Where(s => s.Entry == 0);
            int objectsBySpawnEntry = await zeroObjects.CountAsync(s => objectSpawnEntries.Any(e => e.SpawnGuid == s.Guid), ct).ConfigureAwait(false);
            int objectsByGroup = await zeroObjects.CountAsync(s => !objectSpawnEntries.Any(e => e.SpawnGuid == s.Guid)
                && members.Any(m => m.Guid == s.Guid && groups.Any(g => g.Id == m.Id && g.Type == 1)
                    && groupEntries.Any(e => e.Id == m.Id && objectTemplates.Any(t => t.Entry == e.Entry))), ct).ConfigureAwait(false);
            int randomEntryObjects = await zeroObjects.CountAsync(ct).ConfigureAwait(false) - objectsBySpawnEntry - objectsByGroup;
            int creatureGroups = await groups.CountAsync(g => g.Type == 0, ct).ConfigureAwait(false);
            int objectGroups = await groups.CountAsync(g => g.Type == 1, ct).ConfigureAwait(false);
            o.WriteLine($"  gameobject_spawn_entry  {(await objectSpawnEntries.CountAsync(ct).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture)}");
            o.WriteLine($"  spawn_group  {creatureGroups.ToString(CultureInfo.InvariantCulture)} creature, {objectGroups.ToString(CultureInfo.InvariantCulture)} gameobject");
            o.WriteLine($"  spawn_group_spawn  {(await members.CountAsync(ct).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture)}");
            o.WriteLine($"  spawn_group_entry  {(await groupEntries.CountAsync(ct).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture)}");
            o.WriteLine($"entry-0 creature spawns: {creaturesBySpawnEntry.ToString(CultureInfo.InvariantCulture)} resolved by creature_spawn_entry, "
                + $"{creaturesByGroup.ToString(CultureInfo.InvariantCulture)} by spawn_group_entry, {randomEntryCreatures.ToString(CultureInfo.InvariantCulture)} by nothing");
            o.WriteLine($"entry-0 gameobject spawns: {objectsBySpawnEntry.ToString(CultureInfo.InvariantCulture)} resolved by gameobject_spawn_entry, "
                + $"{objectsByGroup.ToString(CultureInfo.InvariantCulture)} by spawn_group_entry, {randomEntryObjects.ToString(CultureInfo.InvariantCulture)} by nothing");

            int giversWithoutTemplate = await db.Set<CreatureQuestStarterRow>()
                .CountAsync(r => !templates.Any(t => t.Entry == r.Id), ct).ConfigureAwait(false);
            int endersWithoutTemplate = await db.Set<CreatureQuestEnderRow>()
                .CountAsync(r => !templates.Any(t => t.Entry == r.Id), ct).ConfigureAwait(false);
            IQueryable<ItemTemplateRow> itemTemplates = db.Set<ItemTemplateRow>();
            int startingItemsMissing = await db.Set<PlayerCreateInfoItemRow>()
                .CountAsync(r => !itemTemplates.Any(i => i.Entry == r.ItemId), ct).ConfigureAwait(false);
            int totemsWithoutCreature = await db.Set<TotemSpellRow>()
                .CountAsync(r => !templates.Any(t => t.Entry == r.CreatureEntry), ct).ConfigureAwait(false);
            if (totemsWithoutCreature > 0)
            {
                problems.Add($"{totemsWithoutCreature} totem_spell row(s) name a creature that has no creature_template");
            }

            IQueryable<SpellTemplateRow> spells = db.Set<SpellTemplateRow>();
            if (await spells.AnyAsync(ct).ConfigureAwait(false))
            {
                int totemsWithoutSpell = await db.Set<TotemSpellRow>()
                    .CountAsync(r => !spells.Any(s => s.Id == r.SpellId), ct).ConfigureAwait(false);
                if (totemsWithoutSpell > 0)
                {
                    problems.Add($"{totemsWithoutSpell} totem_spell row(s) name a spell that has no imported spell_template");
                }
            }
            else if (await db.Set<TotemSpellRow>().AnyAsync(ct).ConfigureAwait(false))
            {
                o.WriteLine("note: totem spell references were not checked because no spell DBC content is imported; run import-dbc before starting the server");
            }
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
                o.WriteLine($"note: {randomEntryCreatures} creature spawn(s) have entry 0 and neither creature_spawn_entry rows nor a spawn group with entries, so they never spawn");
            }

            if (randomEntryObjects > 0)
            {
                o.WriteLine($"note: {randomEntryObjects} gameobject spawn(s) have entry 0 and neither gameobject_spawn_entry rows nor a spawn group with entries, so they never spawn");
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

    private static int WriteLevelStatsFile(string path, PlayerCreateDumpImporter importer, PlayerStatsContent? migrated)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        if (migrated is null)
        {
            return importer.WriteLevelStats(writer);
        }

        writer.Write("# race,class,level,basehp,basemana,str,agi,sta,int,spi\n");
        writer.Write("# written by arcane-content-importer after opted-in player-stat migrations\n");
        int rows = 0;
        foreach (LevelStats row in migrated.LevelRows)
        {
            ClassLevelStats? health = migrated.ClassLevel(row.Class, row.Level);
            if (health is null)
            {
                continue;
            }

            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"{row.Race},{row.Class},{row.Level},{health.BaseHealth},{health.BaseMana},{row.Strength},{row.Agility},{row.Stamina},{row.Intellect},{row.Spirit}\n"));
            rows++;
        }

        return rows;
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
