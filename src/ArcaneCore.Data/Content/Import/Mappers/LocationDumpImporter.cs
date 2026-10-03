using System.Globalization;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a location import read and wrote.</summary>
public sealed record LocationImportReport(int Portals, int Teleports, int SkippedRows, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps <c>areatrigger_teleport</c> (dungeon and instance portals) and <c>game_tele</c> (the GM
/// <c>.tele</c> names) of a cmangos classic-db or vmangos dump into <see cref="AreaTriggerTeleportRow"/>
/// and <see cref="GameTeleRow"/> by column name. cmangos calls the failure text
/// <c>status_failed_text</c>, vmangos <c>message</c> (vmangos <c>ObjectMgr::LoadAreaTriggerTeleports</c>,
/// src/game/ObjectMgr.cpp:7712-7717); vmangos rows are patch-versioned and the row with the highest
/// <c>patch</c> not above 10 wins. The row carries no item, quest or heroic-key requirement, so those
/// cmangos columns (<c>required_item</c>, <c>required_quest_done</c>, <c>condition_id</c>) are not
/// enforced; <c>plan</c> lists them as not imported. The trigger shapes themselves
/// (<c>areatrigger_template</c>) come from AreaTrigger.dbc and are not imported here. The dumps are
/// GPL data and are never committed.
/// </summary>
public sealed class LocationDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1 (shared with the other dump importers).</summary>
    public const int MaxPatch = CreatureDumpImporter.MaxPatch;

    private static readonly RowMapper<AreaTriggerTeleportRow> s_portalMapper = new(new Dictionary<string, string>
    {
        ["status_failed_text"] = nameof(AreaTriggerTeleportRow.Message),
    });

    private static readonly RowMapper<GameTeleRow> s_teleMapper = new();

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<uint, (int Patch, AreaTriggerTeleportRow Row)> _portals = [];
    private readonly Dictionary<uint, GameTeleRow> _teleports = [];
    private readonly MapDiagnostics _diagnostics = new();
    private int _skipped;

    internal static bool ReadsPortalColumn(string column)
        => s_portalMapper.Maps(column) || column.Equals("patch", StringComparison.OrdinalIgnoreCase);

    internal static bool ReadsTeleColumn(string column) => s_teleMapper.Maps(column);

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
                case "areatrigger_teleport":
                    ReadPortal(row);
                    break;
                case "game_tele":
                    uint[] key = _keys.Read(row, "game_tele");
                    _teleports[key[0]] = s_teleMapper.Map(row, _diagnostics);
                    break;
            }
        }
    }

    /// <summary>The rows that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<AreaTriggerTeleportRow> Portals, IReadOnlyCollection<GameTeleRow> Teleports) Snapshot()
        => ([.. _portals.Values.Select(p => p.Row)], [.. _teleports.Values]);

    public LocationImportReport BuildReport() => new(_portals.Count, _teleports.Count, _skipped, _diagnostics.Samples);

    /// <summary>
    /// Write both tables atomically with the contract of <see cref="ImportTransaction"/>. With
    /// <paramref name="replace"/> they are emptied first; without it an existing key fails the write
    /// and nothing changes. The DBC-derived map tables are untouched.
    /// </summary>
    public async Task<LocationImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
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
                    await db.Set<AreaTriggerTeleportRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameTeleRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, snapshot.Portals, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Teleports, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    private void ReadPortal(DumpRow row)
    {
        uint[] key = _keys.Read(row, "areatrigger_teleport");
        int patch = row.TryGet(out string? raw, "patch") && !string.IsNullOrEmpty(raw)
            ? (int)double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;
        if (patch > MaxPatch || (_portals.TryGetValue(key[0], out var existing) && existing.Patch > patch))
        {
            _skipped++;
            return;
        }

        _portals[key[0]] = (patch, s_portalMapper.Map(row, _diagnostics));
    }
}
