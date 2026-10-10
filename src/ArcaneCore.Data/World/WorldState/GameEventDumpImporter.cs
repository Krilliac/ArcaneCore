using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.WorldState;

/// <summary>What a game-event import read (the counts are rows that would be, or were, written).</summary>
public sealed record GameEventImportReport(
    int Events, int Times, int Creatures, int GameObjects, int CreatureData, int Quests, int Mails, int SkippedRows, IReadOnlyList<string> Warnings)
{
    /// <summary><c>game_event_creature</c> rows dropped because their guid is not in the dump's own <c>creature</c> table.</summary>
    public int OrphanCreatureRows { get; init; }

    /// <summary><c>game_event_gameobject</c> rows dropped because their guid is not in the dump's own <c>gameobject</c> table.</summary>
    public int OrphanGameObjectRows { get; init; }
}

/// <summary>
/// Maps the game-event tables of a cmangos classic-db or vmangos dump onto <see cref="GameEventDataModule"/>'s rows BY COLUMN NAME
/// (taken from the dump's own <c>CREATE TABLE</c> or the INSERT's column list), so a different column order or extra
/// columns do not shift values, and both dialects are read:
/// <list type="bullet">
/// <item><c>game_event</c>: mangos-classic <c>schedule_type</c>, <c>linkedTo</c> (dates in <c>game_event_time</c>) or vmangos
/// <c>start_time</c>, <c>end_time</c>, <c>hardcoded</c>, <c>disabled</c>, <c>patch_min</c>, <c>patch_max</c>
/// (GameEventMgr.cpp:183). A vmangos table has no schedule type, so its rows are date events (type 1).</item>
/// <item><c>game_event_creature_data</c>: mangos-classic <c>modelid</c>, vmangos <c>display_id</c>; vmangos rows are patch
/// versioned and the row with the highest <c>patch</c> not above 10 wins per (guid, event) (GameEventMgr.cpp creature data query).</item>
/// <item><c>game_event_quest</c>: vmangos rows with a <c>patch_min</c> above 10 are skipped (its loader's <c>WHERE patch_min &lt;= patch</c>).</item>
/// </list>
/// <para>
/// Orphan rows. When the dump being read also carries the <c>creature</c> (or <c>gameobject</c>) table, a <c>game_event_creature</c>
/// (or <c>game_event_gameobject</c>) row whose guid is not one of its spawns is dropped and counted (<see cref="GameEventImportReport.OrphanCreatureRows"/>,
/// <see cref="GameEventImportReport.OrphanGameObjectRows"/>): cmangos GameEventMgr::LoadFromDB skips such a row ("not found in `gameobject` table")
/// and classic-db's own update <c>Updates/4498_backport_errors.sql</c> deletes exactly these rows from the z2815 snapshot (33 creature
/// and 1126 gameobject rows: 969 guids that exist in no table, 157 that are creature spawns filed under gameobject).
/// A dump without the spawn table (an events-only file) keeps every row, since there is nothing to check it against.
/// </para> Parsing completes before the database is touched and the
/// write is one transaction, so a mangled dump changes nothing. Nothing is bundled: the operator points the importer at their own dump.
/// </summary>
public sealed class GameEventDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1 (shared with the other dump importers).</summary>
    public const int MaxPatch = CreatureDumpImporter.MaxPatch;

    private static readonly RowMapper<GameEventRow> s_events = new();
    private static readonly RowMapper<GameEventTimeRow> s_times = new();
    private static readonly RowMapper<GameEventCreatureRow> s_creatures = new();
    private static readonly RowMapper<GameEventGameObjectRow> s_gameObjects = new();
    private static readonly RowMapper<GameEventCreatureDataRow> s_creatureData = new(new Dictionary<string, string> { ["display_id"] = nameof(GameEventCreatureDataRow.ModelId) });
    private static readonly RowMapper<GameEventQuestRow> s_quests = new();
    private static readonly RowMapper<GameEventMailRow> s_mails = new();

    private readonly Dictionary<uint, GameEventRow> _events = [];
    private readonly Dictionary<uint, GameEventTimeRow> _times = [];
    private readonly Dictionary<(uint, int), GameEventCreatureRow> _creatures = [];
    private readonly Dictionary<(uint, int), GameEventGameObjectRow> _gameObjects = [];
    private readonly Dictionary<(uint, int), (int Patch, GameEventCreatureDataRow Row)> _creatureData = [];
    private readonly Dictionary<(uint, int), GameEventQuestRow> _quests = [];
    private readonly Dictionary<(int, uint, uint), GameEventMailRow> _mails = [];
    private readonly MapDiagnostics _diagnostics = new();
    private readonly HashSet<uint> _creatureSpawns = [];
    private readonly HashSet<uint> _gameObjectSpawns = [];
    private bool _sawCreatureSpawns;
    private bool _sawGameObjectSpawns;
    private int _skipped;

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            switch (row.Table.ToLowerInvariant())
            {
                case GameEventDataModule.EventTable:
                    Require(row, "entry", "occurence", "length");
                    GameEventRow gameEvent = s_events.Map(row, _diagnostics);
                    if (!row.Has("schedule_type"))
                    {
                        gameEvent.ScheduleType = 1; // vmangos: every event is a date event
                    }

                    _events[gameEvent.Entry] = gameEvent;
                    break;
                case GameEventDataModule.TimeTable:
                    Require(row, "entry", "start_time", "end_time");
                    GameEventTimeRow time = s_times.Map(row, _diagnostics);
                    _times[time.Entry] = time;
                    break;
                case GameEventDataModule.CreatureTable:
                    Require(row, "guid", "event");
                    GameEventCreatureRow creature = s_creatures.Map(row, _diagnostics);
                    _creatures[(creature.Guid, creature.Event)] = creature;
                    break;
                case GameEventDataModule.GameObjectTable:
                    Require(row, "guid", "event");
                    GameEventGameObjectRow gameObject = s_gameObjects.Map(row, _diagnostics);
                    _gameObjects[(gameObject.Guid, gameObject.Event)] = gameObject;
                    break;
                case GameEventDataModule.CreatureDataTable:
                    ReadCreatureData(row);
                    break;
                case GameEventDataModule.QuestTable:
                    ReadQuest(row);
                    break;
                case "creature":
                    if (SpawnGuid(row) is { } creatureGuid)
                    {
                        _sawCreatureSpawns = true;
                        _creatureSpawns.Add(creatureGuid);
                    }

                    break;
                case "gameobject":
                    if (SpawnGuid(row) is { } objectGuid)
                    {
                        _sawGameObjectSpawns = true;
                        _gameObjectSpawns.Add(objectGuid);
                    }

                    break;
                case GameEventDataModule.MailTable:
                    Require(row, "event");
                    GameEventMailRow mail = s_mails.Map(row, _diagnostics);
                    _mails[(mail.Event, mail.RaceMask, mail.Quest)] = mail;
                    break;
            }
        }
    }

    /// <summary>The rows that would be written, as the world daemon would load them.</summary>
    public GameEventContent BuildContent() => new(
        [.. _events.Values.OrderBy(r => r.Entry).Select(r => new GameEventRecord(
            r.Entry, r.ScheduleType, r.Occurence, r.Length, r.Holiday, r.LinkedTo, r.Description ?? string.Empty,
            r.StartTime, r.EndTime, r.Hardcoded, r.Disabled, r.PatchMin, r.PatchMax))],
        [.. _times.Values.OrderBy(r => r.Entry).Select(r => new GameEventTimeRecord(r.Entry, r.StartTime, r.EndTime))],
        [.. KeptCreatures().OrderBy(r => r.Event).ThenBy(r => r.Guid).Select(r => new GameEventSpawnRecord(r.Guid, r.Event))],
        [.. KeptGameObjects().OrderBy(r => r.Event).ThenBy(r => r.Guid).Select(r => new GameEventSpawnRecord(r.Guid, r.Event))],
        [.. _creatureData.Values.Select(v => v.Row).OrderBy(r => r.Event).ThenBy(r => r.Guid)
            .Select(r => new GameEventCreatureDataRecord(r.Guid, r.Event, r.EntryId, r.ModelId, r.EquipmentId, r.SpellStart, r.SpellEnd))],
        [.. _quests.Values.OrderBy(r => r.Event).ThenBy(r => r.Quest).Select(r => new GameEventQuestRecord(r.Quest, r.Event))],
        [.. _mails.Values.OrderBy(r => r.Event).ThenBy(r => r.RaceMask).ThenBy(r => r.Quest)
            .Select(r => new GameEventMailRecord(r.Event, r.RaceMask, r.Quest, r.MailTemplateId, r.SenderEntry))]);

    public GameEventImportReport BuildReport()
    {
        int creatures = KeptCreatures().Count();
        int gameObjects = KeptGameObjects().Count();
        return new(_events.Count, _times.Count, creatures, gameObjects, _creatureData.Count, _quests.Count, _mails.Count, _skipped, _diagnostics.Samples)
        {
            OrphanCreatureRows = _creatures.Count - creatures,
            OrphanGameObjectRows = _gameObjects.Count - gameObjects,
        };
    }

    private IEnumerable<GameEventCreatureRow> KeptCreatures()
        => _sawCreatureSpawns ? _creatures.Values.Where(r => _creatureSpawns.Contains(r.Guid)) : _creatures.Values;

    private IEnumerable<GameEventGameObjectRow> KeptGameObjects()
        => _sawGameObjectSpawns ? _gameObjects.Values.Where(r => _gameObjectSpawns.Contains(r.Guid)) : _gameObjects.Values;

    /// <summary>The guid of a <c>creature</c> / <c>gameobject</c> spawn row; null when the table has no readable guid column.</summary>
    private static uint? SpawnGuid(DumpRow row)
        => row.TryGet(out string? raw, "guid") && uint.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint guid)
            ? guid
            : null;

    /// <summary>
    /// Write all seven tables atomically with the contract of <see cref="ImportTransaction"/>: with <paramref name="replace"/> they are
    /// emptied first, without it an existing key fails the write and nothing changes.
    /// </summary>
    public async Task<GameEventImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        GameEventRow[] events = [.. _events.Values.OrderBy(r => r.Entry)];
        GameEventTimeRow[] times = [.. _times.Values.OrderBy(r => r.Entry)];
        GameEventCreatureRow[] creatures = [.. KeptCreatures()];
        GameEventGameObjectRow[] gameObjects = [.. KeptGameObjects()];
        GameEventCreatureDataRow[] creatureData = [.. _creatureData.Values.Select(v => v.Row)];
        GameEventQuestRow[] quests = [.. _quests.Values];
        GameEventMailRow[] mails = [.. _mails.Values];
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<GameEventRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameEventTimeRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameEventCreatureRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameEventGameObjectRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameEventCreatureDataRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameEventQuestRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameEventMailRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, events, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, times, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, creatures, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, gameObjects, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, creatureData, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, quests, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, mails, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    private void ReadCreatureData(DumpRow row)
    {
        Require(row, "guid", "event");
        GameEventCreatureDataRow data = s_creatureData.Map(row, _diagnostics);
        int patch = PatchOf(row, "patch");
        if (patch > MaxPatch)
        {
            _skipped++;
            return;
        }

        (uint, int) key = (data.Guid, data.Event);
        if (_creatureData.TryGetValue(key, out var existing) && existing.Patch > patch)
        {
            _skipped++;
            return;
        }

        _creatureData[key] = (patch, data);
    }

    private void ReadQuest(DumpRow row)
    {
        Require(row, "quest", "event");
        if (PatchOf(row, "patch_min") > MaxPatch)
        {
            _skipped++;
            return;
        }

        GameEventQuestRow quest = s_quests.Map(row, _diagnostics);
        _quests[(quest.Quest, quest.Event)] = quest;
    }

    private static int PatchOf(DumpRow row, string column)
        => row.TryGet(out string? raw, column) && !string.IsNullOrEmpty(raw) && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int patch)
            ? patch
            : 0;

    private static void Require(DumpRow row, params ReadOnlySpan<string> columns)
    {
        foreach (string column in columns)
        {
            if (!row.Has(column))
            {
                throw new ImportSchemaException(row.Table, column, $"table `{row.Table}` has no column `{column}`");
            }
        }

        if (row.Values.Count != row.Columns.Count)
        {
            throw new ImportSchemaException(row.Table, null, $"table `{row.Table}`: a row has {row.Values.Count} values but {row.Columns.Count} columns");
        }
    }
}
