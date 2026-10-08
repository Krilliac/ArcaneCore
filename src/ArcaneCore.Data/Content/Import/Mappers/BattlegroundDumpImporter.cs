using System.Globalization;
using ArcaneCore.Data.World.Battlegrounds;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a battleground import read and wrote.</summary>
public sealed record BattlegroundImportReport(int Templates, int CreatureEvents, int GameObjectEvents, int Battlemasters, int SkippedRows, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps the battleground tables of a cmangos classic-db or vmangos dump by column name: <c>battleground_template</c> (cmangos <c>id,
/// MinPlayersPerTeam, MaxPlayersPerTeam, MinLvl, MaxLvl, AllianceStartLoc, HordeStartLoc, StartMaxDist, PlayerSkinReflootId</c>; vmangos adds
/// <c>patch</c> and the four mark spells, in either the old CamelCase or the newer snake_case names), <c>creature_battleground</c> and
/// <c>gameobject_battleground</c> (<c>guid, event1, event2</c>) and <c>battlemaster_entry</c> (<c>entry, bg_template</c>). vmangos template rows
/// are patch-versioned: the row with the highest <c>patch</c> not above <see cref="MaxPatch"/> wins. A template id other than 1, 2 or 3 is
/// skipped (vmangos <c>CreateBattleGround</c> only builds those). The dumps are GPL data and are never committed.
/// </summary>
public sealed class BattlegroundDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1 (shared with the other dump importers).</summary>
    public const int MaxPatch = CreatureDumpImporter.MaxPatch;

    private readonly Dictionary<uint, (int Patch, BattlegroundTemplateRow Row)> _templates = [];
    private readonly Dictionary<(uint, byte, byte), CreatureBattlegroundRow> _creatures = [];
    private readonly Dictionary<(uint, byte, byte), GameObjectBattlegroundRow> _objects = [];
    private readonly Dictionary<uint, BattlemasterEntryRow> _masters = [];
    private int _skippedTemplates;
    private int _skippedPatch;

    /// <summary>Read one dump (call again for further files; a later row replaces an earlier one with the same key).</summary>
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
                case BattlegroundWorldDataModule.TemplateTable:
                    ReadTemplate(row);
                    break;
                case BattlegroundWorldDataModule.CreatureEventTable:
                    (uint cGuid, byte cE1, byte cE2) = ReadEvent(row, BattlegroundWorldDataModule.CreatureEventTable);
                    _creatures[(cGuid, cE1, cE2)] = new CreatureBattlegroundRow { Guid = cGuid, Event1 = cE1, Event2 = cE2 };
                    break;
                case BattlegroundWorldDataModule.GameObjectEventTable:
                    (uint oGuid, byte oE1, byte oE2) = ReadEvent(row, BattlegroundWorldDataModule.GameObjectEventTable);
                    _objects[(oGuid, oE1, oE2)] = new GameObjectBattlegroundRow { Guid = oGuid, Event1 = oE1, Event2 = oE2 };
                    break;
                case BattlegroundWorldDataModule.BattlemasterTable:
                    uint entry = Required(row, BattlegroundWorldDataModule.BattlemasterTable, "entry");
                    _masters[entry] = new BattlemasterEntryRow { Entry = entry, BattlegroundTemplate = Required(row, BattlegroundWorldDataModule.BattlemasterTable, "bg_template") };
                    break;
            }
        }
    }

    /// <summary>The rows that would be written (for inspection and tests).</summary>
    public (IReadOnlyList<BattlegroundTemplateRow> Templates, IReadOnlyList<CreatureBattlegroundRow> Creatures, IReadOnlyList<GameObjectBattlegroundRow> Objects, IReadOnlyList<BattlemasterEntryRow> Masters) Snapshot()
        => ([.. _templates.Values.Select(t => t.Row).OrderBy(r => r.Id)],
            [.. _creatures.Values.OrderBy(r => r.Guid).ThenBy(r => r.Event1).ThenBy(r => r.Event2)],
            [.. _objects.Values.OrderBy(r => r.Guid).ThenBy(r => r.Event1).ThenBy(r => r.Event2)],
            [.. _masters.Values.OrderBy(r => r.Entry)]);

    public BattlegroundImportReport BuildReport()
    {
        List<string> warnings = [];
        if (_skippedTemplates > 0)
        {
            warnings.Add($"{BattlegroundWorldDataModule.TemplateTable}: {_skippedTemplates} row(s) skipped: an id other than 1, 2 or 3 (vmangos builds only AV, WS and AB)");
        }

        if (_skippedPatch > 0)
        {
            warnings.Add($"{BattlegroundWorldDataModule.TemplateTable}: {_skippedPatch} row(s) skipped: patch above {MaxPatch}");
        }

        return new BattlegroundImportReport(_templates.Count, _creatures.Count, _objects.Count, _masters.Count, _skippedTemplates + _skippedPatch, warnings);
    }

    /// <summary>
    /// Write the four tables atomically with the contract of <see cref="ImportTransaction"/>. With <paramref name="replace"/> they are emptied
    /// first; without it an existing key fails the write and nothing changes. Nothing read: nothing is written or emptied.
    /// </summary>
    public async Task<BattlegroundImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var snapshot = Snapshot();
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<BattlegroundTemplateRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<CreatureBattlegroundRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameObjectBattlegroundRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<BattlemasterEntryRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, snapshot.Templates, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Creatures, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Objects, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Masters, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    private void ReadTemplate(DumpRow row)
    {
        const string table = BattlegroundWorldDataModule.TemplateTable;
        uint id = Required(row, table, "id");
        int patch = row.TryGet(out string? rawPatch, "patch") && rawPatch is not null ? (int)ParseUInt(table, "patch", rawPatch) : 0;
        if (id is < 1 or > 3)
        {
            _skippedTemplates++;
            return;
        }

        if (patch > MaxPatch)
        {
            _skippedPatch++;
            return;
        }

        if (_templates.TryGetValue(id, out var existing) && existing.Patch > patch)
        {
            return;
        }

        _templates[id] = (patch, new BattlegroundTemplateRow
        {
            Id = id,
            MinPlayersPerTeam = Required(row, table, "MinPlayersPerTeam", "min_players_per_team"),
            MaxPlayersPerTeam = Required(row, table, "MaxPlayersPerTeam", "max_players_per_team"),
            MinLevel = Required(row, table, "MinLvl", "min_level"),
            MaxLevel = Required(row, table, "MaxLvl", "max_level"),
            AllianceWinSpell = Optional(row, table, "AllianceWinSpell", "alliance_win_spell"),
            AllianceLoseSpell = Optional(row, table, "AllianceLoseSpell", "alliance_lose_spell"),
            HordeWinSpell = Optional(row, table, "HordeWinSpell", "horde_win_spell"),
            HordeLoseSpell = Optional(row, table, "HordeLoseSpell", "horde_lose_spell"),
            AllianceStartLoc = Required(row, table, "AllianceStartLoc", "alliance_start_location"),
            HordeStartLoc = Required(row, table, "HordeStartLoc", "horde_start_location"),
            StartMaxDist = row.TryGet(out string? dist, "StartMaxDist") && dist is not null
                ? float.Parse(dist, NumberStyles.Float, CultureInfo.InvariantCulture)
                : 0f,
            PlayerSkinRefLootId = Optional(row, table, "PlayerSkinReflootId", "player_loot_id"),
        });
    }

    private static (uint Guid, byte Event1, byte Event2) ReadEvent(DumpRow row, string table)
    {
        uint guid = Required(row, table, "guid");
        uint e1 = Required(row, table, "event1");
        uint e2 = Required(row, table, "event2");
        if (e1 > byte.MaxValue || e2 > byte.MaxValue)
        {
            throw new ImportSchemaException(table, "event1", $"table `{table}`: event ({e1}, {e2}) of guid {guid} is not a tinyint");
        }

        return (guid, (byte)e1, (byte)e2);
    }

    private static uint Required(DumpRow row, string table, params string[] columns)
    {
        if (!row.TryGet(out string? raw, columns) || raw is null)
        {
            throw new ImportSchemaException(table, columns[0], $"table `{table}`: the column `{columns[0]}` is missing or NULL");
        }

        return ParseUInt(table, columns[0], raw);
    }

    private static uint Optional(DumpRow row, string table, params string[] columns)
        => row.TryGet(out string? raw, columns) && raw is not null ? ParseUInt(table, columns[0], raw) : 0;

    private static uint ParseUInt(string table, string column, string raw)
        => uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
            ? value
            : throw new ImportSchemaException(table, column, $"table `{table}`, column `{column}`: value '{raw}' is not an unsigned number");
}
