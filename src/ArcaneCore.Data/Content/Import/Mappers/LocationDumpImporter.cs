using System.Globalization;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a location import read and wrote.</summary>
public sealed record LocationImportReport(int Portals, int Teleports, int SkippedRows, IReadOnlyList<string> Warnings, int QuestTriggers = 0);

/// <summary>
/// Maps <c>areatrigger_teleport</c> (dungeon and instance portals), <c>areatrigger_involvedrelation</c> (the exploration
/// quest each area trigger credits) and <c>game_tele</c> (the GM <c>.tele</c> names) of a cmangos classic-db or vmangos dump
/// into <see cref="AreaTriggerTeleportRow"/>, <see cref="AreaTriggerQuestRow"/> and <see cref="GameTeleRow"/> by column name. cmangos calls the failure text
/// <c>status_failed_text</c>, vmangos <c>message</c> (vmangos <c>ObjectMgr::LoadAreaTriggerTeleports</c>,
/// src/game/ObjectMgr.cpp:7712-7717); vmangos rows are patch-versioned and the row with the highest
/// <c>patch</c> not above 10 wins. The entry requirements <c>required_item</c>, <c>required_item2</c>,
/// <c>required_quest_done</c> and the conditions reference (<c>condition_id</c> in classic-db, <c>required_condition</c> in
/// vmangos) are read into the row and enforced by <c>AreaTriggerRequirements</c>; the heroic-key columns of later cores do
/// not exist in the 1.12 dumps. Relation rows are filtered by <c>patch_min</c>/<c>patch_max</c> like the other quest relations. The trigger shapes themselves
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
        ["condition_id"] = nameof(AreaTriggerTeleportRow.RequiredCondition),
    });

    private static readonly RowMapper<GameTeleRow> s_teleMapper = new();

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<uint, (int Patch, AreaTriggerTeleportRow Row)> _portals = [];
    private readonly Dictionary<uint, GameTeleRow> _teleports = [];
    private readonly Dictionary<(uint Id, uint Quest), AreaTriggerQuestRow> _questTriggers = [];
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
                case "areatrigger_involvedrelation":
                    ReadQuestTrigger(row);
                    break;
                case "game_tele":
                    uint[] key = _keys.Read(row, "game_tele");
                    _teleports[key[0]] = s_teleMapper.Map(row, _diagnostics);
                    break;
            }
        }
    }

    /// <summary>The rows that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<AreaTriggerTeleportRow> Portals, IReadOnlyCollection<GameTeleRow> Teleports, IReadOnlyCollection<AreaTriggerQuestRow> QuestTriggers) Snapshot()
        => ([.. _portals.Values.Select(p => p.Row)], [.. _teleports.Values], [.. _questTriggers.Values]);

    public LocationImportReport BuildReport() => new(_portals.Count, _teleports.Count, _skipped, _diagnostics.Samples, _questTriggers.Count);

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
                    await db.Set<AreaTriggerQuestRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<GameTeleRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, snapshot.Portals, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.QuestTriggers, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Teleports, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    /// <summary>Replace only the portal table from this dump; GM teleports and exploration relations are untouched.</summary>
    public async Task<int> ReplacePortalsAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyCollection<AreaTriggerTeleportRow> portals = Snapshot().Portals;
        if (portals.Count == 0)
            throw new ImportSchemaException("the dump has no areatrigger_teleport rows");

        await ImportTransaction.RunAsync(db, async token =>
        {
            await db.Set<AreaTriggerTeleportRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
            await ImportBatch.InsertAsync(db, portals, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return portals.Count;
    }

    // vmangos progressive rows: patch_min <= 10 <= patch_max (a table without the columns always applies), as the other quest relations.
    private void ReadQuestTrigger(DumpRow row)
    {
        uint[] key = _keys.Read(row, "areatrigger_involvedrelation");
        if (row.TryGet(out string? min, "patch_min") && !string.IsNullOrEmpty(min))
        {
            double lower = double.Parse(min, NumberStyles.Float, CultureInfo.InvariantCulture);
            double upper = row.TryGet(out string? max, "patch_max") && !string.IsNullOrEmpty(max)
                ? double.Parse(max, NumberStyles.Float, CultureInfo.InvariantCulture)
                : MaxPatch;
            if (lower > MaxPatch || upper < MaxPatch)
            {
                _skipped++;
                return;
            }
        }

        _questTriggers[(key[0], key[1])] = new AreaTriggerQuestRow { Id = key[0], Quest = key[1] };
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
