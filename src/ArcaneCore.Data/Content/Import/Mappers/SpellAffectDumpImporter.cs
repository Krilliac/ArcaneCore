using System.Globalization;
using ArcaneCore.Data.World.Creatures;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a spell-affect import read.</summary>
/// <param name="Rows">Rows kept (one per spell and effect).</param>
/// <param name="SkippedRows">Rows that could not be read (an unparsable mask, an effect index above 2).</param>
/// <param name="WideMasks">Kept rows whose mask needs more than 32 bits: the spell DBC's 32-bit EffectItemType cannot hold them.</param>
/// <param name="ZeroMasks">Kept rows with an empty mask (they affect no spell).</param>
public sealed record SpellAffectImportReport(int Rows, int SkippedRows, int WideMasks, int ZeroMasks, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads <c>spell_affect</c> of a cmangos classic-db dump (entry, effectId, SpellFamilyMask: the 64-bit class mask of a
/// spell-modifier aura effect, the table cmangos used before the mask moved into spell_template) and writes it as the text overlay
/// <c>Spells:Mods:ClassMaskFile</c> reads: one line per row, <c>spellId effectIndex 0xMASK</c> (16 hex digits), sorted by spell and
/// effect, <c>#</c> lines are comments. The game's spell table reads the DBC's 32-bit EffectItemType, so masks above bit 31 (about
/// 11% of the classic rows) are only correct through this file. The overlay is data the operator generates outside the repository
/// (GPL source, never committed). vmangos' own corrections to the masks (sql/migrations/20240926142033_world.sql) need the imported
/// spell data to evaluate their guard values and are not applied (docs/areas/spell-mods.md).
/// </summary>
public sealed class SpellAffectDumpImporter
{
    private readonly SortedDictionary<(uint Spell, int Effect), ulong> _masks = [];
    private readonly List<string> _warnings = [];
    private int _skipped;

    /// <summary>The masks read so far, by spell and effect (later rows replace earlier ones).</summary>
    public IReadOnlyDictionary<(uint Spell, int Effect), ulong> Masks => _masks;

    /// <summary>Read one dump (call again for further files).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && row.Table.Equals("spell_affect", StringComparison.OrdinalIgnoreCase))
            {
                ReadRow(row);
            }
        }
    }

    public SpellAffectImportReport BuildReport()
        => new(_masks.Count, _skipped, _masks.Values.Count(m => m > uint.MaxValue), _masks.Values.Count(m => m == 0), [.. _warnings]);

    /// <summary>Write the overlay file text (LF line ends).</summary>
    public void WriteOverlay(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write("# spell, effect index, class mask (hex); written by arcane-content-importer from spell_affect; set Spells:Mods:ClassMaskFile to this file\n");
        foreach (((uint spell, int effect), ulong mask) in _masks)
        {
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"{spell} {effect} 0x{mask:X16}\n"));
        }
    }

    private void Warn(string message)
    {
        if (_warnings.Count < 20)
        {
            _warnings.Add(message);
        }
    }

    private void ReadRow(DumpRow row)
    {
        if (!row.TryGet(out string? entry, "entry") || !row.TryGet(out string? effect, "effectId", "effect_index", "effectIndex")
            || !row.TryGet(out string? mask, "SpellFamilyMask", "mask"))
        {
            _skipped++;
            Warn("spell_affect row without entry, effectId and SpellFamilyMask columns");
            return;
        }

        if (!uint.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out uint spellId)
            || !int.TryParse(effect, NumberStyles.None, CultureInfo.InvariantCulture, out int effectIndex) || effectIndex is < 0 or > 2
            || !ulong.TryParse(mask, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
        {
            _skipped++;
            Warn($"spell_affect row '{entry}', '{effect}', '{mask}' is not (spell, effect 0-2, unsigned 64-bit mask)");
            return;
        }

        _masks[(spellId, effectIndex)] = value;
    }
}
