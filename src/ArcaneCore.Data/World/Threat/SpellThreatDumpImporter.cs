using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.WorldData.Threat;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Threat;

/// <summary>The rows <see cref="SpellThreatDumpImporter.Parse"/> kept, with what it read and what the build filter dropped.</summary>
public sealed record SpellThreatParseResult(SpellThreatContent Content, int RowsRead, int RowsFilteredByBuild);

/// <summary>
/// Reads <c>spell_threat</c> out of a mysqldump-style SQL file, by COLUMN NAME (case-insensitive: the dumps mix <c>Threat</c> and
/// <c>threat</c>), from the dump's own <c>CREATE TABLE</c> or the column list of an <c>INSERT</c>. Two dialects:
/// <list type="bullet">
/// <item>cmangos-classic / classic-db (<c>entry, Threat, multiplier, ap_bonus</c>, sql/base/mangos.sql, Full_DB z2815): no build range;
/// <c>ap_bonus</c> must be zero because nothing applies it.</item>
/// <item>vmangos (<c>entry, threat, multiplier, inverse_effect_mask, build_min, build_max</c>): only the rows whose build range holds the
/// supported build 5875 are kept (vmangos SpellMgr::LoadSpellThreats, Spells/SpellMgr.cpp:839).</item>
/// </list>
/// Nothing is bundled: the operator points the importer at their own dump. Parsing completes before the database is touched and the import is
/// one transaction, so a mangled file changes nothing. Two kept rows for one spell are an error (fail closed).
/// </summary>
public static class SpellThreatDumpImporter
{
    /// <summary>The client build the content serves (vmangos SUPPORTED_CLIENT_BUILD).</summary>
    public const int SupportedBuild = 5875;

    /// <summary>Parse the table out of <paramref name="dump"/>; throws <see cref="InvalidDataException"/> on anything malformed.</summary>
    public static SpellThreatParseResult Parse(TextReader dump, int build = SupportedBuild)
    {
        ArgumentNullException.ThrowIfNull(dump);
        List<string>? createColumns = null;
        var rows = new List<(IReadOnlyList<string> Columns, string[] Values)>();
        bool creating = false;
        while (dump.ReadLine() is { } line)
        {
            if (creating)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith('`'))
                {
                    int end = trimmed.IndexOf('`', 1);
                    createColumns!.Add(end < 0 ? throw new InvalidDataException("unterminated column name in CREATE TABLE spell_threat") : trimmed[1..end]);
                }
                else if (trimmed.StartsWith(')'))
                {
                    creating = false;
                }

                continue;
            }

            if (line.StartsWith($"CREATE TABLE `{SpellThreatDataModule.Table}` (", StringComparison.OrdinalIgnoreCase))
            {
                creating = true;
                createColumns = [];
            }
            else if (line.StartsWith($"INSERT INTO `{SpellThreatDataModule.Table}`", StringComparison.OrdinalIgnoreCase))
            {
                ParseInsert(line, createColumns, rows);
            }
        }

        return Build(rows, build);
    }

    private static SpellThreatParseResult Build(List<(IReadOnlyList<string> Columns, string[] Values)> rows, int build)
    {
        var kept = new List<SpellThreatRecord>();
        int filtered = 0;
        foreach ((IReadOnlyList<string> columns, string[] values) in rows)
        {
            if (values.Length != columns.Count)
            {
                throw new InvalidDataException($"spell_threat: a row has {values.Length} values but {columns.Count} columns are named");
            }

            string? Get(string name)
            {
                for (int i = 0; i < columns.Count; i++)
                {
                    if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return values[i];
                    }
                }

                return null;
            }

            uint entry = ToUInt(Get("entry") ?? throw new InvalidDataException("spell_threat has no column 'entry'"), "entry");
            string threatText = Get("threat") ?? throw new InvalidDataException("spell_threat has no column 'threat'");
            long threat = ToLong(threatText, "threat");
            if (threat is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException($"spell_threat spell {entry}: threat {threat} is outside 0..65535 (vmangos reads a uint16)");
            }

            float multiplier = Get("multiplier") is { } m ? ToFloat(m, "multiplier") : 1f;
            if (!float.IsFinite(multiplier) || multiplier < 0f)
            {
                throw new InvalidDataException($"spell_threat spell {entry}: multiplier {multiplier} is not a non-negative number");
            }

            if (Get("ap_bonus") is { } ap && ToFloat(ap, "ap_bonus") != 0f)
            {
                throw new InvalidDataException($"spell_threat spell {entry}: ap_bonus is not zero; nothing applies an attack power threat bonus, so the row cannot be honoured");
            }

            long mask = Get("inverse_effect_mask") is { } im ? ToLong(im, "inverse_effect_mask") : 0;
            if (mask is < 0 or > byte.MaxValue)
            {
                throw new InvalidDataException($"spell_threat spell {entry}: inverse_effect_mask {mask} does not fit a byte");
            }

            long buildMin = Get("build_min") is { } bmin ? ToLong(bmin, "build_min") : 0;
            long buildMax = Get("build_max") is { } bmax ? ToLong(bmax, "build_max") : long.MaxValue;
            if (build < buildMin || build > buildMax)
            {
                filtered++;
                continue;
            }

            kept.Add(new SpellThreatRecord(entry, (ushort)threat, multiplier, (byte)mask));
        }

        return new SpellThreatParseResult(new SpellThreatContent(kept), rows.Count, filtered);
    }

    private static void ParseInsert(string line, List<string>? createColumns, List<(IReadOnlyList<string> Columns, string[] Values)> rows)
    {
        int i = $"INSERT INTO `{SpellThreatDataModule.Table}`".Length;
        SkipSpaces(line, ref i);
        IReadOnlyList<string>? columns = null;
        if (i < line.Length && line[i] == '(')
        {
            int close = line.IndexOf(')', i);
            if (close < 0)
            {
                throw new InvalidDataException("spell_threat: unterminated INSERT column list");
            }

            columns = [.. line[(i + 1)..close].Split(',').Select(static c => c.Trim().Trim('`'))];
            i = close + 1;
            SkipSpaces(line, ref i);
        }

        if (string.Compare(line, i, "VALUES", 0, 6, StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidDataException("spell_threat: expected VALUES in INSERT");
        }

        i += 6;
        columns ??= createColumns ?? throw new InvalidDataException("the dump has INSERT rows for spell_threat but no CREATE TABLE or column list to name the columns");
        foreach (string[] tuple in ParseTuples(line, i))
        {
            rows.Add((columns, tuple));
        }
    }

    private static void SkipSpaces(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
    }

    // (1,2,3),(4,5,6); — numbers only; anything else (NULL, strings) is malformed for this table.
    private static List<string[]> ParseTuples(string text, int i)
    {
        var tuples = new List<string[]>();
        while (true)
        {
            SkipSpaces(text, ref i);
            if (i >= text.Length || text[i] != '(')
            {
                throw new InvalidDataException($"spell_threat: expected '(' at offset {i}");
            }

            i++;
            var values = new List<string>();
            while (true)
            {
                SkipSpaces(text, ref i);
                int start = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '-' or '+' or '.' or 'e' or 'E'))
                {
                    i++;
                }

                if (i == start)
                {
                    throw new InvalidDataException($"spell_threat: not a number at offset {start}");
                }

                values.Add(text[start..i]);
                SkipSpaces(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    continue;
                }

                break;
            }

            if (i >= text.Length || text[i] != ')')
            {
                throw new InvalidDataException($"spell_threat: expected ')' at offset {i}");
            }

            i++;
            tuples.Add([.. values]);
            SkipSpaces(text, ref i);
            if (i < text.Length && text[i] == ',')
            {
                i++;
                continue;
            }

            if (i >= text.Length || (text[i] == ';' && text[(i + 1)..].Trim().Length == 0))
            {
                return tuples;
            }

            throw new InvalidDataException($"spell_threat: expected ',' or the end of the statement at offset {i}");
        }
    }

    private static long ToLong(string text, string column)
        => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
            ? value
            : throw new InvalidDataException($"spell_threat column '{column}': '{text}' is not an integer");

    private static uint ToUInt(string text, string column)
    {
        long value = ToLong(text, column);
        return value is >= 0 and <= uint.MaxValue ? (uint)value : throw new InvalidDataException($"spell_threat column '{column}': {value} is out of range");
    }

    private static float ToFloat(string text, string column)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new InvalidDataException($"spell_threat column '{column}': '{text}' is not a number");

    /// <summary>
    /// Replace the table with <paramref name="content"/> in one transaction (a failure leaves the previous rows). Rows are stored for the
    /// supported build (build range 0..9999). Returns the number of rows written.
    /// </summary>
    public static async Task<int> ImportAsync(WorldDbContext db, SpellThreatContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(content);
        SpellThreatRow[] rows = [.. content.Rows.OrderBy(r => r.Entry).Select(r => new SpellThreatRow
        {
            Entry = r.Entry, Threat = r.Threat, Multiplier = r.Multiplier, InverseEffectMask = r.InverseEffectMask, BuildMin = 0, BuildMax = 9999,
        })];

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Set<SpellThreatRow>().RemoveRange(await db.Set<SpellThreatRow>().ToListAsync(cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.Set<SpellThreatRow>().AddRange(rows);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return rows.Length;
    }
}
