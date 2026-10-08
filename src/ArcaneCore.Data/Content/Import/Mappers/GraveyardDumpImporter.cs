using System.Globalization;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a graveyard import read and wrote.</summary>
public sealed record GraveyardImportReport(int SafeLocs, int Links, int SkippedLinks, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps the graveyard tables of a cmangos classic-db or vmangos dump:
/// <c>world_safe_locs</c> (<c>id, map, x, y, z, o, name</c>; cmangos, the facing is <c>o</c>),
/// <c>game_graveyard_zone</c> (cmangos <c>id, ghost_loc, link_kind, faction</c>; vmangos
/// <c>id, ghost_zone, faction, patch_min, patch_max</c>) and vmangos' <c>world_safe_locs_facing</c> (<c>id, orientation</c>,
/// ObjectMgr.cpp:7669-7704). vmangos keeps the safe locations in <c>WorldSafeLocs.dbc</c>, so a vmangos import adds
/// them with <see cref="ReadSafeLocs"/> (<see cref="WorldSafeLocsDbcReader"/>).
/// <para>
/// Load rules follow vmangos <c>ObjectMgr::LoadGraveyardZones</c> (ObjectMgr.cpp:7453-7510): a link whose team is not 0, 67
/// or 469 is skipped; a second link for the same (graveyard, zone) is skipped (the first stays); vmangos rows are kept only
/// when <c>patch_min &lt;= 10 &lt;= patch_max</c>. cmangos <c>link_kind</c> other than 0 (a map link, a cmangos extension
/// that vmangos does not have) is skipped and counted. A link to a safe location that does not exist, and a zone that is
/// not in the area table, are checked where both are known (the graveyard catalog at world start), not here, because the
/// tables arrive in any order and a link can be imported on its own. The facing of a safe location is the vmangos
/// facing table when it has the id, else <c>o</c>, else 0. A name over 50 characters is cut to the column length and
/// reported. The dumps are GPL data and are never committed.
/// </para>
/// </summary>
public sealed class GraveyardDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1 (shared with the other dump importers).</summary>
    public const int MaxPatch = LocationDumpImporter.MaxPatch;

    /// <summary>Team ids of <c>game_graveyard_zone.faction</c>: both, Horde, Alliance (vmangos TEAM_NONE, HORDE, ALLIANCE).</summary>
    private static readonly uint[] s_teams = [0, 67, 469];

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<uint, WorldSafeLocRow> _locs = [];
    private readonly Dictionary<uint, float> _facing = [];
    private readonly Dictionary<(uint, uint), GraveyardZoneRow> _links = [];
    private readonly List<string> _warnings = [];
    private int _skippedKind;
    private int _skippedTeam;
    private int _skippedDuplicate;
    private int _skippedPatch;
    private int _truncatedNames;

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

            switch (row.Table.ToLowerInvariant())
            {
                case GraveyardDataModule.SafeLocsTable:
                    ReadSafeLoc(row);
                    break;
                case GraveyardDataModule.GraveyardZoneTable:
                    ReadLink(row);
                    break;
                case "world_safe_locs_facing":
                    ReadFacing(row);
                    break;
            }
        }
    }

    /// <summary>Add safe locations read from a <c>WorldSafeLocs.dbc</c> (a later one replaces an earlier one with the same id).</summary>
    public void ReadSafeLocs(IEnumerable<WorldSafeLoc> locs)
    {
        ArgumentNullException.ThrowIfNull(locs);
        foreach (WorldSafeLoc loc in locs)
        {
            _locs[loc.Id] = new WorldSafeLocRow
            {
                Id = loc.Id, MapId = loc.MapId, X = loc.X, Y = loc.Y, Z = loc.Z, Orientation = loc.Orientation, Name = FitName(loc.Id, loc.Name),
            };
        }
    }

    /// <summary>
    /// Add the safe locations of a <c>WorldSafeLocs.dbc</c> that the dumps read so far do not carry, and return how many were added. A
    /// dump row wins because it carries the facing (cmangos <c>o</c>) the DBC lacks; the DBC only fills ids a cmangos dump never had.
    /// </summary>
    public int AddMissingSafeLocs(IEnumerable<WorldSafeLoc> locs)
    {
        ArgumentNullException.ThrowIfNull(locs);
        int added = 0;
        foreach (WorldSafeLoc loc in locs)
        {
            if (!_locs.ContainsKey(loc.Id))
            {
                _locs[loc.Id] = new WorldSafeLocRow
                {
                    Id = loc.Id, MapId = loc.MapId, X = loc.X, Y = loc.Y, Z = loc.Z, Orientation = loc.Orientation, Name = FitName(loc.Id, loc.Name),
                };
                added++;
            }
        }

        return added;
    }

    /// <summary>Whether anything was read (an empty importer must not empty the tables on a replace).</summary>
    public bool HasRows => _locs.Count > 0 || _links.Count > 0;

    /// <summary>The rows that would be written (for inspection and tests): the facing table applied.</summary>
    public (IReadOnlyCollection<WorldSafeLocRow> SafeLocs, IReadOnlyCollection<GraveyardZoneRow> Links) Snapshot()
    {
        var locs = new List<WorldSafeLocRow>(_locs.Count);
        foreach (WorldSafeLocRow loc in _locs.Values.OrderBy(l => l.Id))
        {
            locs.Add(_facing.TryGetValue(loc.Id, out float facing)
                ? new WorldSafeLocRow { Id = loc.Id, MapId = loc.MapId, X = loc.X, Y = loc.Y, Z = loc.Z, Orientation = facing, Name = loc.Name }
                : loc);
        }

        return (locs, [.. _links.Values.OrderBy(l => l.Id).ThenBy(l => l.GhostZone)]);
    }

    public GraveyardImportReport BuildReport()
    {
        var warnings = new List<string>(_warnings);
        AddCount(warnings, _skippedKind, "link_kind other than 0 (a map link, not in vmangos)");
        AddCount(warnings, _skippedTeam, "a faction other than 0, 67 or 469 (vmangos skips it at load)");
        AddCount(warnings, _skippedDuplicate, "a second link for the same graveyard and zone (the first one is kept, as vmangos does)");
        AddCount(warnings, _skippedPatch, $"patch_min..patch_max not covering patch {MaxPatch}");
        if (_truncatedNames > 0)
        {
            warnings.Add($"{GraveyardDataModule.SafeLocsTable}: {_truncatedNames} name(s) cut to {GraveyardDataModule.NameLength} characters");
        }

        return new GraveyardImportReport(_locs.Count, _links.Count, _skippedKind + _skippedTeam + _skippedDuplicate + _skippedPatch, warnings);

        static void AddCount(List<string> list, int count, string reason)
        {
            if (count > 0)
            {
                list.Add($"{GraveyardDataModule.GraveyardZoneTable}: {count} link(s) skipped: {reason}");
            }
        }
    }

    /// <summary>
    /// Write both tables atomically with the contract of <see cref="ImportTransaction"/>. With
    /// <paramref name="replace"/> they are emptied first; without it an existing key fails the write and nothing changes.
    /// </summary>
    public async Task<GraveyardImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
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
                    await db.Set<GraveyardZoneRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<WorldSafeLocRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, snapshot.SafeLocs, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Links, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    private void ReadSafeLoc(DumpRow row)
    {
        uint id = _keys.Read(row, GraveyardDataModule.SafeLocsTable)[0];
        _locs[id] = new WorldSafeLocRow
        {
            Id = id,
            MapId = RequiredUInt(row, GraveyardDataModule.SafeLocsTable, "map"),
            X = RequiredFloat(row, GraveyardDataModule.SafeLocsTable, "x"),
            Y = RequiredFloat(row, GraveyardDataModule.SafeLocsTable, "y"),
            Z = RequiredFloat(row, GraveyardDataModule.SafeLocsTable, "z"),
            Orientation = OptionalFloat(row, GraveyardDataModule.SafeLocsTable, "o"),
            Name = FitName(id, row.TryGet(out string? name, "name") ? name ?? string.Empty : string.Empty),
        };
    }

    private void ReadFacing(DumpRow row)
    {
        uint id = _keys.Read(row, "world_safe_locs_facing")[0];
        _facing[id] = RequiredFloat(row, "world_safe_locs_facing", "orientation");
    }

    private void ReadLink(DumpRow row)
    {
        uint[] key = _keys.Read(row, GraveyardDataModule.GraveyardZoneTable);
        if (row.TryGet(out string? kind, "link_kind") && kind is not (null or "0"))
        {
            _skippedKind++;
            return;
        }

        if (!InPatch(row))
        {
            _skippedPatch++;
            return;
        }

        uint team = RequiredUInt(row, GraveyardDataModule.GraveyardZoneTable, "faction");
        if (Array.IndexOf(s_teams, team) < 0)
        {
            _skippedTeam++;
            return;
        }

        if (!_links.TryAdd((key[0], key[1]), new GraveyardZoneRow { Id = key[0], GhostZone = key[1], Team = team }))
        {
            _skippedDuplicate++;
        }
    }

    /// <summary>vmangos <c>WHERE 10 BETWEEN patch_min AND patch_max</c>; a dump without the columns has no patch rows.</summary>
    private static bool InPatch(DumpRow row)
    {
        int min = row.TryGet(out string? lo, "patch_min") && lo is not null ? ParsePatch(lo) : 0;
        int max = row.TryGet(out string? hi, "patch_max") && hi is not null ? ParsePatch(hi) : int.MaxValue;
        return min <= MaxPatch && MaxPatch <= max;
    }

    private static int ParsePatch(string raw)
        => (int)double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);

    private string FitName(uint id, string name)
    {
        if (name.Length <= GraveyardDataModule.NameLength)
        {
            return name;
        }

        int cut = GraveyardDataModule.NameLength;
        if (char.IsHighSurrogate(name[cut - 1]))
        {
            cut--; // never split a surrogate pair
        }

        _truncatedNames++;
        return name[..cut];
    }

    private static uint RequiredUInt(DumpRow row, string table, string column)
    {
        if (!row.TryGet(out string? raw, column) || raw is null)
        {
            throw new ImportSchemaException(table, column, $"table `{table}`: the column `{column}` is missing or NULL");
        }

        return uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
            ? value
            : throw new ImportSchemaException(table, column, $"table `{table}`, column `{column}`: value '{raw}' is not an unsigned number");
    }

    private static float RequiredFloat(DumpRow row, string table, string column)
    {
        if (!row.TryGet(out string? raw, column) || raw is null)
        {
            throw new ImportSchemaException(table, column, $"table `{table}`: the column `{column}` is missing or NULL");
        }

        return ParseFloat(table, column, raw);
    }

    private static float OptionalFloat(DumpRow row, string table, string column)
        => row.TryGet(out string? raw, column) && raw is not null ? ParseFloat(table, column, raw) : 0f;

    private static float ParseFloat(string table, string column, string raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? (float)value
            : throw new ImportSchemaException(table, column, $"table `{table}`, column `{column}`: value '{raw}' is not a valid number");
}
