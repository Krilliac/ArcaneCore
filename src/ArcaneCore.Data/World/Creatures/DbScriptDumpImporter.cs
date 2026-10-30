using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// Reads the four cmangos classic-db DB script tables (<c>dbscripts_on_quest_start</c>, <c>_quest_end</c>, <c>_gossip</c>, <c>_event</c>),
/// ScriptDev2's <c>script_waypoint</c> and the script ids that <c>quest_template</c> (<c>StartScript</c>, <c>CompleteScript</c>) and
/// <c>gossip_menu</c> (<c>script_id</c>) carry, and writes the world-schema-42 rows (<see cref="DbScriptDataModule"/>). The script steps have
/// the <c>dbscripts_on_relay</c> layout and run on the same map runner (mangos-classic DBScripts/ScriptMgr.cpp LoadScripts;
/// AI/ScriptDevAI/system/system.cpp LoadScriptWaypoints). <see cref="CreatureDumpImporter"/> feeds it the rows of a full import; the
/// content importer's <c>refresh</c> uses it on its own.
/// </summary>
public sealed class DbScriptDumpImporter
{
    private readonly Dictionary<string, IReadOnlyList<string>> _tableRegistry = MySqlDumpReader.NewTableRegistry();
    private readonly Dictionary<(DbScriptKind Kind, uint Id), List<DbScriptRow>> _scripts = [];
    private readonly HashSet<(DbScriptKind Kind, uint Id)> _touchedThisRead = [];
    private readonly Dictionary<(uint Entry, uint PathId, uint Point), ScriptWaypointRow> _waypoints = [];
    private readonly HashSet<(uint Entry, uint PathId)> _rejectedPaths = [];
    private readonly Dictionary<uint, (uint Start, uint Complete)> _questScripts = [];
    private readonly Dictionary<(uint Entry, uint TextId, uint ConditionId), uint> _menuScripts = [];
    private readonly Dictionary<uint, List<(SpellScriptRow Row, uint VmangosCondition)>> _vmangosSpellScripts = [];
    private readonly Dictionary<uint, string> _vmangosSpellRejections = [];
    private readonly HashSet<uint> _vmangosTouchedThisRead = [];
    private readonly Dictionary<uint, ConditionRow> _vmangosConditions = [];
    private readonly Dictionary<(int Type, uint V1, uint V2, uint V3, uint V4, byte Flags), uint> _cmangosConditions = [];
    private readonly List<string> _warnings = [];

    /// <summary>Every script step read, with its namespace.</summary>
    public IReadOnlyCollection<(DbScriptKind Kind, RelayScriptStep Step)> Scripts
        => [.. _scripts.SelectMany(pair => pair.Value.Select(row => (pair.Key.Kind, DbScriptDataModule.ToStep(row))))];

    /// <summary>The <c>script_waypoint</c> points kept (a path with a point 0 is rejected whole, as LoadScriptWaypoints does).</summary>
    public IReadOnlyCollection<(uint Entry, uint PathId, CreatureWaypoint Point)> ScriptWaypoints
        => [.. WaypointRows().Select(DbScriptDataModule.ToWaypoint)];

    /// <summary>The script rows to write, of every table.</summary>
    public IReadOnlyCollection<DbScriptRow> ScriptRows => [.. _scripts.Values.SelectMany(rows => rows)];

    /// <summary>The <c>script_waypoint</c> rows to write.</summary>
    public IReadOnlyCollection<ScriptWaypointRow> WaypointRowsToWrite => [.. WaypointRows()];

    /// <summary><c>quest_template</c> entries read that carry a <c>StartScript</c> or <c>CompleteScript</c>.</summary>
    public IReadOnlyDictionary<uint, (uint Start, uint Complete)> QuestScripts => _questScripts;

    /// <summary><c>gossip_menu</c> rows read that carry a <c>script_id</c>.</summary>
    public IReadOnlyDictionary<(uint Entry, uint TextId, uint ConditionId), uint> MenuScripts => _menuScripts;

    /// <summary>Whether any script or <c>script_waypoint</c> row was read (the refresh replaces the tables only then).</summary>
    public bool HasRows => _scripts.Count > 0 || _waypoints.Count > 0 || _rejectedPaths.Count > 0 || _vmangosSpellScripts.Count > 0;

    /// <summary>
    /// The vmangos <c>spell_scripts</c> ids read that translate into the cmangos layout (<see cref="VmangosSpellScriptTranslator"/>). They are
    /// written for the spells that classic-db's <c>dbscripts_on_spell</c> (the source of record) has no rows for, once their conditions
    /// resolve (<see cref="InsertAllAsync"/>).
    /// </summary>
    public IReadOnlyCollection<uint> VmangosSpellScriptIds => _vmangosSpellScripts.Keys;

    /// <summary>The vmangos <c>spell_scripts</c> ids refused whole, with the reason of their first refused row.</summary>
    public IReadOnlyDictionary<uint, string> VmangosSpellScriptRejections => _vmangosSpellRejections;

    /// <summary>What the last write left out: vmangos spell scripts whose condition did not resolve.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Rows to write per table.</summary>
    public IReadOnlyDictionary<string, int> Counts => new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [DbScriptDataModule.QuestStartTable] = StepCount(DbScriptKind.QuestStart),
        [DbScriptDataModule.QuestEndTable] = StepCount(DbScriptKind.QuestEnd),
        [DbScriptDataModule.GossipTable] = StepCount(DbScriptKind.Gossip),
        [DbScriptDataModule.EventTable] = StepCount(DbScriptKind.Event),
        [DbScriptDataModule.CreatureMovementTable] = StepCount(DbScriptKind.CreatureMovement),
        [DbScriptDataModule.SpellTable] = StepCount(DbScriptKind.Spell)
            + _vmangosSpellScripts.Where(pair => !_scripts.ContainsKey((DbScriptKind.Spell, pair.Key))).Sum(pair => pair.Value.Count),
        [DbScriptDataModule.WaypointTable] = WaypointRows().Count(),
    };

    /// <summary>Read a dump; a later input's rows replace every row of a script (kind and id) it carries.</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        BeginRead();
        foreach (object item in new MySqlDumpReader(dump, _tableRegistry).Read())
        {
            if (item is DumpRow row)
            {
                Accept(row);
            }
        }
    }

    /// <summary>Start a new input file (a script id is replaced per file, as in <see cref="Read"/>).</summary>
    internal void BeginRead()
    {
        _touchedThisRead.Clear();
        _vmangosTouchedThisRead.Clear();
    }

    /// <summary>Take one dump row of a table this importer reads (other rows are ignored).</summary>
    internal void Accept(DumpRow row)
    {
        string table = row.Table.ToLowerInvariant();
        if (TryKind(table, out DbScriptKind kind))
        {
            uint id = U32(row, "id");
            var key = (kind, id);
            if (_touchedThisRead.Add(key) || !_scripts.ContainsKey(key))
            {
                _scripts[key] = [];
            }

            List<DbScriptRow> steps = _scripts[key];
            steps.Add(ParseStep(kind, row, (uint)steps.Count));
            return;
        }

        switch (table)
        {
            case DbScriptDataModule.WaypointTable:
                ReadWaypoint(row);
                break;
            case VmangosSpellScriptTranslator.Table:
                // cmangos and classic-db have a spell_scripts table too: the script-name binding (Id, ScriptName), not DB script steps.
                if (row.Has("command"))
                {
                    ReadVmangosSpellScript(row);
                }

                break;
            case "conditions":
                ReadCondition(row);
                break;
            case "quest_template" when row.Has("StartScript") || row.Has("CompleteScript"):
            {
                uint entry = U32(row, "entry");
                (uint start, uint complete) = (U32(row, "StartScript"), U32(row, "CompleteScript"));
                if (start != 0 || complete != 0)
                {
                    _questScripts[entry] = (start, complete);
                }
                else
                {
                    _questScripts.Remove(entry);
                }

                break;
            }

            case "gossip_menu" when row.Has("script_id"):
            {
                var key = (U32(row, "entry"), U32(row, "text_id"), U32(row, "condition_id"));
                if (U32(row, "script_id") is var script and not 0)
                {
                    _menuScripts[key] = script;
                }
                else
                {
                    _menuScripts.Remove(key);
                }

                break;
            }
        }
    }

    /// <summary>Insert every script and waypoint row read (the full import, inside the caller's transaction).</summary>
    internal async Task InsertAllAsync(WorldDbContext db, CancellationToken cancellationToken)
    {
        await InsertRowsAsync(db, _scripts.Values.SelectMany(rows => rows), cancellationToken).ConfigureAwait(false);
        await InsertRowsAsync(db, WaypointRows(), cancellationToken).ConfigureAwait(false);
        await InsertRowsAsync(db, await ResolveVmangosSpellScriptsAsync(db, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The translated vmangos spell script rows to write: those of the spells classic-db's own rows do not cover, with each vmangos
    /// <c>condition_id</c> resolved. The vmangos id names a row of a <c>conditions</c> table without the cmangos <c>comments</c> column
    /// (vmangos mangos.sql has none; cmangos mangos.sql and classic-db do). It resolves only when its type means the same in both cores,
    /// which is checked for CONDITION_TEAM (6, value1 the team: vmangos Conditions.h:67 and Conditions.cpp:204-207, cmangos Conditions.h:39)
    /// with flags and the other values 0, to the cmangos row of the same type and values (cmangos keeps them unique, mangos.sql
    /// <c>unique_conditions</c>): a <c>conditions</c> row of the input that has a <c>comments</c> column first, else the world's own
    /// <c>conditions</c> table. The ids differ between the databases (vmangos 2 and 3 are classic-db 3 and 4, Horde and Alliance), so an id is
    /// never copied. A spell with a step whose condition does not resolve is left out whole and named in <see cref="Warnings"/>.
    /// </summary>
    private async Task<List<SpellScriptRow>> ResolveVmangosSpellScriptsAsync(WorldDbContext db, CancellationToken cancellationToken)
    {
        _warnings.Clear();
        var rows = new List<SpellScriptRow>();
        foreach ((uint spellId, List<(SpellScriptRow Row, uint VmangosCondition)> steps) in _vmangosSpellScripts.OrderBy(pair => pair.Key))
        {
            if (_scripts.ContainsKey((DbScriptKind.Spell, spellId)))
            {
                continue; // classic-db's own rows win
            }

            var resolved = new List<SpellScriptRow>(steps.Count);
            string? failure = null;
            foreach ((SpellScriptRow row, uint vmangosCondition) in steps)
            {
                if (vmangosCondition != 0)
                {
                    uint? condition = await ResolveConditionAsync(db, vmangosCondition, cancellationToken).ConfigureAwait(false);
                    if (condition is null)
                    {
                        failure = $"vmangos condition {vmangosCondition} has no cmangos equivalent here";
                        break;
                    }

                    row.ConditionId = condition.Value;
                }

                resolved.Add(row);
            }

            if (failure is null)
            {
                rows.AddRange(resolved);
            }
            else
            {
                _warnings.Add($"spell_scripts {spellId} not imported: {failure}");
            }
        }

        return rows;
    }

    private async Task<uint?> ResolveConditionAsync(WorldDbContext db, uint vmangosCondition, CancellationToken cancellationToken)
    {
        const int Team = 6;
        if (!_vmangosConditions.TryGetValue(vmangosCondition, out ConditionRow? vm) || vm.Type != Team || vm.Flags != 0
            || vm.Value2 != 0 || vm.Value3 != 0 || vm.Value4 != 0)
        {
            return null;
        }

        if (_cmangosConditions.TryGetValue((vm.Type, vm.Value1, 0, 0, 0, 0), out uint entry))
        {
            return entry;
        }

        List<uint> entries = await db.Set<ConditionRow>()
            .Where(c => c.Type == vm.Type && c.Value1 == vm.Value1 && c.Value2 == 0 && c.Value3 == 0 && c.Value4 == 0 && c.Flags == 0)
            .Select(c => c.ConditionEntry).ToListAsync(cancellationToken).ConfigureAwait(false);
        return entries.Count == 0 ? null : entries.Min();
    }

    /// <summary>One vmangos <c>spell_scripts</c> row: a later input's rows replace an id it carries; one refused row refuses its id.</summary>
    private void ReadVmangosSpellScript(DumpRow row)
    {
        uint id = U32(row, "id");
        if (_vmangosTouchedThisRead.Add(id))
        {
            _vmangosSpellScripts.Remove(id);
            _vmangosSpellRejections.Remove(id);
        }

        if (_vmangosSpellRejections.ContainsKey(id))
        {
            return;
        }

        if (!_vmangosSpellScripts.TryGetValue(id, out List<(SpellScriptRow Row, uint VmangosCondition)>? steps))
        {
            steps = [];
            _vmangosSpellScripts[id] = steps;
        }

        VmangosSpellScriptStep step = VmangosSpellScriptTranslator.Translate(row, (uint)steps.Count);
        if (step.Row is null)
        {
            _vmangosSpellScripts.Remove(id);
            _vmangosSpellRejections[id] = step.Rejection!;
            return;
        }

        steps.Add((step.Row, step.VmangosConditionId));
    }

    /// <summary>A <c>conditions</c> row, kept only to resolve vmangos spell script conditions (the conditions importer writes the table).</summary>
    private void ReadCondition(DumpRow row)
    {
        var parsed = new ConditionRow
        {
            ConditionEntry = U32(row, "condition_entry"),
            Type = I32(row, "type"),
            Value1 = U32(row, "value1"),
            Value2 = U32(row, "value2"),
            Value3 = U32(row, "value3"),
            Value4 = U32(row, "value4"),
            Flags = (byte)U32(row, "flags"),
        };
        if (row.Has("comments"))
        {
            var key = (parsed.Type, parsed.Value1, parsed.Value2, parsed.Value3, parsed.Value4, parsed.Flags);
            if (!_cmangosConditions.TryGetValue(key, out uint known) || parsed.ConditionEntry < known)
            {
                _cmangosConditions[key] = parsed.ConditionEntry;
            }
        }
        else
        {
            _vmangosConditions[parsed.ConditionEntry] = parsed;
        }
    }

    /// <summary>Empty the four script tables and <c>script_waypoint</c>.</summary>
    internal static async Task DeleteAllAsync(WorldDbContext db, CancellationToken cancellationToken)
    {
        await db.Set<QuestStartScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<QuestEndScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<GossipScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<EventScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CreatureMovementScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<SpellScriptRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<ScriptWaypointRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The content importer's <c>refresh</c>: replace the four script tables and <c>script_waypoint</c> with the rows read (nothing read:
    /// nothing is emptied), and set <c>quest_template.StartScript</c>/<c>CompleteScript</c> and <c>gossip_menu.script_id</c> on the rows the
    /// database already has, from the dump's values (the quests and menus themselves are left alone). Runs inside the caller's transaction.
    /// Returns the quest and menu rows whose script ids were set.
    /// </summary>
    public async Task<(int Quests, int Menus)> ReplaceAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (HasRows)
        {
            await DeleteAllAsync(db, cancellationToken).ConfigureAwait(false);
            await InsertAllAsync(db, cancellationToken).ConfigureAwait(false);
        }

        int quests = 0;
        if (_questScripts.Count > 0)
        {
            await db.Set<QuestTemplate>().Where(q => q.StartScript != 0 || q.CompleteScript != 0)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.StartScript, 0u).SetProperty(q => q.CompleteScript, 0u), cancellationToken)
                .ConfigureAwait(false);
            foreach ((uint entry, (uint start, uint complete)) in _questScripts)
            {
                quests += await db.Set<QuestTemplate>().Where(q => q.Entry == entry)
                    .ExecuteUpdateAsync(s => s.SetProperty(q => q.StartScript, start).SetProperty(q => q.CompleteScript, complete), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        int menus = 0;
        foreach (((uint entry, uint textId, uint condition), uint script) in _menuScripts)
        {
            menus += await db.Set<GossipMenu>().Where(m => m.Entry == entry && m.TextId == textId && m.ConditionId == condition)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ScriptId, script), cancellationToken).ConfigureAwait(false);
        }

        return (quests, menus);
    }

    private void ReadWaypoint(DumpRow row)
    {
        uint entry = U32(row, "Entry");
        uint pathId = U32(row, "PathId");
        uint point = U32(row, "Point");
        // LoadScriptWaypoints (system.cpp:92-96, 113-114): a point 0 rejects the whole path of that entry.
        if (point == 0)
        {
            _rejectedPaths.Add((entry, pathId));
            return;
        }

        if (entry == 0)
        {
            return;
        }

        _waypoints[(entry, pathId, point)] = new ScriptWaypointRow
        {
            Entry = entry,
            PathId = pathId,
            Point = point,
            X = F32(row, "PositionX"),
            Y = F32(row, "PositionY"),
            Z = F32(row, "PositionZ"),
            Orientation = F32(row, "Orientation"),
            WaitTimeMs = U32(row, "WaitTime"),
            ScriptId = U32(row, "ScriptId"),
        };
    }

    private int StepCount(DbScriptKind kind) => _scripts.Where(pair => pair.Key.Kind == kind).Sum(pair => pair.Value.Count);

    private IEnumerable<ScriptWaypointRow> WaypointRows()
        => _waypoints.Where(pair => !_rejectedPaths.Contains((pair.Key.Entry, pair.Key.PathId))).Select(pair => pair.Value);

    private static async Task InsertRowsAsync(WorldDbContext db, IEnumerable<object> rows, CancellationToken ct)
    {
        const int BatchSize = 2000;
        int pending = 0;
        foreach (object row in rows)
        {
            db.Add(row);
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

    private static bool TryKind(string table, out DbScriptKind kind)
    {
        (bool known, kind) = table switch
        {
            DbScriptDataModule.QuestStartTable => (true, DbScriptKind.QuestStart),
            DbScriptDataModule.QuestEndTable => (true, DbScriptKind.QuestEnd),
            DbScriptDataModule.GossipTable => (true, DbScriptKind.Gossip),
            DbScriptDataModule.EventTable => (true, DbScriptKind.Event),
            DbScriptDataModule.CreatureMovementTable => (true, DbScriptKind.CreatureMovement),
            DbScriptDataModule.SpellTable => (true, DbScriptKind.Spell),
            _ => (false, default(DbScriptKind)),
        };
        return known;
    }

    private static DbScriptRow ParseStep(DbScriptKind kind, DumpRow row, uint ordinal)
    {
        DbScriptRow step = DbScriptDataModule.NewRow(kind);
        step.Id = U32(row, "id");
        step.Ordinal = ordinal;
        step.Delay = U32(row, "delay");
        step.Priority = U32(row, "priority");
        step.Command = U32(row, "command");
        step.DataLong = U32(row, "datalong");
        step.DataLong2 = U32(row, "datalong2");
        step.DataLong3 = U32(row, "datalong3");
        step.BuddyEntry = U32(row, "buddy_entry");
        step.SearchRadius = U32(row, "search_radius");
        step.DataFlags = U32(row, "data_flags");
        step.DataInt = I32(row, "dataint");
        step.DataInt2 = I32(row, "dataint2");
        step.DataInt3 = I32(row, "dataint3");
        step.DataInt4 = I32(row, "dataint4");
        step.DataFloat = F32(row, "datafloat");
        step.X = F32(row, "x");
        step.Y = F32(row, "y");
        step.Z = F32(row, "z");
        step.O = F32(row, "o");
        step.Speed = F32(row, "speed");
        step.ConditionId = U32(row, "condition_id");
        return step;
    }

    private static uint U32(DumpRow row, string name)
        => row.TryGet(out string? value, name) && !string.IsNullOrWhiteSpace(value)
            ? uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;

    private static int I32(DumpRow row, string name)
        => row.TryGet(out string? value, name) && !string.IsNullOrWhiteSpace(value)
            ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;

    private static float F32(DumpRow row, string name)
        => row.TryGet(out string? value, name) && !string.IsNullOrWhiteSpace(value)
            ? float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;
}
