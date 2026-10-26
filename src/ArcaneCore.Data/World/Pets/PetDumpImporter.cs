using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Pets;

/// <summary>What a pet data import read and wrote.</summary>
/// <param name="LevelStats">Rows for <c>pet_levelstats</c>.</param>
/// <param name="CreateSpells">Rows for <c>petcreateinfo_spell</c>.</param>
/// <param name="SkippedRows">Source rows ignored as invalid.</param>
/// <param name="Warnings">Human readable notes (skipped rows, entries dropped).</param>
public sealed record PetImportReport(int LevelStats, int CreateSpells, int SkippedRows, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps the pet tables of a cmangos classic-db dump into <c>pet_levelstats</c> and <c>petcreateinfo_spell</c> by column
/// <b>name</b>. classic-db <c>pet_levelstats</c> is <c>creature_entry, level, hp, mana, armor, str, agi, sta, inte, spi</c>
/// (no damage columns, so <c>DmgMin</c>/<c>DmgMax</c> stay 0 and the creature keeps its own damage; docs/integration/pets.md),
/// <c>petcreateinfo_spell</c> is <c>entry, Spell1..Spell4</c>. The vmangos names (<c>entry</c>, <c>health</c>, ...) are read too.
/// Row validity follows vmangos <c>LoadPetLevelInfo</c> (ObjectMgr.cpp:4406-4516): a level outside 1..60 is ignored and a
/// creature with no level 1 row with health is dropped (vmangos exits; <c>PetContent</c> would refuse to start the world).
/// The sibling table <c>pet_levelstats_copy</c> of the dump is not read.
/// </summary>
public sealed class PetDumpImporter
{
    private readonly Dictionary<(uint Entry, byte Level), PetLevelStatsRow> _stats = [];
    private readonly Dictionary<uint, PetCreateSpellRow> _spells = [];
    private readonly List<string> _warnings = [];
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

            if (row.Table.Equals("pet_levelstats", StringComparison.OrdinalIgnoreCase))
            {
                ReadLevelStats(row);
            }
            else if (row.Table.Equals("petcreateinfo_spell", StringComparison.OrdinalIgnoreCase))
            {
                ReadCreateSpells(row);
            }
        }
    }

    /// <summary>The rows that would be written (entries without a usable level 1 row are dropped), ordered by key.</summary>
    public (IReadOnlyList<PetLevelStatsRow> Stats, IReadOnlyList<PetCreateSpellRow> Spells) Resolve()
    {
        HashSet<uint> usable = [.. _stats.Values.Where(r => r.Level == 1 && r.Health > 0).Select(r => r.Entry)];
        PetLevelStatsRow[] stats = [.. _stats.Values.Where(r => usable.Contains(r.Entry)).OrderBy(r => r.Entry).ThenBy(r => r.Level)];
        PetCreateSpellRow[] spells = [.. _spells.Values.OrderBy(r => r.Entry)];
        return (stats, spells);
    }

    public PetImportReport BuildReport()
    {
        (IReadOnlyList<PetLevelStatsRow> stats, IReadOnlyList<PetCreateSpellRow> spells) = Resolve();
        var warnings = new List<string>(_warnings);
        int dropped = _stats.Count - stats.Count;
        if (dropped > 0)
        {
            warnings.Add($"pet_levelstats: {dropped} row(s) dropped because their creature has no level 1 row with health");
        }

        return new PetImportReport(stats.Count, spells.Count, _skipped, warnings);
    }

    /// <summary>
    /// Write both tables with the shared content-import transaction contract: with <paramref name="replace"/> they are
    /// emptied first; otherwise an existing key fails the import and rolls it back.
    /// </summary>
    public async Task<PetImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        (IReadOnlyList<PetLevelStatsRow> stats, IReadOnlyList<PetCreateSpellRow> spells) = Resolve();
        await ImportTransaction.RunAsync(db, async token =>
        {
            if (replace)
            {
                await db.Set<PetLevelStatsRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                await db.Set<PetCreateSpellRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
            }

            await ImportBatch.InsertAsync(db, stats, token).ConfigureAwait(false);
            await ImportBatch.InsertAsync(db, spells, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return BuildReport();
    }

    private void ReadLevelStats(DumpRow row)
    {
        if (!TryUInt(row, out uint entry, "creature_entry", "entry")
            || !TryUInt(row, out uint level, "level") || level is < 1 or > 60)
        {
            Skip("pet_levelstats: missing or out of range creature entry/level (levels 1..60 only), ignoring");
            return;
        }

        if (!TryUInt(row, out uint health, "hp", "health") || !TryUInt(row, out uint mana, "mana")
            || !TryUInt(row, out uint armor, "armor") || !TryUInt(row, out uint strength, "str", "strength")
            || !TryUInt(row, out uint agility, "agi", "agility") || !TryUInt(row, out uint stamina, "sta", "stamina")
            || !TryUInt(row, out uint intellect, "inte", "intellect") || !TryUInt(row, out uint spirit, "spi", "spirit"))
        {
            Skip($"pet_levelstats: entry {entry} level {level} has a missing or invalid stat column, ignoring");
            return;
        }

        _stats[(entry, (byte)level)] = new PetLevelStatsRow
        {
            Entry = entry, Level = (byte)level, Health = health, Mana = mana, Armor = armor,
            DmgMin = TryFloat(row, "dmg_min"), DmgMax = TryFloat(row, "dmg_max"),
            Strength = strength, Agility = agility, Stamina = stamina, Intellect = intellect, Spirit = spirit,
        };
    }

    private void ReadCreateSpells(DumpRow row)
    {
        if (!TryUInt(row, out uint entry, "entry"))
        {
            Skip("petcreateinfo_spell: missing or invalid entry, ignoring");
            return;
        }

        TryUInt(row, out uint spell1, "Spell1");
        TryUInt(row, out uint spell2, "Spell2");
        TryUInt(row, out uint spell3, "Spell3");
        TryUInt(row, out uint spell4, "Spell4");
        _spells[entry] = new PetCreateSpellRow { Entry = entry, Spell1 = spell1, Spell2 = spell2, Spell3 = spell3, Spell4 = spell4 };
    }

    private void Skip(string message)
    {
        _skipped++;
        if (_warnings.Count < 20)
        {
            _warnings.Add(message);
        }
    }

    private static bool TryUInt(DumpRow row, out uint value, params ReadOnlySpan<string> names)
    {
        value = 0;
        return row.TryGet(out string? text, names) && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static float TryFloat(DumpRow row, string name)
        => row.TryGet(out string? text, name) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : 0f;
}
