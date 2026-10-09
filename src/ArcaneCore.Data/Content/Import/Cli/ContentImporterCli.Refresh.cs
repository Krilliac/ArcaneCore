using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.World.Battlegrounds;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.Procs;
using ArcaneCore.Data.World.Rest;
using ArcaneCore.Data.World.Transports;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

public static partial class ContentImporterCli
{
    /// <summary>The first classic-db core revision whose <c>spell_proc_event.Cooldown</c> is in milliseconds (z2829).</summary>
    public const int ProcCooldownMillisecondsFromCore = 2829;

    [GeneratedRegex(@"core z(?<rev>\d{3,5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassicCoreRevision();

    /// <summary>
    /// <c>refresh</c>: bring a world database that an older importer built up to the tables the current world reads, without touching the
    /// creature, object, item, quest, loot or NPC tables it already has. Every table it writes is replaced (emptied, then filled) inside one
    /// transaction, and only when the inputs carry it, so a second run with the same inputs leaves the same rows and a failure changes nothing.
    /// Tables: <c>world_safe_locs</c> and <c>game_graveyard_zone</c> (dump; <c>WorldSafeLocs.dbc</c> fills ids the dump lacks), the battleground
    /// tables, <c>exploration_basexp</c> and <c>game_weather</c>, <c>areatrigger_tavern</c>, <c>transports</c>, <c>spell_proc_event</c> (build
    /// 5875, cooldown unit from the dump's classic-db revision unless given), the relay DB scripts, the quest, gossip and event DB scripts and
    /// <c>script_waypoint</c> (world 42; the script ids of the <c>quest_template</c> and <c>gossip_menu</c> rows the world has are set, and the
    /// gossip options that run a script are added when missing), <c>areatrigger_template</c> from
    /// <c>AreaTrigger.dbc</c>, and <c>taxi_nodes</c> / <c>taxi_path</c> from <c>TaxiNodes.dbc</c> / <c>TaxiPath.dbc</c>. The ships' own
    /// <c>gameobject_template</c> rows (type 15) are written as the dump has them; every other object template is left alone. With
    /// <c>Map.dbc</c> and <c>AreaTable.dbc</c> in <c>--dbc-dir</c> it also replaces <c>map_template</c> (every map, the dungeon columns from
    /// the dump's <c>instance_template</c> or <c>map_template</c>) and <c>area_template</c> (every area); one without the other is refused.
    /// Afterwards it checks the references the world logs at start (teleports and taverns without a trigger, battleground start locations
    /// without a safe location, transports without a type-15 object, portals to a map with no map_template row, graveyard links to a zone
    /// with no area_template row, and, with <c>TaxiPathNode.dbc</c>, every ship whose route the world could not build). Two groups are filled
    /// only when the world has none of their rows: the seven game-event tables, and <c>creature_spawn_entry</c> (for the world's own spawns
    /// only; see <see cref="FillSpawnEntriesAsync"/>). The world's schema
    /// must already be this importer's: a database behind it is refused unless <c>--migrate</c> is given, so a refresh never migrates a live
    /// world as a side effect.
    /// </summary>
    private static async Task<int> RefreshAsync(CliArguments a, TextWriter o, CancellationToken ct)
    {
        RequireInputs(a);
        bool dryRun = a.Flag("--dry-run");
        bool migrate = a.Flag("--migrate");
        Target? target = dryRun && a.Value("--database") is null && a.Value("--provider") is null ? null : ResolveTarget(a);
        if (!dryRun && target is null)
        {
            throw new UsageException("refresh needs a target: --database <file>, or --provider with --connection-string");
        }

        if (target?.FilePath is { } file)
        {
            GuardPath(file);
            if (!File.Exists(file))
            {
                // A refresh adds to a world that exists; creating an empty one would hide a wrong path.
                throw new CliException(ExitCodes.Io, $"database '{file}' does not exist (refresh updates an existing world database)");
            }
        }

        string? reportPath = a.Value("--report");
        GuardPath(reportPath);
        string? dbcDirectory = a.Value("--dbc-dir");
        if (dbcDirectory is not null && !Directory.Exists(dbcDirectory))
        {
            throw new CliException(ExitCodes.Io, $"--dbc-dir '{dbcDirectory}' does not exist");
        }

        (IReadOnlyList<DumpInput> inputs, IReadOnlyList<SourceFileInfo> files) = OpenInputs(a.Positional);
        ScanResult scan = ContentScanner.Scan(inputs);
        o.WriteLine(dryRun ? "refresh (dry run): reads and counts everything, writes nothing" : "refresh");
        foreach (SourceFileInfo info in files)
        {
            o.WriteLine($"  input {info.Name}  {info.Bytes.ToString(CultureInfo.InvariantCulture)} bytes  sha256 {info.Sha256}");
        }

        if (scan.DbVersion is not null)
        {
            o.WriteLine($"  db_version: {scan.DbVersion}");
        }

        var warnings = new List<string>();

        // --- read everything first: a malformed input fails before the database is opened -----------------------------------------
        var graveyards = new GraveyardDumpImporter();
        var battlegrounds = new BattlegroundDumpImporter();
        var taverns = new AreaTriggerTavernDumpImporter();
        var transports = new TransportDumpImporter();
        var relays = new CreatureDumpImporter();
        var instances = new InstanceTemplateDumpImporter();
        var dbScripts = new DbScriptDumpImporter();
        var gossip = new NpcDumpImporter();
        WorldStateContent worldState;
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            graveyards.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            battlegrounds.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            taverns.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            transports.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            relays.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            worldState = WorldStateDumpImporter.Parse(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            instances.Read(reader);
        }

        // The seven game-event tables (both dialects). Worlds built before the game-event importer, or migrated from the Codex-line
        // schema, have them empty: every holiday's NPCs then stand in the world all year and its quests are always offered (the
        // wave-8 rehearsal's bots took "Winter's Presents" and "Dearest Colara," in October). They are filled only when the world has
        // no event at all, so a world whose events were imported (or disabled by a GM, game_event.disabled) keeps them.
        var gameEvents = new GameEventDumpImporter();
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            gameEvents.Read(reader);
        }

        // World schema 42: the quest, gossip and event DB scripts, script_waypoint, the script ids of quest_template and gossip_menu, and the
        // gossip options that run a script (older importers skipped those options).
        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            dbScripts.Read(reader);
        }

        using (TextReader reader = ChainedTextReader.Create(inputs))
        {
            gossip.Read(reader);
        }

        IReadOnlyCollection<GossipMenuOption> scriptedOptions = gossip.ScriptedOptions;

        SpellProcEventParseResult? procs = null;
        if (scan.Tables.TryGetValue(SpellProcEventDataModule.Table, out TableScan? procTable) && procTable.Rows > 0)
        {
            ProcCooldownUnit unit = ResolveCooldownUnit(a.Value("--cooldown-unit"), scan.DbVersion);
            o.WriteLine($"  spell_proc_event cooldown unit: {(unit == ProcCooldownUnit.Seconds ? "seconds" : "milliseconds")}");
            using TextReader reader = ChainedTextReader.Create(inputs);
            procs = SpellProcEventDumpImporter.Parse(reader, cooldownUnit: unit);
        }

        IReadOnlyList<AreaTriggerTemplateRow>? triggers = null;
        MapAreaDbcSnapshot? mapTables = null;
        IReadOnlyList<TaxiNode>? taxiNodes = null;
        IReadOnlyList<TaxiPath>? taxiPaths = null;
        TaxiPathNodeCatalog? taxiPathNodes = null;
        if (dbcDirectory is not null)
        {
            string mapPath = Path.Combine(dbcDirectory, "Map.dbc");
            string areaPath = Path.Combine(dbcDirectory, "AreaTable.dbc");
            bool haveMaps = File.Exists(mapPath);
            bool haveAreas = File.Exists(areaPath);
            if (haveMaps != haveAreas)
            {
                // Half a map table set is worse than none: area lookups resolve maps through map_template, and the reverse.
                throw new CliException(ExitCodes.Io,
                    $"'{dbcDirectory}' has {(haveMaps ? "Map.dbc but no AreaTable.dbc" : "AreaTable.dbc but no Map.dbc")}; give both or neither. Nothing was changed.");
            }

            if (haveMaps)
            {
                mapTables = ReadDbc("Map.dbc and AreaTable.dbc",
                    () => MapAreaDbcImporter.ReadSnapshot(mapPath, areaPath, instances.SawTable ? instances.Maps : null));
                o.WriteLine($"  Map.dbc: {mapTables.Maps.Count} map(s), AreaTable.dbc: {mapTables.Areas.Count} area(s); " +
                    $"{mapTables.InstanceRows} map(s) with instance data from the dump");
                foreach (string correction in instances.Corrections)
                {
                    o.WriteLine($"  instance data: {correction}");
                }

                warnings.AddRange(mapTables.Warnings);
            }
            else
            {
                warnings.Add($"no Map.dbc and AreaTable.dbc in '{dbcDirectory}': map_template and area_template are left as they are");
            }

            string triggerPath = Path.Combine(dbcDirectory, "AreaTrigger.dbc");
            if (File.Exists(triggerPath))
            {
                triggers = ReadDbc("AreaTrigger.dbc", () => AreaTriggerDbcReader.Load(triggerPath));
            }
            else
            {
                warnings.Add($"no AreaTrigger.dbc in '{dbcDirectory}': areatrigger_template is left as it is");
            }

            string safeLocsPath = Path.Combine(dbcDirectory, "WorldSafeLocs.dbc");
            if (File.Exists(safeLocsPath))
            {
                IReadOnlyList<WorldSafeLoc> locs = ReadDbc("WorldSafeLocs.dbc", () => WorldSafeLocsDbcReader.Load(safeLocsPath));
                int added = graveyards.AddMissingSafeLocs(locs);
                o.WriteLine($"  WorldSafeLocs.dbc: {locs.Count} row(s), {added} not in the dump added");
            }

            // The flight masters' nodes and paths (vmangos reads them from these DBCs; the world reads the tables), and the ships' paths.
            string taxiNodesPath = Path.Combine(dbcDirectory, "TaxiNodes.dbc");
            if (File.Exists(taxiNodesPath))
            {
                taxiNodes = ReadDbc("TaxiNodes.dbc", () => NpcServiceDbcReaders.LoadTaxiNodes(taxiNodesPath));
            }
            else
            {
                warnings.Add($"no TaxiNodes.dbc in '{dbcDirectory}': taxi_nodes is left as it is");
            }

            string taxiPathPath = Path.Combine(dbcDirectory, "TaxiPath.dbc");
            if (File.Exists(taxiPathPath))
            {
                taxiPaths = ReadDbc("TaxiPath.dbc", () => NpcServiceDbcReaders.LoadTaxiPaths(taxiPathPath));
            }
            else
            {
                warnings.Add($"no TaxiPath.dbc in '{dbcDirectory}': taxi_path is left as it is");
            }

            string taxiPathNodePath = Path.Combine(dbcDirectory, "TaxiPathNode.dbc");
            if (File.Exists(taxiPathNodePath))
            {
                DbcFile pathNodeFile = ReadDbc("TaxiPathNode.dbc", () => DbcFile.Load(taxiPathNodePath));
                taxiPathNodes = ReadDbc("TaxiPathNode.dbc", () => NpcServiceDbcReaders.ReadTaxiPathNodes(pathNodeFile));
                o.WriteLine($"  TaxiPathNode.dbc: {pathNodeFile.RecordCount.ToString(CultureInfo.InvariantCulture)} row(s) on {taxiPathNodes.PathCount.ToString(CultureInfo.InvariantCulture)} path(s)");
            }
            else
            {
                warnings.Add($"no TaxiPathNode.dbc in '{dbcDirectory}': the ship routes (gameobject_template type 15, data0) are not checked");
            }
        }
        else
        {
            warnings.Add("no --dbc-dir: areatrigger_template is left as it is (it comes from AreaTrigger.dbc)");
            warnings.Add("no --dbc-dir: map_template and area_template are left as they are (they come from Map.dbc and AreaTable.dbc)");
            warnings.Add("no --dbc-dir: taxi_nodes and taxi_path are left as they are, the ship routes (gameobject_template type 15, data0) are not checked");
        }

        GraveyardImportReport graveyardReport = graveyards.BuildReport();
        BattlegroundImportReport battlegroundReport = battlegrounds.BuildReport();
        (IReadOnlyCollection<RelayScriptRow> relaySteps, IReadOnlyCollection<RelayScriptTemplateRow> relayTemplates) = relays.RelaySnapshot();
        warnings.AddRange(graveyardReport.Warnings);
        warnings.AddRange(battlegroundReport.Warnings);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        void Count(string table, long rows, bool present)
        {
            if (present)
            {
                counts[table] = rows;
            }
        }

        Count(GraveyardDataModule.SafeLocsTable, graveyardReport.SafeLocs, graveyards.HasRows);
        Count(GraveyardDataModule.GraveyardZoneTable, graveyardReport.Links, graveyards.HasRows);
        bool anyBattleground = battlegroundReport.Templates + battlegroundReport.CreatureEvents + battlegroundReport.GameObjectEvents + battlegroundReport.Battlemasters > 0;
        Count(BattlegroundWorldDataModule.TemplateTable, battlegroundReport.Templates, anyBattleground);
        Count(BattlegroundWorldDataModule.CreatureEventTable, battlegroundReport.CreatureEvents, anyBattleground);
        Count(BattlegroundWorldDataModule.GameObjectEventTable, battlegroundReport.GameObjectEvents, anyBattleground);
        Count(BattlegroundWorldDataModule.BattlemasterTable, battlegroundReport.Battlemasters, anyBattleground);
        Count("exploration_basexp", worldState.BaseXp.Count, worldState.BaseXp.Count > 0);
        Count("game_weather", worldState.Weather.Count, worldState.BaseXp.Count > 0 || worldState.Weather.Count > 0);
        Count(AreaTriggerTavernDataModule.Table, taverns.Ids.Count, taverns.SawTable);
        Count(TransportWorldDataModule.Table, transports.Rows.Count, transports.SawTable);
        Count("gameobject_template (type 15)", transports.Ships.Count, transports.Ships.Count > 0);
        Count(SpellProcEventDataModule.Table, procs?.Content.Count ?? 0, procs is not null);
        Count("dbscripts_on_relay", relaySteps.Count, relaySteps.Count + relayTemplates.Count > 0);
        Count("dbscript_relay_template", relayTemplates.Count, relaySteps.Count + relayTemplates.Count > 0);
        Count("creature_template (ScriptName)", relays.ScriptNames.Count, relays.ScriptNames.Count > 0);
        (int scriptDevTexts, int scriptDevPoints) = relays.ScriptDevContentCounts();
        Count("creature_ai_texts (script_texts, gossip_texts)", scriptDevTexts, scriptDevTexts > 0);
        Count("creature_movement_template (script_waypoint, waypoint_path)", scriptDevPoints, scriptDevPoints > 0);
        foreach ((string table, int rows) in dbScripts.Counts)
        {
            Count(table, rows, dbScripts.HasRows);
        }

        Count("quest_template (StartScript/CompleteScript)", dbScripts.QuestScripts.Count, dbScripts.QuestScripts.Count > 0);
        Count("gossip_menu (script_id)", dbScripts.MenuScripts.Count, dbScripts.MenuScripts.Count > 0);
        Count("gossip_menu_option (action_script_id)", scriptedOptions.Count, scriptedOptions.Count > 0);
        Count(MapDataModule.AreaTriggerTemplateTable, triggers?.Count ?? 0, triggers is not null);
        Count(MapDataModule.MapTemplateTable, mapTables?.Maps.Count ?? 0, mapTables is not null);
        Count(MapDataModule.AreaTemplateTable, mapTables?.Areas.Count ?? 0, mapTables is not null);
        Count("taxi_nodes", taxiNodes?.Count ?? 0, taxiNodes is not null);
        Count("taxi_path", taxiPaths?.Count ?? 0, taxiPaths is not null);
        GameEventImportReport eventReport = gameEvents.BuildReport();
        bool anyEvents = eventReport.Events > 0;
        string[] eventTables = [GameEventDataModule.EventTable, GameEventDataModule.TimeTable, GameEventDataModule.CreatureTable,
            GameEventDataModule.GameObjectTable, GameEventDataModule.CreatureDataTable, GameEventDataModule.QuestTable, GameEventDataModule.MailTable];
        int[] eventRows = [eventReport.Events, eventReport.Times, eventReport.Creatures, eventReport.GameObjects, eventReport.CreatureData,
            eventReport.Quests, eventReport.Mails];
        for (int i = 0; i < eventTables.Length; i++)
        {
            Count(eventTables[i], eventRows[i], anyEvents);
        }

        if (anyEvents)
        {
            warnings.AddRange(eventReport.Warnings);
        }

        // creature_spawn_entry: the entries a spawn whose creature.id is 0 becomes (cmangos; vmangos id2..id5). Worlds built by the Codex-line
        // importer have the table empty, so those spawns (2802 of classic-db z2815, 2234 of them with rows here) never appear. Filled only
        // when the world has no row at all, and only for the world's own spawns whose entry is 0 or one of the dump's entries for that guid,
        // so a world built from other data never gets a creature it did not have.
        IReadOnlyCollection<CreatureSpawnEntryRow> spawnEntries = relays.SpawnEntrySnapshot();
        Count(CreatureSpawnEntryTable, spawnEntries.Count, spawnEntries.Count > 0);

        if (procs is not null && procs.RowsFilteredByBuild > 0)
        {
            warnings.Add($"spell_proc_event: {procs.RowsFilteredByBuild} row(s) outside build {SpellProcEventDumpImporter.SupportedBuild} dropped");
        }

        List<string> checks = [];
        if (!dryRun)
        {
            o.WriteLine($"target: {target!.Describe}");
            try
            {
                await using WorldDbContext db = OpenWorld(target);
                await EnsureRefreshSchemaAsync(db, migrate, o, ct).ConfigureAwait(false);
                await ImportTransaction.RunAsync(db, async token =>
                {
                    if (graveyards.HasRows)
                    {
                        await graveyards.WriteAsync(db, replace: true, token).ConfigureAwait(false);
                    }

                    if (anyBattleground)
                    {
                        await battlegrounds.WriteAsync(db, replace: true, token).ConfigureAwait(false);
                    }

                    if (worldState.BaseXp.Count > 0 || worldState.Weather.Count > 0)
                    {
                        await WorldStateDumpImporter.WriteAsync(db, worldState, replace: true, token).ConfigureAwait(false);
                    }

                    await taverns.ReplaceAsync(db, token).ConfigureAwait(false);
                    await transports.ReplaceAsync(db, token).ConfigureAwait(false);
                    await transports.ReplaceShipTemplatesAsync(db, token).ConfigureAwait(false);
                    await relays.ReplaceRelayScriptsAsync(db, token).ConfigureAwait(false);
                    await relays.RefreshScriptNamesAsync(db, token).ConfigureAwait(false);
                    await relays.ReplaceScriptDevContentAsync(db, token).ConfigureAwait(false);
                    await dbScripts.ReplaceAsync(db, token).ConfigureAwait(false);
                    await UpsertScriptedGossipOptionsAsync(db, scriptedOptions, token).ConfigureAwait(false);
                    if (procs is not null)
                    {
                        await db.Set<SpellProcEventRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                        await ImportBatch.InsertAsync(db, procs.Content.Rows.OrderBy(r => r.Entry).Select(SpellProcEventDataModule.ToRow), token).ConfigureAwait(false);
                    }

                    if (triggers is not null)
                    {
                        await db.Set<AreaTriggerTemplateRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                        await ImportBatch.InsertAsync(db, triggers, token).ConfigureAwait(false);
                    }

                    if (mapTables is not null)
                    {
                        await MapAreaDbcImporter.ReplaceAsync(db, mapTables, token).ConfigureAwait(false);
                    }

                    if (taxiNodes is not null)
                    {
                        await db.Set<TaxiNode>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                        await ImportBatch.InsertAsync(db, taxiNodes, token).ConfigureAwait(false);
                    }

                    if (taxiPaths is not null)
                    {
                        await db.Set<TaxiPath>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                        await ImportBatch.InsertAsync(db, taxiPaths, token).ConfigureAwait(false);
                    }

                    if (spawnEntries.Count > 0)
                    {
                        int? written = await FillSpawnEntriesAsync(db, spawnEntries, token).ConfigureAwait(false);
                        if (written is { } rows)
                        {
                            counts[CreatureSpawnEntryTable] = rows;
                        }
                        else
                        {
                            counts.Remove(CreatureSpawnEntryTable);
                            o.WriteLine($"  {CreatureSpawnEntryTable}: the world has rows already; left as it is");
                        }
                    }

                    if (anyEvents)
                    {
                        int existing = await db.Set<GameEventRow>().CountAsync(token).ConfigureAwait(false);
                        if (existing == 0)
                        {
                            await gameEvents.WriteAsync(db, replace: true, token).ConfigureAwait(false);
                        }
                        else
                        {
                            foreach (string table in eventTables)
                            {
                                counts.Remove(table);
                            }

                            o.WriteLine($"  game_event: the world has {existing.ToString(CultureInfo.InvariantCulture)} event(s) already; the seven game-event tables are left as they are");
                        }
                    }
                }, ct).ConfigureAwait(false);

                checks = await RefreshChecksAsync(db, taxiPathNodes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or CliException))
            {
                throw DatabaseError(ex, target);
            }
        }

        o.WriteLine(dryRun ? "would replace:" : "replaced:");
        foreach ((string table, long rows) in counts)
        {
            o.WriteLine($"  {table}  {rows.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (string check in checks)
        {
            o.WriteLine($"check: {check}");
        }

        if (!dryRun)
        {
            o.WriteLine(checks.Count == 0 ? "refresh: every checked reference resolves" : $"refresh: {checks.Count} reference check(s) to look at");
        }

        warnings.AddRange(checks);
        PrintWarnings(o, warnings);
        WriteReport(reportPath, ContentImportReport.Create("refresh", files, scan, warnings, dryRun) with { Imported = counts });
        return ExitCodes.Ok;
    }

    private const string CreatureSpawnEntryTable = "creature_spawn_entry";

    /// <summary>
    /// Fill an empty <c>creature_spawn_entry</c> from the dump's rows: only for spawns the world has, and only when the world's spawn has entry
    /// 0 (it needs the rows to appear at all) or an entry among the dump's for that guid (the same spawn). Returns the rows written, or null
    /// when the world already has rows (nothing is touched).
    /// </summary>
    internal static async Task<int?> FillSpawnEntriesAsync(WorldDbContext db, IReadOnlyCollection<CreatureSpawnEntryRow> dumpRows, CancellationToken ct)
    {
        if (await db.Set<CreatureSpawnEntryRow>().AnyAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        Dictionary<uint, uint> worldEntries = await db.Set<CreatureSpawnRow>().AsNoTracking()
            .ToDictionaryAsync(r => r.Guid, r => r.Entry, ct).ConfigureAwait(false);
        CreatureSpawnEntryRow[] rows = [.. dumpRows
            .GroupBy(r => r.SpawnGuid)
            .Where(g => worldEntries.TryGetValue(g.Key, out uint entry) && (entry == 0 || g.Any(r => r.Entry == entry)))
            .SelectMany(g => g)
            .OrderBy(r => r.SpawnGuid).ThenBy(r => r.Entry)
            .Select(r => new CreatureSpawnEntryRow { SpawnGuid = r.SpawnGuid, Entry = r.Entry })];
        await ImportBatch.InsertAsync(db, rows, ct).ConfigureAwait(false);
        return rows.Length;
    }

    /// <summary>
    /// The gossip options that run a <c>dbscripts_on_gossip</c> script: an existing row (menu_id, id) takes the dump's
    /// <c>action_script_id</c>; a missing one (importers before world schema 42 skipped them) is added as the dump has it. A second run
    /// changes nothing.
    /// </summary>
    private static async Task UpsertScriptedGossipOptionsAsync(WorldDbContext db, IReadOnlyCollection<GossipMenuOption> options, CancellationToken ct)
    {
        foreach (GossipMenuOption option in options)
        {
            int updated = await db.Set<GossipMenuOption>().Where(o => o.MenuId == option.MenuId && o.Id == option.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ActionScriptId, option.ActionScriptId), ct).ConfigureAwait(false);
            if (updated == 0)
            {
                db.Add(option);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// The refresh's schema gate: a world already at this importer's schema passes; one behind it is refused (nothing written) unless
    /// <paramref name="migrate"/>, in which case it is upgraded first and the step is reported. Built together with a lane that raised the
    /// world schema, a silent upgrade here would migrate the live world before the server that needs it is deployed.
    /// </summary>
    private static async Task EnsureRefreshSchemaAsync(WorldDbContext db, bool migrate, TextWriter o, CancellationToken ct)
    {
        SchemaPlan before = await SchemaPlanner.PlanAsync(db, WorldDbContext.Schema, cancellationToken: ct).ConfigureAwait(false);
        try
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema,
                new SchemaUpgradeOptions { Policy = migrate ? SchemaPolicy.Always : SchemaPolicy.Never }, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (SchemaPolicyException ex)
        {
            string have = ex.DatabaseVersion is { } version ? $"schema version {version}" : $"no complete schema ({ex.State})";
            throw new CliException(ExitCodes.Schema,
                $"the world database has {have}, this importer's world schema is {ex.CodeVersion}; refresh does not migrate on its own. Start the " +
                "world server from the build this importer belongs to once (it migrates at start) or pass --migrate. Nothing was changed.", ex);
        }

        if (before.DatabaseVersion is { } from && from != before.CodeVersion && migrate)
        {
            o.WriteLine($"world schema {from} -> {before.CodeVersion} (--migrate)");
        }
        else
        {
            o.WriteLine($"world schema {before.DatabaseVersion?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        }
    }

    /// <summary>
    /// The cooldown unit of <c>spell_proc_event</c>: given, or (auto) read from the classic-db <c>db_version</c> text ("For Classic core
    /// zNNNN"): seconds before z2829, milliseconds from it on. A dump whose revision cannot be read needs an explicit unit (fail closed:
    /// guessing wrong makes every proc cooldown a thousand times off).
    /// </summary>
    internal static ProcCooldownUnit ResolveCooldownUnit(string? given, string? dbVersion)
    {
        switch ((given ?? "auto").ToLowerInvariant())
        {
            case "ms" or "milliseconds":
                return ProcCooldownUnit.Milliseconds;
            case "s" or "seconds":
                return ProcCooldownUnit.Seconds;
            case "auto":
                Match match = dbVersion is null ? Match.Empty : ClassicCoreRevision().Match(dbVersion);
                if (!match.Success)
                {
                    throw new UsageException("the dump's db_version names no classic-db core revision; give --cooldown-unit ms or seconds for spell_proc_event");
                }

                int revision = int.Parse(match.Groups["rev"].Value, CultureInfo.InvariantCulture);
                return revision < ProcCooldownMillisecondsFromCore ? ProcCooldownUnit.Seconds : ProcCooldownUnit.Milliseconds;
            default:
                throw new UsageException($"unknown --cooldown-unit '{given}' (auto, ms, seconds)");
        }
    }

    private static T ReadDbc<T>(string name, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (InvalidDataException ex)
        {
            throw new CliException(ExitCodes.Schema, $"{name}: {ex.Message}", ex);
        }
    }

    /// <summary>The references the world checks at start, read back after the write.</summary>
    private static async Task<List<string>> RefreshChecksAsync(WorldDbContext db, TaxiPathNodeCatalog? taxiPathNodes, CancellationToken ct)
    {
        var checks = new List<string>();
        IQueryable<AreaTriggerTemplateRow> triggers = db.Set<AreaTriggerTemplateRow>();
        List<uint> teleports = await db.Set<AreaTriggerTeleportRow>().Where(t => !triggers.Any(r => r.Id == t.Id)).Select(t => t.Id)
            .OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
        if (teleports.Count > 0)
        {
            checks.Add($"{teleports.Count} areatrigger_teleport row(s) have no areatrigger_template row: {Ids(teleports)}");
        }

        List<uint> inns = await db.Set<AreaTriggerTavernRow>().Where(t => !triggers.Any(r => r.Id == t.Id)).Select(t => t.Id)
            .OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
        if (inns.Count > 0)
        {
            checks.Add($"{inns.Count} areatrigger_tavern row(s) have no areatrigger_template row: {Ids(inns)}");
        }

        // vmangos ObjectMgr::LoadAreaTriggerTeleports skips a portal whose target map is not in sMapStorage ("unknown target map"); with
        // an empty map_template the world knows only the two continents (MapRegistry).
        IQueryable<MapTemplateRow> maps = db.Set<MapTemplateRow>();
        bool anyMap = await maps.AnyAsync(ct).ConfigureAwait(false);
        IQueryable<AreaTriggerTeleportRow> unknownTargets = anyMap
            ? db.Set<AreaTriggerTeleportRow>().Where(t => !maps.Any(m => m.Entry == t.TargetMap))
            : db.Set<AreaTriggerTeleportRow>().Where(t => t.TargetMap > 1);
        List<(uint Id, uint Map)> portals = (await unknownTargets
                .OrderBy(t => t.Id).Select(t => new { t.Id, t.TargetMap }).ToListAsync(ct).ConfigureAwait(false))
            .Select(t => (t.Id, t.TargetMap)).ToList();
        if (portals.Count > 0)
        {
            checks.Add($"{portals.Count} areatrigger_teleport row(s) lead to a map with no map_template row: " +
                string.Join(", ", portals.Take(20).Select(p => $"{p.Id} (map {p.Map})")) + (portals.Count > 20 ? $" ... ({portals.Count - 20} more)" : string.Empty));
        }

        // vmangos ObjectMgr::LoadGraveyardZones skips a link whose zone is not in the area table ("not existing zone id"); the world checks
        // it only when it has area data (GraveyardCatalog.Build).
        IQueryable<AreaTemplateRow> areas = db.Set<AreaTemplateRow>();
        if (await areas.AnyAsync(ct).ConfigureAwait(false))
        {
            List<uint> zones = await db.Set<GraveyardZoneRow>().Where(g => !areas.Any(a => a.Entry == g.GhostZone)).Select(g => g.GhostZone)
                .Distinct().OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
            if (zones.Count > 0)
            {
                checks.Add($"{zones.Count} game_graveyard_zone row(s) name a zone with no area_template row: {Ids(zones)}");
            }
        }

        IQueryable<WorldSafeLocRow> locs = db.Set<WorldSafeLocRow>();
        List<uint> battlegrounds = await db.Set<BattlegroundTemplateRow>()
            .Where(b => !locs.Any(l => l.Id == b.AllianceStartLoc) || !locs.Any(l => l.Id == b.HordeStartLoc)).Select(b => b.Id)
            .OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
        if (battlegrounds.Count > 0)
        {
            checks.Add($"battleground_template {Ids(battlegrounds)}: a start location is not a world_safe_locs id");
        }

        IQueryable<GameObjectTemplateRow> objects = db.Set<GameObjectTemplateRow>();
        List<uint> ships = await db.Set<TransportRow>().Where(t => !objects.Any(g => g.Entry == t.Entry && g.Type == 15)).Select(t => t.Entry)
            .Distinct().OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
        if (ships.Count > 0)
        {
            checks.Add($"{ships.Count} transports row(s) name no gameobject_template of type 15: {Ids(ships)}");
        }

        if (taxiPathNodes is not null)
        {
            checks.AddRange(await ShipRouteChecksAsync(db, taxiPathNodes, ct).ConfigureAwait(false));
        }

        return checks;

        static string Ids(List<uint> ids) => string.Join(", ", ids.Take(20)) + (ids.Count > 20 ? $" ... ({ids.Count - 20} more)" : string.Empty);
    }

    /// <summary>
    /// The ships the world would refuse at start (World <c>TransportFeature</c>, vmangos <c>TransportMgr::LoadTransportTemplates</c>): a type 15
    /// template whose speed or acceleration (data1, data2) is not positive, whose TaxiPathNode.dbc path (data0) has fewer than the three nodes
    /// a spline needs, or whose path touches a map without a <c>map_template</c> row.
    /// </summary>
    private static async Task<List<string>> ShipRouteChecksAsync(WorldDbContext db, TaxiPathNodeCatalog taxiPathNodes, CancellationToken ct)
    {
        var checks = new List<string>();
        HashSet<uint> maps = [.. await db.Set<MapTemplateRow>().Select(m => m.Entry).ToListAsync(ct).ConfigureAwait(false)];
        List<GameObjectTemplateRow> ships = await db.Set<GameObjectTemplateRow>().AsNoTracking().Where(g => g.Type == TransportDumpImporter.ShipType)
            .OrderBy(g => g.Entry).ToListAsync(ct).ConfigureAwait(false);
        foreach (GameObjectTemplateRow ship in ships)
        {
            string name = $"transport {ship.Entry.ToString(CultureInfo.InvariantCulture)} ({ship.Name})";
            if (ship.Data1 == 0 || ship.Data2 == 0)
            {
                checks.Add($"{name}: speed {ship.Data1.ToString(CultureInfo.InvariantCulture)} yd/s and acceleration {ship.Data2.ToString(CultureInfo.InvariantCulture)} yd/s\u00b2 (data1, data2) must both be positive");
                continue;
            }

            IReadOnlyList<TaxiPathNodeRecord> nodes = taxiPathNodes.Nodes(ship.Data0);
            if (nodes.Count < 3)
            {
                checks.Add($"{name}: path {ship.Data0.ToString(CultureInfo.InvariantCulture)} has {nodes.Count.ToString(CultureInfo.InvariantCulture)} TaxiPathNode.dbc row(s), a route needs at least 3");
                continue;
            }

            uint[] missing = [.. nodes.Select(n => n.MapId).Distinct().Where(m => !maps.Contains(m)).Order()];
            if (missing.Length > 0)
            {
                checks.Add($"{name}: path {ship.Data0.ToString(CultureInfo.InvariantCulture)} sails on map {string.Join(", ", missing)}, which has no map_template row");
            }
        }

        return checks;
    }
}
