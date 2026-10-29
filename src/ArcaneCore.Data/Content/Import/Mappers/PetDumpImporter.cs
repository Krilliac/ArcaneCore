using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.Pets;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a pet import read and wrote.</summary>
public sealed record PetImportReport(int LevelStats, int CreateSpells, int SkippedRows, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps <c>pet_levelstats</c> and <c>petcreateinfo_spell</c> of a cmangos classic-db or vmangos dump into
/// <see cref="PetLevelStatsRow"/> and <see cref="PetCreateSpellRow"/> by column name. classic-db names the
/// columns <c>creature_entry, hp, str, agi, sta, inte, spi</c> (and has no damage columns, so
/// <c>DmgMin</c>/<c>DmgMax</c> stay 0 and the pet keeps its template damage); vmangos uses
/// <c>entry, health, strength, agility, stamina, intellect, spirit, dmg_min, dmg_max</c>
/// (<c>ObjectMgr::LoadPetLevelInfo</c>, src/game/ObjectMgr.cpp:4406). A creature with level data but none
/// for level 1 is a hard error when <see cref="ArcaneCore.Kernel.WorldData.Pets.PetContent"/> loads
/// (vmangos exits, ObjectMgr.cpp:4492), so the importer drops such a creature and says so rather than
/// writing a table the world cannot start with. The dumps are GPL data and are never committed.
/// </summary>
public sealed class PetDumpImporter
{
    private const string LevelTable = "pet_levelstats";
    private const string SpellTable = "petcreateinfo_spell";

    private static readonly RowMapper<PetLevelStatsRow> s_levelMapper = new(new Dictionary<string, string>
    {
        ["creature_entry"] = nameof(PetLevelStatsRow.Entry),
        ["hp"] = nameof(PetLevelStatsRow.Health),
        ["str"] = nameof(PetLevelStatsRow.Strength),
        ["agi"] = nameof(PetLevelStatsRow.Agility),
        ["sta"] = nameof(PetLevelStatsRow.Stamina),
        ["inte"] = nameof(PetLevelStatsRow.Intellect),
        ["spi"] = nameof(PetLevelStatsRow.Spirit),
    });

    private static readonly RowMapper<PetCreateSpellRow> s_spellMapper = new();

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<(uint Entry, uint Level), PetLevelStatsRow> _levels = [];
    private readonly Dictionary<uint, PetCreateSpellRow> _spells = [];
    private readonly MapDiagnostics _diagnostics = new();

    /// <summary>Whether a <c>pet_levelstats</c> column is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsLevelColumn(string column) => s_levelMapper.Maps(column);

    /// <summary>Whether a <c>petcreateinfo_spell</c> column is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsSpellColumn(string column) => s_spellMapper.Maps(column);

    /// <summary>The level rows that would be written (creatures without level 1 data removed), ordered by entry and level.</summary>
    public IReadOnlyList<PetLevelStatsRow> LevelStats => Resolve().Rows;

    /// <summary>The create-spell rows that would be written, ordered by entry.</summary>
    public IReadOnlyList<PetCreateSpellRow> CreateSpells => [.. _spells.Values.OrderBy(r => r.Entry)];

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

            if (row.Table.Equals(LevelTable, StringComparison.OrdinalIgnoreCase))
            {
                uint[] key = _keys.Read(row, LevelTable);
                PetLevelStatsRow mapped = s_levelMapper.Map(row, _diagnostics);
                mapped.Entry = key[0];
                _levels[(key[0], key[1])] = mapped;
            }
            else if (row.Table.Equals(SpellTable, StringComparison.OrdinalIgnoreCase))
            {
                uint[] key = _keys.Read(row, SpellTable);
                PetCreateSpellRow mapped = s_spellMapper.Map(row, _diagnostics);
                mapped.Entry = key[0];
                _spells[key[0]] = mapped;
            }
        }
    }

    public PetImportReport BuildReport()
    {
        (List<PetLevelStatsRow> rows, int skipped, List<string> warnings) = Resolve();
        warnings.AddRange(_diagnostics.Samples);
        return new PetImportReport(rows.Count, _spells.Count, skipped, warnings);
    }

    /// <summary>
    /// Write the rows atomically with the contract of <see cref="ImportTransaction"/>. With
    /// <paramref name="replace"/> both tables are emptied first; without it an existing key fails the
    /// write and nothing changes.
    /// </summary>
    public async Task<PetImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyList<PetLevelStatsRow> levels = LevelStats;
        IReadOnlyList<PetCreateSpellRow> spells = CreateSpells;
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<PetLevelStatsRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<PetCreateSpellRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, levels, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, spells, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    private (List<PetLevelStatsRow> Rows, int Skipped, List<string> Warnings) Resolve()
    {
        var rows = new List<PetLevelStatsRow>();
        var warnings = new List<string>();
        int skipped = 0;
        foreach (IGrouping<uint, KeyValuePair<(uint Entry, uint Level), PetLevelStatsRow>> entry in _levels.GroupBy(l => l.Key.Entry).OrderBy(g => g.Key))
        {
            bool usesLevels = entry.Any(l => l.Key.Level is >= 1 and <= Kernel.WorldData.Pets.PetContent.MaxLevel);
            bool hasLevelOne = entry.Any(l => l.Key.Level == 1 && l.Value.Health > 0);
            if (usesLevels && !hasLevelOne)
            {
                skipped += entry.Count();
                warnings.Add($"pet_levelstats: creature {entry.Key} has no stats for level 1 (health above 0), so its {entry.Count()} row(s) were not imported");
                continue;
            }

            rows.AddRange(entry.OrderBy(l => l.Key.Level).Select(l => l.Value));
        }

        return (rows, skipped, warnings);
    }
}
