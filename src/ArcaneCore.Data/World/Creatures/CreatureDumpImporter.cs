using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>Which world-database layout a dump uses (detected from its column names).</summary>
public enum CreatureDumpDialect
{
    Unknown,

    /// <summary>cmangos classic-db: <c>creature_template.Entry/MinLevel/…</c> (mangos.sql).</summary>
    CMangos,

    /// <summary>vmangos world db: <c>creature_template.entry/level_min/…</c>, patch-versioned rows.</summary>
    VMangos,
}

/// <summary>What an import read and wrote.</summary>
public sealed record CreatureImportReport(
    CreatureDumpDialect Dialect,
    int Templates,
    int Spawns,
    int Waypoints,
    int Models,
    int Addons,
    int SkippedSpawns,
    IReadOnlyList<string> Warnings)
{
    /// <summary>cmangos <c>creature_ai_scripts</c> rows (EventAI).</summary>
    public int AiEvents { get; init; }

    /// <summary>cmangos <c>creature_ai_texts</c> rows.</summary>
    public int AiTexts { get; init; }

    /// <summary><c>broadcast_text</c> rows (the lines positive EventAI text ids refer to).</summary>
    public int BroadcastTexts { get; init; }

    /// <summary><c>creature_ai_summons</c> rows.</summary>
    public int AiSummons { get; init; }

    /// <summary><c>creature_spawn_entry</c> rows (the entries a spawn can become; vmangos <c>id2</c> ... <c>id5</c> included).</summary>
    public int SpawnEntries { get; init; }

    /// <summary><c>creature_movement_template</c> rows (the entry paths a spawn without its own path walks).</summary>
    public int MovementTemplates { get; init; }
    public int AiTextTemplates { get; init; }

    /// <summary><c>dbscripts_on_relay</c> rows (the relay DB scripts EventAI's START_RELAY_SCRIPT runs).</summary>
    public int RelayScriptSteps { get; init; }

    /// <summary>Type-1 (relay) rows of <c>dbscript_random_templates</c>.</summary>
    public int RelayScriptTemplates { get; init; }
}

/// <summary>
/// Maps the creature tables of a cmangos classic-db or vmangos world dump into ArcaneCore's
/// creature schema, by column <b>name</b> (ROADMAP § Content: the importer maps by name so
/// several sources can feed one schema). Read one or more dump files with <see cref="Read"/>,
/// then <see cref="WriteAsync"/> the result. The dumps are GPL data and are never committed.
/// <para>
/// cmangos columns come from mangos-classic <c>sql/base/mangos.sql</c>; vmangos columns from
/// <c>ObjectMgr::LoadCreatureTemplates / LoadCreatures</c>, <c>WaypointManager::Load</c> and
/// <c>LoadCreatureDisplayInfoAddon</c>. vmangos rows are patch-versioned: the row with the
/// highest <c>patch</c> (templates) or the rows whose <c>patch_min..patch_max</c> contains
/// <see cref="MaxPatch"/> (spawns) are used, like vmangos with <c>WowPatch</c> = 10 (1.12).
/// </para>
/// </summary>
public sealed class CreatureDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1 (vmangos <c>WOW_PATCH_112</c> = 10).</summary>
    public const int MaxPatch = 10;

    /// <summary>vmangos build filter for progressive tables (<c>SUPPORTED_CLIENT_BUILD</c> 5875).</summary>
    public const int ClientBuild = 5875;

    private readonly Dictionary<uint, (int Patch, CreatureTemplateRow Row, VMangosStats? Stats)> _templates = [];
    private readonly Dictionary<uint, CreatureSpawnRow> _spawns = [];
    private readonly Dictionary<(uint, uint), CreatureMovementRow> _movement = [];
    private readonly Dictionary<(uint Entry, uint PathId, uint Point), CreatureMovementTemplateRow> _movementTemplates = [];
    private readonly Dictionary<(uint Entry, uint PathId, uint Point), CreatureMovementTemplateRow> _scriptWaypoints = [];
    private readonly HashSet<(uint Owner, uint Path, uint Point)> _scriptedNodes = [];
    private readonly Dictionary<(uint SpawnGuid, uint Entry), CreatureSpawnEntryRow> _spawnEntries = [];
    private readonly Dictionary<uint, (int Build, CreatureModelInfoRow Row)> _models = [];
    private readonly Dictionary<uint, CreatureAddonRow> _addons = [];
    private readonly Dictionary<(byte Class, byte Level), ClassLevelStats> _classLevelStats = [];
    private readonly Dictionary<uint, CreatureAiScriptRow> _aiScripts = [];
    private readonly Dictionary<int, CreatureAiTextRow> _aiTexts = [];
    private readonly Dictionary<uint, BroadcastTextRow> _broadcastTexts = [];
    private readonly Dictionary<uint, CreatureAiSummonRow> _aiSummons = [];
    private readonly Dictionary<(uint, int), CreatureTextTemplateRow> _textTemplates = [];
    private readonly Dictionary<uint, List<RelayScriptRow>> _relaySteps = [];
    private readonly HashSet<uint> _relayIdsThisRead = [];
    private readonly Dictionary<(uint, uint), RelayScriptTemplateRow> _relayTemplates = [];
    private bool _warnedVMangosAiEvents;
    private readonly List<string> _warnings = [];
    private int _skippedSpawns;

    public CreatureDumpDialect Dialect { get; private set; }

    /// <summary>
    /// How template <c>ExtraFlags</c> / <c>flags_extra</c> are decoded. <see cref="CreatureExtraFlagsDialect.Unknown"/> (the
    /// default) detects it per row from the column name: <c>flags_extra</c> is vmangos, <c>ExtraFlags</c> is cmangos.
    /// Set it to force a dialect for a dump whose column names do not say.
    /// </summary>
    public CreatureExtraFlagsDialect ExtraFlagsDialect { get; set; }

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        _relayIdsThisRead.Clear();
        var reader = new MySqlDumpReader(dump);
        foreach (object item in reader.Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            switch (row.Table.ToLowerInvariant())
            {
                case "creature_template":
                    ReadTemplate(row);
                    break;
                case "creature":
                    ReadSpawn(row);
                    break;
                case "creature_movement":
                    ReadMovement(row);
                    break;
                case "creature_movement_template":
                    ReadMovementTemplate(row);
                    break;
                case "script_waypoint":
                    ReadMovementTemplate(row, scriptDev: true);
                    break;
                case "creature_spawn_entry":
                    ReadSpawnEntry(row);
                    break;
                case "creature_model_info":
                case "creature_display_info_addon":
                    ReadModel(row);
                    break;
                case "creature_addon":
                    ReadAddon(row);
                    break;
                case "creature_classlevelstats":
                    ReadClassLevelStats(row);
                    break;
                case "creature_ai_scripts" when row.Has("action1_type"):
                    ReadAiScript(row);
                    break;
                case "creature_ai_texts":
                    ReadAiText(row);
                    break;
                case "script_texts":
                    ReadAiText(row, scriptDev: true);
                    break;
                case "dbscript_random_templates":
                    if (U32(row, "type") == 0)
                    {
                        var choice = new CreatureTextTemplateRow { Id = U32(row, "id"), TargetId = Int(Get(row, "target_id")), Chance = U32(row, "chance") };
                        _textTemplates[(choice.Id, choice.TargetId)] = choice;
                    }
                    else if (U32(row, "type") == 1 && Int(Get(row, "target_id")) > 0)
                    {
                        // cmangos RELAY_TEMPLATE: target_id is a dbscripts_on_relay id.
                        var relay = new RelayScriptTemplateRow { Id = U32(row, "id"), RelayId = (uint)Int(Get(row, "target_id")), Chance = U32(row, "chance") };
                        _relayTemplates[(relay.Id, relay.RelayId)] = relay;
                    }
                    break;
                case "dbscripts_on_relay":
                    ReadRelayStep(row);
                    break;
                case "broadcast_text":
                    ReadBroadcastText(row);
                    break;
                case "creature_ai_summons":
                    ReadAiSummon(row);
                    break;
                case "creature_ai_events":
                    if (!_warnedVMangosAiEvents)
                    {
                        _warnedVMangosAiEvents = true;
                        Warn("vmangos creature_ai_events use generic script commands; they are not imported (cmangos EventAI rows are)");
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Write everything read so far atomically. With <paramref name="replace"/> the creature
    /// tables are emptied first. The context must have an empty change tracker so batching
    /// cannot save or detach unrelated caller state. An existing EF transaction is protected
    /// by an import savepoint and remains owned by the caller; otherwise this method owns the
    /// transaction. Ambient transactions without an EF transaction are not supported.
    /// </summary>
    public async Task<CreatureImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException("Creature imports require an empty change tracker; save caller changes and clear tracking, or use a dedicated context.");
        }

        IDbContextTransaction? callerTransaction = db.Database.CurrentTransaction;
        if (callerTransaction is null && System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Creature imports require an explicit EF transaction when a caller owns the transaction; ambient transactions are not supported.");
        }

        if (callerTransaction is { SupportsSavepoints: false })
        {
            throw new InvalidOperationException("The caller's transaction does not support savepoints; creature import cannot protect its existing work.");
        }

        FinishTemplates();
        await using IDbContextTransaction? ownedTransaction = callerTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        IDbContextTransaction transaction = callerTransaction ?? ownedTransaction!;
        string? savepoint = callerTransaction is not null ? "ArcaneCreatureImport_" + Guid.NewGuid().ToString("N") : null;
        if (savepoint is not null)
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
        }

        bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            if (replace)
            {
                await db.Set<CreatureAiScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureAiTextRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<BroadcastTextRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureAiSummonRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureTextTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<RelayScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<RelayScriptTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureAddonRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureMovementRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureMovementTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureSpawnEntryRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureSpawnRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureModelInfoRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertBatchedAsync(db, _templates.Values.Select(t => t.Row), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _models.Values.Select(m => m.Row), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _spawns.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _movement.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, EffectivePaths(), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _spawnEntries.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _addons.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _aiScripts.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _aiTexts.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _broadcastTexts.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _aiSummons.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _textTemplates.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _relaySteps.Values.SelectMany(rows => rows), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _relayTemplates.Values, cancellationToken).ConfigureAwait(false);

            if (savepoint is not null)
            {
                await transaction.ReleaseSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception importError)
        {
            try
            {
                // Cancellation interrupts the import, not the rollback that preserves the
                // previous content. Never roll back a transaction owned by the caller.
                if (savepoint is not null)
                {
                    await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                    await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Creature import and rollback failed; discard the context and transaction.", importError, rollbackError);
            }

            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }

        return BuildReport();
    }

    /// <summary>The rows that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<CreatureTemplateRow> Templates, IReadOnlyCollection<CreatureSpawnRow> Spawns,
        IReadOnlyCollection<CreatureMovementRow> Movement, IReadOnlyCollection<CreatureModelInfoRow> Models,
        IReadOnlyCollection<CreatureAddonRow> Addons) Snapshot()
    {
        FinishTemplates();
        return ([.. _templates.Values.Select(t => t.Row)], [.. _spawns.Values], [.. _movement.Values],
            [.. _models.Values.Select(m => m.Row)], [.. _addons.Values]);
    }

    private IReadOnlyList<string> ReportWarnings()
    {
        if (_scriptedNodes.Count == 0)
        {
            return _warnings;
        }

        return [.. _warnings, $"{_scriptedNodes.Count} waypoint node(s) carry a ScriptId; creature movement scripts are not executed, the nodes are walked without them"];
    }

    public CreatureImportReport BuildReport() => new(
        Dialect, _templates.Count, _spawns.Count, _movement.Count, _models.Count, _addons.Count, _skippedSpawns, [.. ReportWarnings()])
    {
        AiEvents = _aiScripts.Count,
        AiTexts = _aiTexts.Count,
        BroadcastTexts = _broadcastTexts.Count,
        AiSummons = _aiSummons.Count,
        MovementTemplates = EffectivePaths().Count(),
        SpawnEntries = _spawnEntries.Count,
        AiTextTemplates = _textTemplates.Count,
        RelayScriptSteps = _relaySteps.Values.Sum(rows => rows.Count),
        RelayScriptTemplates = _relayTemplates.Count,
    };

    /// <summary>The relay DB script rows and relay templates that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<RelayScriptRow> Steps, IReadOnlyCollection<RelayScriptTemplateRow> Templates) RelaySnapshot()
        => ([.. _relaySteps.Values.SelectMany(rows => rows)], [.. _relayTemplates.Values]);

    /// <summary>
    /// Replace only <c>dbscripts_on_relay</c> and <c>dbscript_relay_template</c> with the rows read (the content-importer's <c>refresh</c>:
    /// EventAI's START_RELAY_SCRIPT needs them, and the other creature tables stay as they are). Runs inside the caller's
    /// <see cref="Content.Import.ImportTransaction"/>. Nothing read: nothing is emptied. Returns the step and template counts written.
    /// </summary>
    public async Task<(int Steps, int Templates)> ReplaceRelayScriptsAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (_relaySteps.Count == 0 && _relayTemplates.Count == 0)
        {
            return (0, 0);
        }

        await db.Set<RelayScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<RelayScriptTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await InsertBatchedAsync(db, _relaySteps.Values.SelectMany(rows => rows), cancellationToken).ConfigureAwait(false);
        await InsertBatchedAsync(db, _relayTemplates.Values, cancellationToken).ConfigureAwait(false);
        return (_relaySteps.Values.Sum(rows => rows.Count), _relayTemplates.Count);
    }

    /// <summary>
    /// One <c>dbscripts_on_relay</c> row (cmangos mangos.sql column names). The table has no key: the rows of one id are kept in dump
    /// order (<see cref="RelayScriptRow.Ordinal"/>), and a later dump file that carries an id replaces every row of that id.
    /// </summary>
    private void ReadRelayStep(DumpRow row)
    {
        uint id = U32(row, "id");
        if (_relayIdsThisRead.Add(id) || !_relaySteps.ContainsKey(id))
        {
            _relaySteps[id] = [];
        }

        List<RelayScriptRow> rows = _relaySteps[id];
        rows.Add(new RelayScriptRow
        {
            Id = id,
            Ordinal = (uint)rows.Count,
            Delay = U32(row, "delay"),
            Priority = U32(row, "priority"),
            Command = U32(row, "command"),
            DataLong = U32(row, "datalong"),
            DataLong2 = U32(row, "datalong2"),
            DataLong3 = U32(row, "datalong3"),
            BuddyEntry = U32(row, "buddy_entry"),
            SearchRadius = U32(row, "search_radius"),
            DataFlags = U32(row, "data_flags"),
            DataInt = Int(Get(row, "dataint")),
            DataInt2 = Int(Get(row, "dataint2")),
            DataInt3 = Int(Get(row, "dataint3")),
            DataInt4 = Int(Get(row, "dataint4")),
            DataFloat = F32(row, 0f, "datafloat"),
            X = F32(row, 0f, "x"),
            Y = F32(row, 0f, "y"),
            Z = F32(row, 0f, "z"),
            O = F32(row, 0f, "o"),
            Speed = F32(row, 0f, "speed"),
            ConditionId = U32(row, "condition_id"),
        });
    }

    /// <summary>The spawn entry lists that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<CreatureSpawnEntryRow> SpawnEntrySnapshot() => [.. _spawnEntries.Values];

    /// <summary>The entry waypoint paths that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<CreatureMovementTemplateRow> PathSnapshot() => [.. EffectivePaths()];

    /// <summary>ScriptDev2 escort paths fill only entry/path pairs without a movement-template path; never blend two sources' points.</summary>
    private IEnumerable<CreatureMovementTemplateRow> EffectivePaths()
    {
        HashSet<(uint Entry, uint PathId)> explicitPaths = [.. _movementTemplates.Keys.Select(k => (k.Entry, k.PathId))];
        return _movementTemplates.Values.Concat(_scriptWaypoints.Values.Where(p => !explicitPaths.Contains((p.Entry, p.PathId))));
    }

    /// <summary>The EventAI rows that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<CreatureAiScriptRow> Scripts, IReadOnlyCollection<CreatureAiTextRow> Texts) AiSnapshot()
        => ([.. _aiScripts.Values], [.. _aiTexts.Values]);

    /// <summary>The broadcast texts and EventAI summon locations that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<BroadcastTextRow> BroadcastTexts, IReadOnlyCollection<CreatureAiSummonRow> Summons) BehaviourSnapshot()
        => ([.. _broadcastTexts.Values], [.. _aiSummons.Values]);

    private static async Task InsertBatchedAsync<T>(WorldDbContext db, IEnumerable<T> rows, CancellationToken ct)
        where T : class
    {
        const int BatchSize = 2000;
        int pending = 0;
        foreach (T row in rows)
        {
            db.Set<T>().Add(row);
            if (++pending == BatchSize)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    // --- creature_template --------------------------------------------------------------

    private void ReadTemplate(DumpRow row)
    {
        bool vmangos = row.Has("level_min");
        SetDialect(vmangos ? CreatureDumpDialect.VMangos : CreatureDumpDialect.CMangos);

        uint entry = U32(row, "Entry");
        int patch = vmangos ? (int)U32(row, "patch") : 0;
        if (patch > MaxPatch)
        {
            return;
        }

        if (_templates.TryGetValue(entry, out var existing) && existing.Patch > patch)
        {
            return;
        }

        var t = new CreatureTemplateRow
        {
            Entry = entry,
            Name = Str(row, "Name"),
            SubName = Str(row, "SubName", "subname"),
            MinLevel = U8(row, "MinLevel", "level_min"),
            MaxLevel = U8(row, "MaxLevel", "level_max"),
            DisplayId1 = U32(row, "DisplayId1", "ModelId1", "display_id1"),
            DisplayId2 = U32(row, "DisplayId2", "ModelId2", "display_id2"),
            DisplayId3 = U32(row, "DisplayId3", "ModelId3", "display_id3"),
            DisplayId4 = U32(row, "DisplayId4", "ModelId4", "display_id4"),
            DisplayProbability1 = U32(row, "DisplayIdProbability1", "display_probability1"),
            DisplayProbability2 = U32(row, "DisplayIdProbability2", "display_probability2"),
            DisplayProbability3 = U32(row, "DisplayIdProbability3", "display_probability3"),
            DisplayProbability4 = U32(row, "DisplayIdProbability4", "display_probability4"),
            Scale = F32(row, 0f, "Scale", "display_scale1"),
            DisplayScale2 = F32(row, 0f, "display_scale2"),
            DisplayScale3 = F32(row, 0f, "display_scale3"),
            DisplayScale4 = F32(row, 0f, "display_scale4"),
            Faction = U32(row, "Faction", "FactionAlliance", "faction"),
            NpcFlags = U32(row, "NpcFlags", "npc_flags"),
            GossipMenuId = U32(row, "GossipMenuId", "gossip_menu_id"),
            TrainerType = U32(row, "TrainerType", "trainer_type"),
            TrainerClass = U8(row, "TrainerClass", "trainer_class"),
            TrainerRace = U8(row, "TrainerRace", "trainer_race"),
            TrainerSpell = U32(row, "TrainerSpell", "trainer_spell"),
            UnitFlags = U32(row, "UnitFlags"),
            DynamicFlags = U32(row, "DynamicFlags"),
            TypeFlags = U32(row, "CreatureTypeFlags"),
            CreatureType = U32(row, "CreatureType", "type"),
            Family = U32(row, "Family", "pet_family"),
            Rank = U32(row, "Rank"),
            UnitClass = U8(row, "UnitClass", "unit_class"),
            InhabitType = row.TryGet(out _, "InhabitType", "inhabit_type") ? U8(row, "InhabitType", "inhabit_type") : (byte)3,
            Civilian = U32(row, "Civilian") != 0,
            RacialLeader = U32(row, "RacialLeader", "racial_leader") != 0,
            SpeedWalk = F32(row, 1.0f, "SpeedWalk", "speed_walk"),
            SpeedRun = F32(row, 1.14286f, "SpeedRun", "speed_run"),
            MinLevelHealth = U32(row, "MinLevelHealth"),
            MaxLevelHealth = U32(row, "MaxLevelHealth"),
            MinLevelMana = U32(row, "MinLevelMana"),
            MaxLevelMana = U32(row, "MaxLevelMana"),
            Armor = U32(row, "Armor"),
            MinMeleeDamage = F32(row, 0f, "MinMeleeDmg"),
            MaxMeleeDamage = F32(row, 0f, "MaxMeleeDmg"),
            MinRangedDamage = F32(row, 0f, "MinRangedDmg"),
            MaxRangedDamage = F32(row, 0f, "MaxRangedDmg"),
            MeleeAttackPower = U32(row, "MeleeAttackPower"),
            RangedAttackPower = U32(row, "RangedAttackPower"),
            MeleeBaseAttackTime = U32Or(row, 2000, "MeleeBaseAttackTime", "base_attack_time"),
            RangedBaseAttackTime = U32Or(row, 2000, "RangedBaseAttackTime", "ranged_attack_time"),
            DamageSchool = U32(row, "DamageSchool", "damage_school"),
            PetSpellDataId = U32(row, "PetSpellDataId", "pet_spell_list_id"),
            MovementType = U8(row, "MovementType", "movement_type"),
            CorpseDecaySeconds = U32(row, "CorpseDecay"),
            ExtraFlags = U32(row, "ExtraFlags", "flags_extra"),
            AIName = Truncate(Str(row, "AIName", "ai_name"), 64),

            // Behaviour columns: cmangos Detection/CallForHelp/Pursuit/Leash/Timeout (mangos.sql creature_template),
            // vmangos detection_range/call_for_help_range/leash_range (CreatureDefines.h:250-252). A missing
            // Detection column stays null so the content default (18) applies. A missing call-for-help column takes the
            // dialect's schema default: vmangos call_for_help_range DEFAULT '5' (sql/old_migrations/20190123062532_world.sql:28),
            // cmangos CallForHelp DEFAULT '0' (mangos.sql:1258).
            Detection = row.TryGet(out _, "Detection", "detection_range") ? F32(row, 18.0f, "Detection", "detection_range") : null,
            CallForHelp = F32(row, vmangos ? 5f : 0f, "CallForHelp", "call_for_help_range"),
            Pursuit = U32(row, "Pursuit"),
            Leash = F32(row, 0f, "Leash", "leash_range"),
            Timeout = U32(row, "Timeout"),
            StaticFlags1 = U32(row, "StaticFlags1", "static_flags1"),
            StaticFlags2 = U32(row, "StaticFlags2", "static_flags2"),
            ExtraFlagsDialect = (byte)ResolveExtraFlagsDialect(row, vmangos),
        };

        VMangosStats? stats = null;
        if (vmangos)
        {
            uint static1 = U32(row, "static_flags1");
            uint static2 = U32(row, "static_flags2");
            t.TypeFlags = VMangosTypeFlags(static1, static2);
            t.UnitFlags = VMangosUnitFlags(static1);
            stats = new VMangosStats(
                F32(row, 1f, "health_multiplier"), F32(row, 1f, "mana_multiplier"), F32(row, 1f, "armor_multiplier"),
                F32(row, 1f, "damage_multiplier"), F32(row, 0.14f, "damage_variance"));
        }

        _templates[entry] = (patch, t, stats);
    }

    private CreatureExtraFlagsDialect ResolveExtraFlagsDialect(DumpRow row, bool vmangosTemplate)
    {
        if (ExtraFlagsDialect != CreatureExtraFlagsDialect.Unknown)
        {
            return ExtraFlagsDialect;
        }

        if (row.Has("flags_extra"))
        {
            return CreatureExtraFlagsDialect.VMangos;
        }

        if (row.Has("ExtraFlags"))
        {
            return CreatureExtraFlagsDialect.CMangos;
        }

        return vmangosTemplate ? CreatureExtraFlagsDialect.VMangos : CreatureExtraFlagsDialect.CMangos;
    }

    /// <summary>vmangos CreatureInfo::GetTypeFlags — the client-visible subset of the static flags.</summary>
    internal static uint VMangosTypeFlags(uint static1, uint static2)
    {
        uint flags = 0;
        if ((static1 & 0x00000010) != 0) flags |= 0x01;  // TAMEABLE
        if ((static1 & 0x00200000) != 0) flags |= 0x02;  // VISIBLE_TO_GHOSTS
        if ((static1 & 0x00010000) != 0) flags |= 0x04;  // RAID_BOSS_MOB
        if ((static1 & 0x00800000) != 0) flags |= 0x08;  // DO_NOT_PLAY_WOUND_ANIM
        if ((static1 & 0x01000000) != 0) flags |= 0x10;  // NO_FACTION_TOOLTIP
        if ((static1 & 0x40000000) != 0) flags |= 0x20;  // MORE_AUDIBLE
        if ((static2 & 0x00000008) != 0) flags |= 0x40;  // NO_HARMFUL_VERTEX_COLORING
        return flags;
    }

    /// <summary>vmangos Creature::ToggleUnitFlagsFromStaticFlags — unit flags implied by static flags.</summary>
    internal static uint VMangosUnitFlags(uint static1)
    {
        uint flags = 0;
        if ((static1 & 0x00000020) != 0) flags |= 0x00000100; // IMMUNE_TO_PC -> UNIT_FLAG_IMMUNE_TO_PLAYER
        if ((static1 & 0x00000040) != 0) flags |= 0x00000200; // IMMUNE_TO_NPC -> UNIT_FLAG_IMMUNE_TO_NPC
        if ((static1 & 0x00000200) != 0) flags |= 0x02000000; // UNINTERACTIBLE -> UNIT_FLAG_NOT_SELECTABLE
        if ((static1 & 0x10000000) != 0) flags |= 0x00008000; // CAN_SWIM -> UNIT_FLAG_USE_SWIM_ANIMATION
        return flags;
    }

    /// <summary>
    /// vmangos keeps health/mana/armor/damage in <c>creature_classlevelstats</c> × per-template
    /// multipliers (Creature::InitStatsForLevel); ArcaneCore stores the resulting per-level
    /// values like cmangos' MinLevelHealth/MaxLevelHealth columns.
    /// </summary>
    private void FinishTemplates()
    {
        foreach (uint entry in _templates.Keys.ToArray())
        {
            (int patch, CreatureTemplateRow t, VMangosStats? stats) = _templates[entry];
            if (stats is null)
            {
                continue;
            }

            byte cls = t.UnitClass == 0 ? (byte)1 : t.UnitClass;
            if (_classLevelStats.TryGetValue((cls, t.MinLevel), out ClassLevelStats min)
                && _classLevelStats.TryGetValue((cls, t.MaxLevel), out ClassLevelStats max))
            {
                // health = max(1, round(cls.health * health_multiplier)); mana = round(cls.mana * mana_multiplier)
                t.MinLevelHealth = Math.Max(1u, (uint)MathF.Round(min.Health * stats.HealthMultiplier));
                t.MaxLevelHealth = Math.Max(1u, (uint)MathF.Round(max.Health * stats.HealthMultiplier));
                t.MinLevelMana = (uint)MathF.Round(min.Mana * stats.ManaMultiplier);
                t.MaxLevelMana = (uint)MathF.Round(max.Mana * stats.ManaMultiplier);
                t.Armor = (uint)MathF.Round(max.Armor * stats.ArmorMultiplier);

                // average ± average × variance (InitStatsForLevel)
                float melee = max.MeleeDamage * stats.DamageMultiplier;
                float ranged = max.RangedDamage * stats.DamageMultiplier;
                t.MinMeleeDamage = melee - (melee * stats.DamageVariance);
                t.MaxMeleeDamage = melee + (melee * stats.DamageVariance);
                t.MinRangedDamage = ranged - (ranged * stats.DamageVariance);
                t.MaxRangedDamage = ranged + (ranged * stats.DamageVariance);
                t.MeleeAttackPower = (uint)Math.Max(0, max.AttackPower);
                t.RangedAttackPower = (uint)Math.Max(0, max.RangedAttackPower);
            }
            else
            {
                t.MinLevelHealth = Math.Max(t.MinLevelHealth, 1);
                t.MaxLevelHealth = Math.Max(t.MaxLevelHealth, 1);
                Warn($"creature_template {entry}: no creature_classlevelstats for class {cls} levels {t.MinLevel}-{t.MaxLevel}; health left at 1");
            }

            _templates[entry] = (patch, t, null);
        }
    }

    private void ReadClassLevelStats(DumpRow row)
    {
        var key = (U8(row, "class"), U8(row, "level"));
        _classLevelStats[key] = new ClassLevelStats(
            F32(row, 0f, "health"), F32(row, 0f, "mana"), F32(row, 0f, "armor"),
            F32(row, 0f, "melee_damage"), F32(row, 0f, "ranged_damage"),
            (int)F32(row, 0f, "attack_power"), (int)F32(row, 0f, "ranged_attack_power"));
    }

    // --- creature ------------------------------------------------------------------------

    private void ReadSpawn(DumpRow row)
    {
        if (row.TryGet(out string? patchMin, "patch_min") && row.TryGet(out string? patchMax, "patch_max"))
        {
            if (Int(patchMin) > MaxPatch || Int(patchMax) < MaxPatch)
            {
                _skippedSpawns++;
                return;
            }
        }

        uint min = row.TryGet(out _, "spawntimesecsmin") ? U32(row, "spawntimesecsmin") : U32Or(row, 120, "spawntimesecs");
        uint max = row.TryGet(out _, "spawntimesecsmax") ? U32(row, "spawntimesecsmax") : min;

        // vmangos spells the alternatives id2 ... id5 (CreatureData::creature_id[5]); the spawn can become any non-zero one, id included.
        uint spawnGuid = U32(row, "guid");
        uint[] alternatives = [U32(row, "id2"), U32(row, "id3"), U32(row, "id4"), U32(row, "id5")];
        if (alternatives.Any(id => id != 0))
        {
            foreach (uint entry in new[] { U32(row, "id") }.Concat(alternatives).Where(id => id != 0))
            {
                _spawnEntries[(spawnGuid, entry)] = new CreatureSpawnEntryRow { SpawnGuid = spawnGuid, Entry = entry };
            }
        }

        var spawn = new CreatureSpawnRow
        {
            Guid = U32(row, "guid"),
            Entry = U32(row, "id"),
            MapId = U32(row, "map"),
            X = F32(row, 0f, "position_x"),
            Y = F32(row, 0f, "position_y"),
            Z = F32(row, 0f, "position_z"),
            Orientation = F32(row, 0f, "orientation"),
            SpawnTimeMinSeconds = Math.Min(min, max),
            SpawnTimeMaxSeconds = Math.Max(min, max),
            WanderDistance = F32(row, 0f, "spawndist", "wander_distance"),
            MovementType = U8(row, "MovementType", "movement_type"),
        };
        _spawns[spawn.Guid] = spawn;
    }

    private void ReadMovement(DumpRow row)
    {
        var point = new CreatureMovementRow
        {
            SpawnGuid = U32(row, "Id"),
            Point = U32(row, "Point"),
            X = F32(row, 0f, "PositionX", "position_x"),
            Y = F32(row, 0f, "PositionY", "position_y"),
            Z = F32(row, 0f, "PositionZ", "position_z"),
            Orientation = F32(row, 0f, "Orientation"),
            WaitTimeMs = U32(row, "WaitTime"),
            Run = U32(row, "Run", "run") != 0,
        };
        _movement[(point.SpawnGuid, point.Point)] = point;
        NoteScript(point.SpawnGuid, 0, point.Point, row);
    }

    // cmangos creature_spawn_entry (guid, entry); the primary key makes a repeated pair one row.
    private void ReadSpawnEntry(DumpRow row)
    {
        var entry = new CreatureSpawnEntryRow { SpawnGuid = U32(row, "guid"), Entry = U32(row, "entry") };
        if (entry.Entry != 0)
        {
            _spawnEntries[(entry.SpawnGuid, entry.Entry)] = entry;
        }
    }

    private void ReadMovementTemplate(DumpRow row, bool scriptDev = false)
    {
        var point = new CreatureMovementTemplateRow
        {
            Entry = U32(row, "Entry"),
            PathId = U32(row, "PathId", "path_id"),
            Point = U32(row, "Point"),
            X = F32(row, 0f, "PositionX", "position_x"),
            Y = F32(row, 0f, "PositionY", "position_y"),
            Z = F32(row, 0f, "PositionZ", "position_z"),
            Orientation = F32(row, 0f, "Orientation"),
            WaitTimeMs = U32(row, "WaitTime", "waittime"),
        };
        (scriptDev ? _scriptWaypoints : _movementTemplates)[(point.Entry, point.PathId, point.Point)] = point;
        NoteScript(point.Entry, point.PathId + 1, point.Point, row);
    }

    // creature_movement scripts (dbscripts_on_creature_movement) are not run by ArcaneCore; count the nodes that carried one so the
    // loss is reported, not silent. The owner/path pair keeps a node of creature_movement apart from one of creature_movement_template.
    private void NoteScript(uint owner, uint path, uint point, DumpRow row)
    {
        if (U32(row, "ScriptId", "script_id") != 0)
        {
            _scriptedNodes.Add((owner, path, point));
        }
        else
        {
            _scriptedNodes.Remove((owner, path, point));
        }
    }

    private void ReadModel(DumpRow row)
    {
        int build = row.TryGet(out string? b, "build") && b is not null ? Int(b) : 0;
        if (build > ClientBuild)
        {
            return;
        }

        uint displayId = U32(row, "modelid", "display_id");
        if (_models.TryGetValue(displayId, out var existing) && existing.Build > build)
        {
            return;
        }

        _models[displayId] = (build, new CreatureModelInfoRow
        {
            DisplayId = displayId,
            BoundingRadius = F32(row, 0f, "bounding_radius"),
            CombatReach = F32(row, 0f, "combat_reach"),
            Gender = row.TryGet(out _, "gender") ? U8(row, "gender") : (byte)2,
            DisplayIdOtherGender = U32(row, "modelid_other_gender", "display_id_other_gender"),
        });
    }

    private void ReadAddon(DumpRow row)
    {
        var addon = new CreatureAddonRow
        {
            Guid = U32(row, "guid"),
            MountDisplayId = (uint)Math.Max(0, Int(Get(row, "mount", "mount_display_id"))),
            StandState = U8(row, "stand_state"),
            SheathState = U8(row, "sheath_state"),
            EmoteState = U32(row, "emote", "emote_state"),
        };
        _addons[addon.Guid] = addon;
    }

    // --- helpers ---------------------------------------------------------------------------

    private void SetDialect(CreatureDumpDialect dialect)
    {
        if (Dialect == CreatureDumpDialect.Unknown)
        {
            Dialect = dialect;
        }
        else if (Dialect != dialect)
        {
            throw new FormatException($"mixed dump dialects: {Dialect} and {dialect}");
        }
    }

    // --- creature_ai_scripts / creature_ai_texts / script_texts (cmangos-classic EventAI and ScriptDev2) ---

    private void ReadAiScript(DumpRow row)
    {
        // cmangos CreatureEventAIMgr.cpp:233-252: a positive creature_id is a template entry, a negative
        // one is a spawn guid (-creature_id).
        int key = Int(Get(row, "creature_id"));
        uint id = U32(row, "id");

        // event_chance is read as GetUInt8; 0 never triggers and above 100 is forced to 100 (cpp:266, 272-279).
        uint chance = U32(row, "event_chance");
        if (chance == 0)
        {
            Warn($"creature_ai_scripts {id}: 0 percent chance; the event never triggers");
        }
        else if (chance > 100)
        {
            Warn($"creature_ai_scripts {id}: chance {chance} is above 100; adjusted to 100");
            chance = 100;
        }

        // event_flags is read as GetUInt32 (cpp:267); the old byte column keeps the low byte.
        uint flags = U32(row, "event_flags");
        var script = new CreatureAiScriptRow
        {
            Id = id,
            CreatureId = key > 0 ? (uint)key : 0,
            CreatureGuid = key < 0 ? (uint)-(long)key : 0,
            EventType = U8(row, "event_type"),
            EventInversePhaseMask = U32(row, "event_inverse_phase_mask"),
            EventChance = (byte)chance,
            EventFlags = (byte)(flags & 0xFF),
            EventFlags32 = flags,
            EventParam1 = Int(Get(row, "event_param1")),
            EventParam2 = Int(Get(row, "event_param2")),
            EventParam3 = Int(Get(row, "event_param3")),
            EventParam4 = Int(Get(row, "event_param4")),
            EventParam5 = Int(Get(row, "event_param5")),
            EventParam6 = Int(Get(row, "event_param6")),
            Action1Type = U8(row, "action1_type"),
            Action1Param1 = Int(Get(row, "action1_param1")),
            Action1Param2 = Int(Get(row, "action1_param2")),
            Action1Param3 = Int(Get(row, "action1_param3")),
            Action2Type = U8(row, "action2_type"),
            Action2Param1 = Int(Get(row, "action2_param1")),
            Action2Param2 = Int(Get(row, "action2_param2")),
            Action2Param3 = Int(Get(row, "action2_param3")),
            Action3Type = U8(row, "action3_type"),
            Action3Param1 = Int(Get(row, "action3_param1")),
            Action3Param2 = Int(Get(row, "action3_param2")),
            Action3Param3 = Int(Get(row, "action3_param3")),
            Comment = Truncate(Str(row, "comment"), 255),
        };
        _aiScripts[script.Id] = script;
    }

    private void ReadAiText(DumpRow row, bool scriptDev = false)
    {
        var text = new CreatureAiTextRow
        {
            Entry = Int(Get(row, "entry")),
            Content = Str(row, "content_default"),
            Type = U8(row, "type"),
            Language = U32(row, "language"),
            Emote = U32(row, "emote"),
            Sound = U32(row, "sound"),
            BroadcastTextId = U32(row, "broadcast_text_id"),
        };
        if (text.Entry >= 0)
        {
            Warn($"{(scriptDev ? "script_texts" : "creature_ai_texts")} entry {text.Entry} is not negative; skipped");
            return;
        }

        if (scriptDev)
        {
            if (!_aiTexts.TryAdd(text.Entry, text))
                Warn($"script_texts entry {text.Entry} overlaps an earlier text; kept the earlier line");
        }
        else _aiTexts[text.Entry] = text;
    }

    // broadcast_text: the columns mangos-classic ObjectMgr::LoadBroadcastText reads (ObjectMgr.cpp:7786-7821).
    private void ReadBroadcastText(DumpRow row)
    {
        var text = new BroadcastTextRow
        {
            Id = U32(row, "Id"),
            Text = Truncate(Str(row, "Text"), 2000),
            FemaleText = Truncate(Str(row, "Text1"), 2000),
            ChatType = U8(row, "ChatTypeID"),
            Language = U8(row, "LanguageID"),
            SoundId = U32(row, "SoundEntriesID1"),
            EmoteId1 = U32(row, "EmoteID1"),
            EmoteId2 = U32(row, "EmoteID2"),
            EmoteId3 = U32(row, "EmoteID3"),
            EmoteDelay1 = U32(row, "EmoteDelay1"),
            EmoteDelay2 = U32(row, "EmoteDelay2"),
            EmoteDelay3 = U32(row, "EmoteDelay3"),
        };
        _broadcastTexts[text.Id] = text;
    }

    private void ReadAiSummon(DumpRow row)
    {
        var summon = new CreatureAiSummonRow
        {
            Id = U32(row, "id"),
            X = F32(row, 0f, "position_x"),
            Y = F32(row, 0f, "position_y"),
            Z = F32(row, 0f, "position_z"),
            Orientation = F32(row, 0f, "orientation"),
            SpawnTimeSeconds = U32Or(row, 120, "spawntimesecs"),
            Comment = Truncate(Str(row, "comment"), 255),
        };
        _aiSummons[summon.Id] = summon;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private void Warn(string message)
    {
        if (_warnings.Count < 1000)
        {
            _warnings.Add(message);
        }
    }

    private static string? Get(DumpRow row, params ReadOnlySpan<string> names)
        => row.TryGet(out string? value, names) ? value : null;

    private static string Str(DumpRow row, params ReadOnlySpan<string> names) => Get(row, names) ?? string.Empty;

    private static int Int(string? raw)
        => string.IsNullOrEmpty(raw) ? 0 : (int)double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static uint U32(DumpRow row, params ReadOnlySpan<string> names) => U32Or(row, 0, names);

    private static uint U32Or(DumpRow row, uint fallback, params ReadOnlySpan<string> names)
    {
        string? raw = Get(row, names);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        double value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        return value <= 0 ? 0 : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
    }

    private static byte U8(DumpRow row, params ReadOnlySpan<string> names) => (byte)Math.Min(U32(row, names), byte.MaxValue);

    private static float F32(DumpRow row, float fallback, params ReadOnlySpan<string> names)
    {
        string? raw = Get(row, names);
        return string.IsNullOrEmpty(raw) ? fallback : float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private sealed record VMangosStats(float HealthMultiplier, float ManaMultiplier, float ArmorMultiplier, float DamageMultiplier, float DamageVariance);

    private readonly record struct ClassLevelStats(
        float Health, float Mana, float Armor, float MeleeDamage, float RangedDamage, int AttackPower, int RangedAttackPower);
}
