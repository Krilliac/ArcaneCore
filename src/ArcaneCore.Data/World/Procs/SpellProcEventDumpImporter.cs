using System.Globalization;
using System.Numerics;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.WorldData.Procs;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Procs;

/// <summary>The rows <see cref="SpellProcEventDumpImporter.Parse"/> kept, with what it read and what the build filter dropped.</summary>
public sealed record SpellProcEventParseResult(SpellProcEventContent Content, int RowsRead, int RowsFilteredByBuild);

/// <summary>The unit of a dump's <c>Cooldown</c> column.</summary>
public enum ProcCooldownUnit
{
    /// <summary>vmangos and cmangos-classic from z2829 on (<c>z2829_01_mangos_proc_cooldown.sql</c> multiplied the column by 1000).</summary>
    Milliseconds,

    /// <summary>classic-db dumps before z2829 (the Full_DB z2815 file stores 3 for Hand of Justice's 3 s).</summary>
    Seconds,
}

/// <summary>
/// Reads <c>spell_proc_event</c> out of a mysqldump-style SQL file, by COLUMN NAME (case-insensitive), from the dump's own <c>CREATE TABLE</c>
/// or the column list of an <c>INSERT</c>. Two dialects:
/// <list type="bullet">
/// <item>cmangos-classic / classic-db (<c>entry, SchoolMask, SpellFamilyName, SpellFamilyMask0..2, procFlags, procEx, ppmRate, CustomChance,
/// Cooldown</c>): no build range.</item>
/// <item>vmangos (the same columns plus <c>build_min, build_max</c>): only the rows whose build range holds the supported build 5875 are kept
/// (vmangos SpellMgr::LoadSpellProcEvents, Spells/SpellMgr.cpp:321).</item>
/// </list>
/// Nothing is bundled: the operator points the importer at their own dump. Parsing completes before the database is touched and the import is one
/// transaction, so a mangled file changes nothing. Two kept rows for one spell are an error (fail closed).
/// </summary>
public static class SpellProcEventDumpImporter
{
    /// <summary>The client build the content serves (vmangos SUPPORTED_CLIENT_BUILD).</summary>
    public const int SupportedBuild = 5875;

    /// <summary>Parse the table out of <paramref name="dump"/>; throws <see cref="InvalidDataException"/> on anything malformed.</summary>
    public static SpellProcEventParseResult Parse(TextReader dump, int build = SupportedBuild, ProcCooldownUnit cooldownUnit = ProcCooldownUnit.Milliseconds)
    {
        ArgumentNullException.ThrowIfNull(dump);
        List<string>? createColumns = null;
        var rows = new List<(IReadOnlyList<string> Columns, string[] Values)>();
        bool creating = false;
        string table = SpellProcEventDataModule.Table;
        while (dump.ReadLine() is { } line)
        {
            if (creating)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith('`'))
                {
                    int end = trimmed.IndexOf('`', 1);
                    createColumns!.Add(end < 0 ? throw new InvalidDataException($"unterminated column name in CREATE TABLE {table}") : trimmed[1..end]);
                }
                else if (trimmed.StartsWith(')'))
                {
                    creating = false;
                }

                continue;
            }

            if (line.StartsWith($"CREATE TABLE `{table}` (", StringComparison.OrdinalIgnoreCase))
            {
                creating = true;
                createColumns = [];
            }
            else if (line.StartsWith($"INSERT INTO `{table}`", StringComparison.OrdinalIgnoreCase))
            {
                ParseInsert(line, createColumns, rows);
            }
        }

        return Build(rows, build, cooldownUnit);
    }

    private static SpellProcEventParseResult Build(List<(IReadOnlyList<string> Columns, string[] Values)> rows, int build, ProcCooldownUnit cooldownUnit)
    {
        var kept = new List<SpellProcEventRecord>();
        int filtered = 0;
        foreach ((IReadOnlyList<string> columns, string[] values) in rows)
        {
            if (values.Length != columns.Count)
            {
                throw new InvalidDataException($"spell_proc_event: a row has {values.Length} values but {columns.Count} columns are named");
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

            uint entry = ToUInt(Get("entry") ?? throw new InvalidDataException("spell_proc_event has no column 'entry'"), "entry");
            long buildMin = Get("build_min") is { } bmin ? ToLong(bmin, "build_min") : 0;
            long buildMax = Get("build_max") is { } bmax ? ToLong(bmax, "build_max") : long.MaxValue;
            if (build < buildMin || build > buildMax)
            {
                filtered++;
                continue;
            }

            float ppm = Get("ppmRate") is { } p ? ToFloat(p, "ppmRate") : 0f;
            float chance = Get("CustomChance") is { } c ? ToFloat(c, "CustomChance") : 0f;
            if (!float.IsFinite(ppm) || ppm < 0f || !float.IsFinite(chance) || chance < 0f)
            {
                throw new InvalidDataException($"spell_proc_event spell {entry}: ppmRate {ppm} or CustomChance {chance} is not a non-negative number");
            }

            uint cooldown = Get("Cooldown") is { } cd ? ToUInt(cd, "Cooldown") : 0;
            if (cooldownUnit == ProcCooldownUnit.Seconds)
            {
                cooldown = checked(cooldown * 1000);
            }

            kept.Add(new SpellProcEventRecord(
                entry,
                Get("SchoolMask") is { } school ? ToUInt(school, "SchoolMask") : 0,
                Get("SpellFamilyName") is { } family ? ToUInt(family, "SpellFamilyName") : 0,
                Get("SpellFamilyMask0") is { } m0 ? ToULong(m0, "SpellFamilyMask0") : 0,
                Get("SpellFamilyMask1") is { } m1 ? ToULong(m1, "SpellFamilyMask1") : 0,
                Get("SpellFamilyMask2") is { } m2 ? ToULong(m2, "SpellFamilyMask2") : 0,
                Get("procFlags") is { } flags ? ToUInt(flags, "procFlags") : 0,
                Get("procEx") is { } ex ? ToUInt(ex, "procEx") : 0,
                ppm,
                chance,
                cooldown));
        }

        return new SpellProcEventParseResult(new SpellProcEventContent(kept), rows.Count, filtered);
    }

    private static void ParseInsert(string line, List<string>? createColumns, List<(IReadOnlyList<string> Columns, string[] Values)> rows)
    {
        int i = $"INSERT INTO `{SpellProcEventDataModule.Table}`".Length;
        SkipSpaces(line, ref i);
        IReadOnlyList<string>? columns = null;
        if (i < line.Length && line[i] == '(')
        {
            int close = line.IndexOf(')', i);
            if (close < 0)
            {
                throw new InvalidDataException("spell_proc_event: unterminated INSERT column list");
            }

            columns = [.. line[(i + 1)..close].Split(',').Select(static c => c.Trim().Trim('`'))];
            i = close + 1;
            SkipSpaces(line, ref i);
        }

        if (string.Compare(line, i, "VALUES", 0, 6, StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidDataException("spell_proc_event: expected VALUES in INSERT");
        }

        i += 6;
        columns ??= createColumns ?? throw new InvalidDataException("the dump has INSERT rows for spell_proc_event but no CREATE TABLE or column list to name the columns");
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

    // (1,2,3),(4,5,6); — numbers only (optionally quoted, as some dumps write floats); anything else is malformed for this table.
    private static List<string[]> ParseTuples(string text, int i)
    {
        var tuples = new List<string[]>();
        while (true)
        {
            SkipSpaces(text, ref i);
            if (i >= text.Length || text[i] != '(')
            {
                throw new InvalidDataException($"spell_proc_event: expected '(' at offset {i}");
            }

            i++;
            var values = new List<string>();
            while (true)
            {
                SkipSpaces(text, ref i);
                bool quoted = i < text.Length && text[i] == '\'';
                if (quoted)
                {
                    i++;
                }

                int start = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '-' or '+' or '.' or 'e' or 'E'))
                {
                    i++;
                }

                if (i == start)
                {
                    throw new InvalidDataException($"spell_proc_event: not a number at offset {start}");
                }

                values.Add(text[start..i]);
                if (quoted)
                {
                    if (i >= text.Length || text[i] != '\'')
                    {
                        throw new InvalidDataException($"spell_proc_event: unterminated quoted number at offset {start}");
                    }

                    i++;
                }

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
                throw new InvalidDataException($"spell_proc_event: expected ')' at offset {i}");
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

            throw new InvalidDataException($"spell_proc_event: expected ',' or the end of the statement at offset {i}");
        }
    }

    private static long ToLong(string text, string column)
        => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
            ? value
            : throw new InvalidDataException($"spell_proc_event column '{column}': '{text}' is not an integer");

    private static uint ToUInt(string text, string column)
    {
        long value = ToLong(text, column);
        return value is >= 0 and <= uint.MaxValue ? (uint)value : throw new InvalidDataException($"spell_proc_event column '{column}': {value} is out of range");
    }

    // The family masks are bigint unsigned: up to 2^64 - 1, beyond a long.
    private static ulong ToULong(string text, string column)
        => BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger value) && value >= 0 && value <= ulong.MaxValue
            ? (ulong)value
            : throw new InvalidDataException($"spell_proc_event column '{column}': '{text}' is not an unsigned 64-bit integer");

    private static float ToFloat(string text, string column)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new InvalidDataException($"spell_proc_event column '{column}': '{text}' is not a number");

    /// <summary>
    /// Replace the table with <paramref name="content"/> in one transaction (a failure leaves the previous rows). Rows are stored for the supported
    /// build (build range 0..9999). Returns the number of rows written.
    /// </summary>
    public static async Task<int> ImportAsync(WorldDbContext db, SpellProcEventContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(content);
        SpellProcEventRow[] rows = [.. content.Rows.OrderBy(r => r.Entry).Select(SpellProcEventDataModule.ToRow)];

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Set<SpellProcEventRow>().RemoveRange(await db.Set<SpellProcEventRow>().ToListAsync(cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.Set<SpellProcEventRow>().AddRange(rows);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return rows.Length;
    }
}
