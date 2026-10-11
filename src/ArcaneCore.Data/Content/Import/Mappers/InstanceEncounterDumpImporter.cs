using System.Globalization;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.Instances;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Maps the encounters of a cmangos classic-db dump: <c>instance_dungeon_encounters</c> (the DungeonEncounter.dbc export: <c>Id, MapId,
/// Difficulty, EncounterData, EncounterIndex, ..., EncounterName</c>) joined on <c>entry = Id</c> to <c>instance_encounters</c>
/// (<c>entry, creditType, creditEntry, lastEncounterDungeon</c>). Only <c>Difficulty = 0</c> rows are kept (the table lists Onyxia's Lair
/// and Naxxramas twice, once per later difficulty); an encounter without an <c>instance_encounters</c> row has no credit rule and is
/// dropped. The dumps are GPL data and are never committed.
/// </summary>
public sealed class InstanceEncounterDumpImporter
{
    private const string DungeonTable = "instance_dungeon_encounters";
    private const string CreditTable = "instance_encounters";

    private readonly Dictionary<uint, (uint Map, uint Index, string Name)> _encounters = [];
    private readonly Dictionary<uint, (byte Type, uint Entry, bool Last)> _credits = [];
    private bool _sawDungeon;
    private bool _sawCredit;

    /// <summary>Whether the dump carried both tables (an absent table leaves the database untouched).</summary>
    public bool SawTable => _sawDungeon && _sawCredit;

    /// <summary>The joined encounters read so far, ordered by map and index.</summary>
    public IReadOnlyList<InstanceEncounterRow> Rows => [.. Join().OrderBy(r => r.Map).ThenBy(r => r.EncounterIndex).ThenBy(r => r.EncounterId)];

    /// <summary>Read one dump (call again for further files).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            if (string.Equals(row.Table, DungeonTable, StringComparison.OrdinalIgnoreCase))
            {
                _sawDungeon = true;
                uint id = Unsigned(row, DungeonTable, "Id");
                if (Unsigned(row, DungeonTable, "Difficulty") != 0)
                {
                    continue;
                }

                uint map = Unsigned(row, DungeonTable, "MapId");
                uint index = Unsigned(row, DungeonTable, "EncounterIndex");
                if (!row.TryGet(out string? name, "EncounterName") || name is null)
                {
                    throw new ImportSchemaException(DungeonTable, "EncounterName", $"table `{DungeonTable}`: encounter {id} has no EncounterName");
                }

                _encounters[id] = (map, index, name);
            }
            else if (string.Equals(row.Table, CreditTable, StringComparison.OrdinalIgnoreCase))
            {
                _sawCredit = true;
                uint entry = Unsigned(row, CreditTable, "entry");
                uint type = Unsigned(row, CreditTable, "creditType");
                if (type > byte.MaxValue)
                {
                    throw new ImportSchemaException(CreditTable, "creditType", $"table `{CreditTable}`: creditType {type} of encounter {entry} is not a credit type");
                }

                _credits[entry] = ((byte)type, Unsigned(row, CreditTable, "creditEntry"), Unsigned(row, CreditTable, "lastEncounterDungeon") != 0);
            }
        }
    }

    private IEnumerable<InstanceEncounterRow> Join()
    {
        if (!SawTable)
        {
            yield break;
        }

        foreach ((uint id, (uint map, uint index, string name)) in _encounters)
        {
            if (!_credits.TryGetValue(id, out (byte Type, uint Entry, bool Last) credit))
            {
                continue;
            }

            yield return new InstanceEncounterRow
            {
                EncounterId = id, Map = map, EncounterIndex = index, CreditType = credit.Type, CreditEntry = credit.Entry,
                LastEncounter = credit.Last, Name = name.Length > 255 ? name[..255] : name,
            };
        }
    }

    /// <summary>Replace the table with the rows read (inside the caller's <see cref="ImportTransaction"/>); nothing read: nothing changes.</summary>
    public async Task<int> ReplaceAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!SawTable)
        {
            return 0;
        }

        IReadOnlyList<InstanceEncounterRow> rows = Rows;
        await db.Set<InstanceEncounterRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, rows, cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    private static uint Unsigned(DumpRow row, string table, string column)
    {
        if (!row.TryGet(out string? raw, column) || raw is null
            || !uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
        {
            throw new ImportSchemaException(table, column, $"table `{table}`: {column} '{raw}' is not an unsigned number");
        }

        return value;
    }
}
