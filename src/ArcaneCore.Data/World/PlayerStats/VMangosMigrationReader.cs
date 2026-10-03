using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.World.Creatures;

namespace ArcaneCore.Data.World.PlayerStats;

/// <summary>A statement of a vmangos world migration that touches one of the tables being tracked.</summary>
public abstract record MigrationStatement(string Table);

/// <summary>
/// <c>UPDATE `t` SET `a`=1, `b`=2 WHERE `x`=3 AND `y`=4</c> (also with <c>&amp;&amp;</c>); numeric values only.
/// </summary>
public sealed record MigrationUpdate(string Table, IReadOnlyList<(string Column, double Value)> Set, IReadOnlyList<(string Column, double Value)> Where)
    : MigrationStatement(Table);

/// <summary>One row of an <c>INSERT INTO `t` (cols) VALUES (...)</c>; the statement must carry a column list.</summary>
public sealed record MigrationInsert(string Table, DumpRow Row) : MigrationStatement(Table);

/// <summary>
/// Reads the statements of a vmangos <c>sql/migrations/*_world.sql</c> file that touch a given set of tables.
/// A migration is a stored-procedure wrapper (<c>DELIMITER ??</c>, <c>CREATE PROCEDURE add_migration()</c>,
/// <c>IF v = 0 THEN INSERT INTO migrations … END IF</c>) around ordinary statements, so the file is split at
/// top-level semicolons (honouring quotes, back-ticks and <c>--</c>, <c>#</c> and block comments) and each
/// statement is classified on its own. The wrapper statements never mention a tracked table and are ignored.
/// <para>
/// Supported on a tracked table: <c>CREATE TABLE</c> (ignored: the schema is this repository's own),
/// <c>INSERT</c>/<c>REPLACE</c> with a column list, and <c>UPDATE … SET numeric … WHERE numeric conditions</c>.
/// Any other statement on a tracked table (<c>DELETE</c>, <c>TRUNCATE</c>, <c>ALTER</c>, <c>DROP</c>,
/// <c>RENAME</c>, an <c>UPDATE</c> without <c>WHERE</c> or with a non-numeric value) throws
/// <see cref="NotSupportedException"/>: ignoring it would silently diverge from the reference (ROADMAP:
/// fail closed). The migrations are GPL data and are never committed; tests use hand-written statements in
/// the same shapes.
/// </para>
/// </summary>
public static partial class VMangosMigrationReader
{
    [GeneratedRegex(@"\b(?<verb>UPDATE|INSERT|REPLACE|DELETE\s+FROM|TRUNCATE|ALTER\s+TABLE|DROP\s+TABLE|CREATE\s+TABLE|RENAME\s+TABLE)\s+(?:IGNORE\s+|INTO\s+|TABLE\s+|IF\s+NOT\s+EXISTS\s+|IF\s+EXISTS\s+|LOW_PRIORITY\s+)*`?(?<table>\w+)`?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerbPattern();

    [GeneratedRegex(@"^UPDATE\s+(?:IGNORE\s+)?`?(?<table>\w+)`?\s+SET\s+(?<set>.+?)\s+WHERE\s+(?<where>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UpdatePattern();

    [GeneratedRegex(@"^`?(?<col>\w+)`?\s*=\s*(?<value>-?\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"\s+(?:AND|&&)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AndPattern();

    /// <summary>The tracked statements of one migration, in file order.</summary>
    /// <param name="sql">The migration text.</param>
    /// <param name="name">The migration's name, used in error messages.</param>
    /// <param name="tables">Lower-case names of the tables to track.</param>
    public static IReadOnlyList<MigrationStatement> Parse(TextReader sql, string name, IReadOnlySet<string> tables)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(tables);
        var result = new List<MigrationStatement>();
        foreach (string statement in Split(sql.ReadToEnd()))
        {
            Match verb = VerbPattern().Match(statement);
            if (!verb.Success)
            {
                continue;
            }

            string table = verb.Groups["table"].Value.ToLowerInvariant();
            if (!tables.Contains(table))
            {
                continue;
            }

            string kind = Regex.Replace(verb.Groups["verb"].Value.ToUpperInvariant(), @"\s+", " ");
            switch (kind)
            {
                case "CREATE TABLE":
                    break;
                case "UPDATE":
                    result.Add(ParseUpdate(statement, name));
                    break;
                case "INSERT":
                case "REPLACE":
                    result.AddRange(ParseInsert(statement, table, name));
                    break;
                default:
                    throw new NotSupportedException($"migration {name}: {kind} on tracked table `{table}` is not supported: {Abbreviate(statement)}");
            }
        }

        return result;
    }

    private static MigrationUpdate ParseUpdate(string statement, string name)
    {
        Match match = UpdatePattern().Match(statement.Substring(statement.IndexOf("UPDATE", StringComparison.OrdinalIgnoreCase)));
        if (!match.Success)
        {
            throw new NotSupportedException($"migration {name}: UPDATE without SET … WHERE is not supported: {Abbreviate(statement)}");
        }

        var set = new List<(string, double)>();
        foreach (string part in match.Groups["set"].Value.Split(','))
        {
            set.Add(ParseAssignment(part, name, statement));
        }

        var where = new List<(string, double)>();
        foreach (string part in AndPattern().Split(match.Groups["where"].Value.Trim()))
        {
            where.Add(ParseAssignment(part, name, statement));
        }

        return new MigrationUpdate(match.Groups["table"].Value.ToLowerInvariant(), set, where);
    }

    private static (string, double) ParseAssignment(string text, string name, string statement)
    {
        Match match = AssignmentPattern().Match(text.Trim());
        if (!match.Success)
        {
            throw new NotSupportedException($"migration {name}: only `column` = number terms are supported, found '{text.Trim()}' in: {Abbreviate(statement)}");
        }

        return (match.Groups["col"].Value.ToLowerInvariant(), double.Parse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    private static List<MigrationInsert> ParseInsert(string statement, string table, string name)
    {
        int start = VerbPattern().Match(statement).Index;
        var rows = new List<MigrationInsert>();
        try
        {
            foreach (object item in new MySqlDumpReader(new StringReader(statement[start..] + ";")).Read())
            {
                if (item is DumpRow row)
                {
                    if (row.Columns.Count == 0 || row.Columns.Count != row.Values.Count)
                    {
                        throw new NotSupportedException($"migration {name}: INSERT into `{table}` needs a column list: {Abbreviate(statement)}");
                    }

                    rows.Add(new MigrationInsert(table, row));
                }
            }
        }
        catch (FormatException error)
        {
            // The dump reader cannot name the columns of an INSERT without a column list for a table it never saw defined.
            throw new NotSupportedException($"migration {name}: INSERT into `{table}` could not be read (a column list is required): {Abbreviate(statement)}", error);
        }

        return rows;
    }
    /// <summary>Split at top-level semicolons, dropping comments; whitespace is collapsed to single spaces.</summary>
    internal static IEnumerable<string> Split(string text)
    {
        var current = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '\'' or '"' or '`')
            {
                int end = SkipQuoted(text, i);
                current.Append(text, i, end - i);
                i = end;
            }
            else if ((c == '-' && i + 1 < text.Length && text[i + 1] == '-' && (i + 2 >= text.Length || char.IsWhiteSpace(text[i + 2]))) || c == '#')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                current.Append(' ');
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                current.Append(' ');
            }
            else if (c == ';')
            {
                string statement = Collapse(current);
                current.Clear();
                if (statement.Length > 0)
                {
                    yield return statement;
                }

                i++;
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        string last = Collapse(current);
        if (last.Length > 0)
        {
            yield return last;
        }
    }

    private static int SkipQuoted(string text, int start)
    {
        char quote = text[start];
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && quote != '`')
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static string Collapse(StringBuilder text) => Regex.Replace(text.ToString(), @"\s+", " ").Trim();

    private static string Abbreviate(string statement) => statement.Length <= 160 ? statement : statement[..160] + "…";
}
