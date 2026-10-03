using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>A table definition seen in a dump: its name and column names in order.</summary>
public sealed record DumpTable(string Name, IReadOnlyList<string> Columns);

/// <summary>
/// One row of an <c>INSERT</c>: the table, the column names that apply (the statement's own
/// list, or the table's <c>CREATE TABLE</c> order), and the raw values (<c>null</c> for SQL
/// <c>NULL</c>, otherwise the unquoted text).
/// </summary>
public sealed record DumpRow(string Table, IReadOnlyList<string> Columns, IReadOnlyList<string?> Values)
{
    /// <summary>The value of the first column (case-insensitive) that exists, or <c>null</c> when none does.</summary>
    public bool TryGet(out string? value, params ReadOnlySpan<string> names)
    {
        foreach (string name in names)
        {
            for (int i = 0; i < Columns.Count && i < Values.Count; i++)
            {
                if (string.Equals(Columns[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    value = Values[i];
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    public bool Has(string name) => Columns.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Streams a MySQL/MariaDB dump (<c>mysqldump</c> output, as the cmangos classic-db and
/// vmangos world databases ship) and yields its table definitions and inserted rows without
/// loading the file into memory. Only what such dumps contain is understood: comments
/// (<c>--</c>, <c>#</c>, <c>/* */</c> including <c>/*!…*/</c>), <c>CREATE TABLE</c>,
/// <c>INSERT</c>/<c>REPLACE</c> with or without a column list and with extended (multi-row)
/// values, MySQL string escapes, and <c>NULL</c>. Other statements are skipped.
/// </summary>
public sealed partial class MySqlDumpReader(TextReader input)
{
    private readonly Dictionary<string, IReadOnlyList<string>> _tables = NewTableRegistry();
    private readonly Dictionary<string, int> _unapplied = new(StringComparer.Ordinal);
    private readonly StringBuilder _token = new();

    /// <summary>
    /// Read <paramref name="input"/> against a <see cref="NewTableRegistry"/> shared with other
    /// readers, so a column-less <c>INSERT</c> in a later file finds the <c>CREATE TABLE</c> of
    /// an earlier one (the real classic-db dumps use column-less INSERTs). Tables this reader
    /// defines are added to the shared registry.
    /// </summary>
    public MySqlDumpReader(TextReader input, Dictionary<string, IReadOnlyList<string>> sharedTables)
        : this(input)
    {
        ArgumentNullException.ThrowIfNull(sharedTables);
        _tables = sharedTables;
    }

    /// <summary>An empty, case-insensitive table registry to share between readers.</summary>
    public static Dictionary<string, IReadOnlyList<string>> NewTableRegistry() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Column order of every table defined so far.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Tables => _tables;

    /// <summary>
    /// Data-changing statements that are read past but not applied (a dump snapshot is only its
    /// <c>INSERT</c>/<c>REPLACE</c> rows): count per <c>"VERB table"</c> for <c>UPDATE</c>,
    /// <c>DELETE</c>, <c>ALTER</c> and <c>TRUNCATE</c>. Statements inside <c>/*! … */</c>
    /// conditional comments (mysqldump's <c>DISABLE KEYS</c>) are comments and are not counted.
    /// </summary>
    public IReadOnlyDictionary<string, int> UnappliedStatements => _unapplied;

    /// <summary>Yield <see cref="DumpTable"/> and <see cref="DumpRow"/> items in file order.</summary>
    public IEnumerable<object> Read()
    {
        while (true)
        {
            SkipWhitespaceAndComments();
            int next = input.Peek();
            if (next < 0)
            {
                yield break;
            }

            if (next == ';')
            {
                input.Read();
                continue;
            }

            string keyword = ReadWord().ToUpperInvariant();
            switch (keyword)
            {
                case "CREATE":
                    string statement = ReadStatement();
                    if (TryParseCreateTable(statement, out DumpTable? table))
                    {
                        _tables[table.Name] = table.Columns;
                        yield return table;
                    }

                    break;

                case "INSERT":
                case "REPLACE":
                    foreach (DumpRow row in ReadInsert())
                    {
                        yield return row;
                    }

                    break;

                case "":
                    // Not a statement we know how to start (stray symbol): skip to its end.
                    input.Read();
                    ReadStatement();
                    break;

                default:
                    RecordUnapplied(keyword, ReadStatement());
                    break;
            }
        }
    }

    private void RecordUnapplied(string verb, string statement)
    {
        if (verb is not ("UPDATE" or "DELETE" or "ALTER" or "TRUNCATE"))
        {
            return;
        }

        Match match = UnappliedTargetPattern().Match(statement);
        string key = verb + " " + (match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : "?");
        _unapplied[key] = _unapplied.GetValueOrDefault(key) + 1;
    }

    // Statement text after the verb: [LOW_PRIORITY|IGNORE|ONLINE|QUICK|FROM|TABLE]… then the table.
    [GeneratedRegex(@"^\s*(?:(?:LOW_PRIORITY|IGNORE|ONLINE|QUICK|FROM|TABLE)\s+)*(?:`([^`]+)`|([A-Za-z0-9_$.]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnappliedTargetPattern();

    private IEnumerable<DumpRow> ReadInsert()
    {
        // INSERT [LOW_PRIORITY|DELAYED|HIGH_PRIORITY] [IGNORE] [INTO] tbl [(cols)] VALUES (...),(...);
        string? table = null;
        while (table is null)
        {
            SkipWhitespaceAndComments();
            if (input.Peek() == '`')
            {
                table = ReadQuotedIdentifier();
                break;
            }

            string word = ReadWord();
            if (word.Length == 0)
            {
                throw new FormatException("INSERT without a table name");
            }

            if (word.ToUpperInvariant() is not ("INTO" or "IGNORE" or "LOW_PRIORITY" or "DELAYED" or "HIGH_PRIORITY"))
            {
                table = word;
            }
        }

        SkipWhitespaceAndComments();
        IReadOnlyList<string>? columns = null;
        if (input.Peek() == '(')
        {
            input.Read();
            var list = new List<string>();
            while (true)
            {
                SkipWhitespaceAndComments();
                int c = input.Peek();
                if (c == ')')
                {
                    input.Read();
                    break;
                }

                if (c == ',')
                {
                    input.Read();
                    continue;
                }

                list.Add(c == '`' ? ReadQuotedIdentifier() : ReadWord());
            }

            columns = list;
            SkipWhitespaceAndComments();
        }

        string values = ReadWord();
        if (!values.Equals("VALUES", StringComparison.OrdinalIgnoreCase) && !values.Equals("VALUE", StringComparison.OrdinalIgnoreCase))
        {
            // INSERT … SELECT / SET forms do not occur in content dumps.
            ReadStatement();
            yield break;
        }

        columns ??= _tables.GetValueOrDefault(table)
            ?? throw new FormatException($"INSERT into `{table}` before its CREATE TABLE and without a column list");

        while (true)
        {
            SkipWhitespaceAndComments();
            int c = input.Read();
            if (c < 0 || c == ';')
            {
                yield break;
            }

            if (c == ',')
            {
                continue;
            }

            if (c != '(')
            {
                throw new FormatException($"unexpected '{(char)c}' in VALUES of `{table}`");
            }

            yield return new DumpRow(table, columns, ReadTuple());
        }
    }

    private List<string?> ReadTuple()
    {
        var values = new List<string?>();
        while (true)
        {
            // Only whitespace inside a tuple: '-' starts a negative number here, not a comment.
            SkipWhitespace();
            int c = input.Peek();
            switch (c)
            {
                case < 0:
                    throw new FormatException("dump ended inside a VALUES tuple");
                case ')':
                    input.Read();
                    return values;
                case ',':
                    input.Read();
                    continue;
                case '\'':
                case '"':
                    input.Read();
                    values.Add(ReadString((char)c));
                    continue;
                default:
                    _token.Clear();
                    while ((c = input.Peek()) >= 0 && c != ',' && c != ')' && !char.IsWhiteSpace((char)c))
                    {
                        _token.Append((char)input.Read());
                    }

                    string raw = _token.ToString();
                    values.Add(raw.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : raw);
                    continue;
            }
        }
    }

    /// <summary>A quoted string after its opening quote; MySQL escapes and doubled quotes.</summary>
    private string ReadString(char quote)
    {
        _token.Clear();
        while (true)
        {
            int c = input.Read();
            if (c < 0)
            {
                throw new FormatException("dump ended inside a string");
            }

            if (c == '\\')
            {
                int e = input.Read();
                _token.Append(e switch
                {
                    '0' => '\0',
                    'b' => '\b',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'Z' => '\x1A',
                    < 0 => throw new FormatException("dump ended inside a string"),
                    _ => (char)e, // \' \" \\ \% \_ and anything else: the character itself
                });
                continue;
            }

            if (c == quote)
            {
                if (input.Peek() == quote)
                {
                    input.Read();
                    _token.Append(quote);
                    continue;
                }

                return _token.ToString();
            }

            _token.Append((char)c);
        }
    }

    private string ReadQuotedIdentifier()
    {
        input.Read(); // opening backtick
        _token.Clear();
        while (true)
        {
            int c = input.Read();
            if (c < 0)
            {
                throw new FormatException("dump ended inside an identifier");
            }

            if (c == '`')
            {
                if (input.Peek() == '`')
                {
                    input.Read();
                    _token.Append('`');
                    continue;
                }

                return _token.ToString();
            }

            _token.Append((char)c);
        }
    }

    private string ReadWord()
    {
        _token.Clear();
        int c;
        while ((c = input.Peek()) >= 0 && (char.IsLetterOrDigit((char)c) || c == '_' || c == '.' || c == '$'))
        {
            _token.Append((char)input.Read());
        }

        return _token.ToString();
    }

    /// <summary>The rest of a statement up to (not including) its ';', honouring quotes and comments.</summary>
    private string ReadStatement()
    {
        var text = new StringBuilder();
        while (true)
        {
            int c = input.Read();
            if (c < 0 || c == ';')
            {
                return text.ToString();
            }

            if (c is '\'' or '"')
            {
                text.Append((char)c).Append(ReadString((char)c).Replace("'", "''", StringComparison.Ordinal)).Append((char)c);
                continue;
            }

            if (c == '`')
            {
                text.Append('`');
                while ((c = input.Read()) >= 0 && c != '`')
                {
                    text.Append((char)c);
                }

                text.Append('`');
                continue;
            }

            if (c == '-' && input.Peek() == '-')
            {
                SkipLine();
                text.Append('\n');
                continue;
            }

            if (c == '/' && input.Peek() == '*')
            {
                input.Read();
                SkipBlockComment();
                continue;
            }

            text.Append((char)c);
        }
    }

    private void SkipWhitespace()
    {
        int c;
        while ((c = input.Peek()) >= 0 && char.IsWhiteSpace((char)c))
        {
            input.Read();
        }
    }

    private void SkipWhitespaceAndComments()
    {
        while (true)
        {
            int c = input.Peek();
            if (c < 0)
            {
                return;
            }

            if (char.IsWhiteSpace((char)c))
            {
                input.Read();
                continue;
            }

            if (c == '#')
            {
                SkipLine();
                continue;
            }

            if (c == '-')
            {
                input.Read();
                if (input.Peek() == '-')
                {
                    SkipLine();
                    continue;
                }

                throw new FormatException("unexpected '-' between statements");
            }

            if (c == '/')
            {
                input.Read();
                if (input.Peek() == '*')
                {
                    input.Read();
                    SkipBlockComment();
                    continue;
                }

                throw new FormatException("unexpected '/' between statements");
            }

            return;
        }
    }

    private void SkipLine()
    {
        int c;
        while ((c = input.Read()) >= 0 && c != '\n')
        {
        }
    }

    private void SkipBlockComment()
    {
        int previous = 0;
        int c;
        while ((c = input.Read()) >= 0)
        {
            if (previous == '*' && c == '/')
            {
                return;
            }

            previous = c;
        }
    }

    /// <summary>Parse <c>TABLE [IF NOT EXISTS] `name` ( items… ) options</c>; column items start with an identifier.</summary>
    internal static bool TryParseCreateTable(string statement, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DumpTable? table)
    {
        table = null;
        string s = statement.TrimStart();
        if (!s.StartsWith("TABLE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int open = s.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        string head = s[5..open].Trim();
        if (head.StartsWith("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            head = head[13..].Trim();
        }

        string name = head.Trim('`');

        var columns = new List<string>();
        foreach (string item in SplitTopLevel(s, open + 1))
        {
            string trimmed = item.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed[0] == '`')
            {
                int end = trimmed.IndexOf('`', 1);
                columns.Add(trimmed[1..end]);
                continue;
            }

            string first = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
            if (first is not ("PRIMARY" or "KEY" or "UNIQUE" or "INDEX" or "CONSTRAINT" or "FULLTEXT" or "SPATIAL" or "FOREIGN" or "CHECK"))
            {
                columns.Add(trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0]);
            }
        }

        table = new DumpTable(name, columns);
        return true;
    }

    /// <summary>Items of the parenthesised list starting at <paramref name="start"/>, split on top-level commas.</summary>
    private static List<string> SplitTopLevel(string s, int start)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        char quote = '\0';
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    if (i + 1 < s.Length && s[i + 1] == quote)
                    {
                        current.Append(s[++i]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"' or '`':
                    quote = c;
                    current.Append(c);
                    break;
                case '(':
                    depth++;
                    current.Append(c);
                    break;
                case ')' when depth == 0:
                    items.Add(current.ToString());
                    return items;
                case ')':
                    depth--;
                    current.Append(c);
                    break;
                case ',' when depth == 0:
                    items.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        items.Add(current.ToString());
        return items;
    }
}
