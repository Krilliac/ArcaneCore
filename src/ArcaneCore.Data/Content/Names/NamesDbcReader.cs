using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.Names;

public static class NamesDbcReader
{
    public const int FieldCount = 2;
    public const int MaxPatternLength = 4096;
    public const long MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxRecords = 100_000;
    /// <summary>
    /// The per-match ceiling. The patterns use the non-backtracking engine, whose match time is linear in the input (a name
    /// of at most a few dozen characters), so the timeout is only a backstop. It must not be close to the cost of building
    /// the engine's automaton on a pattern's first match (measured up to 17 ms unloaded, far more under memory pressure or a
    /// GC pause): NameCatalog fails closed on a timeout, which would refuse a legal name. Patterns are also warmed at load.
    /// </summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A character-name-shaped input matched once per pattern at load, so the first player check pays no build cost.</summary>
    private const string WarmUpInput = "Warmupname";
    private const int WarmUpAttempts = 5;

    /// <summary>
    /// Rows per combined regex. Every NonBacktracking regex keeps its own symbolic-automaton caches (BDD dictionaries and state
    /// arrays), so one regex per row cost about 0.5 GB of live heap for the two real DBCs; a handful of combined regexes keeps
    /// that roughly constant while each chunk still builds and matches quickly.
    /// </summary>
    internal const int DefaultChunkRows = 256;

    private const RegexOptions PatternOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static NameCatalogSource Read(string path, string kind, out NamePatternSet patterns)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException($"{kind} exceeds the {MaxFileBytes}-byte file limit");
        if (stream.Length > int.MaxValue) throw new InvalidDataException($"{kind} is too large to load");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        DbcFile file = DbcFile.Parse(bytes);
        patterns = Read(file, kind, path);
        return new NameCatalogSource(kind, Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)), file.RecordCount);
    }

    /// <summary>
    /// Returns a set that matches a name exactly when any DBC row's pattern matches it: rows that are plain caseless literals
    /// go to one multi-string search, the rest into a few combined regexes (<see cref="DefaultChunkRows"/> rows each).
    /// </summary>
    public static NamePatternSet Read(DbcFile file, string kind, string source = "synthetic")
        => Build(ReadPatternStrings(file, kind), DefaultChunkRows, useLiterals: true);

    /// <summary>Reads and validates every row (each compiled once and discarded) and returns the unescaped pattern texts.</summary>
    internal static List<string> ReadPatternStrings(DbcFile file, string kind)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount || file.RecordSize != FieldCount * 4)
            throw new InvalidDataException($"{kind} requires the build-5875 two-field ds layout");
        if (file.RecordCount > MaxRecords)
            throw new InvalidDataException($"{kind} exceeds the {MaxRecords}-record limit");

        var result = new List<string>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            string pattern = file.GetStringStrict(row, 1);
            if (pattern.Length > MaxPatternLength)
                throw new InvalidDataException($"{kind} row {row} exceeds the pattern limit");
            pattern = pattern.Replace("\\^", "^").Replace("\\$", "$");
            try
            {
                _ = new Regex(pattern, PatternOptions, RegexTimeout);
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException($"invalid {kind} regex at row {row}", error);
            }
            catch (NotSupportedException error)
            {
                throw new InvalidDataException($"unsupported {kind} regex at row {row}", error);
            }
            result.Add(pattern);
        }
        return result;
    }

    /// <summary>Splits validated patterns into literal rows and regex rows, and builds the set.</summary>
    internal static NamePatternSet Build(IReadOnlyList<string> patterns, int chunkRows, bool useLiterals)
    {
        List<string> regexRows = patterns as List<string> ?? [.. patterns];
        HashSet<string>? literals = null;
        if (useLiterals)
        {
            regexRows = new List<string>(patterns.Count);
            literals = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pattern in patterns)
            {
                if (TryGetCaselessLiteral(pattern, out string? literal)) literals.Add(literal);
                else regexRows.Add(pattern);
            }
        }

        SearchValues<string>? search = literals is { Count: > 0 }
            ? SearchValues.Create([.. literals], StringComparison.Ordinal)
            : null;
        return new NamePatternSet([.. Combine(regexRows, chunkRows)], search, patterns.Count);
    }

    /// <summary>
    /// A row is a caseless literal when it is only digits, CJK/Hangul/kana characters and non-metacharacter ASCII punctuation
    /// (a backslash before such punctuation is that punctuation character). For these an ordinal substring search answers what
    /// the IgnoreCase | CultureInvariant regex would, because none of those characters has a case or a case-equivalent.
    /// ASCII letters are excluded on purpose (the regex folds e.g. K with the Kelvin sign, which an ordinal search would not).
    /// </summary>
    internal static bool TryGetCaselessLiteral(string pattern, [NotNullWhen(true)] out string? literal)
    {
        literal = null;
        if (pattern.Length == 0) return false;
        var builder = new System.Text.StringBuilder(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                if (++i >= pattern.Length || !IsLiteralPunctuation(pattern[i])) return false;
                c = pattern[i];
            }
            else if (!(char.IsAsciiDigit(c) || IsLiteralPunctuation(c) || IsCaselessScript(c)))
            {
                return false;
            }
            builder.Append(c);
        }
        literal = builder.ToString();
        return true;
    }

    /// <summary>ASCII punctuation that is a plain character in a .NET pattern (not a metacharacter, whitespace or '#').</summary>
    private static bool IsLiteralPunctuation(char c) => c is '<' or '>' or '-' or '~' or '@' or '!' or '%' or '&' or '\'' or ',' or '/' or ':' or ';' or '=' or '`' or '"' or '_';

    private static bool IsCaselessScript(char c)
        => c is (>= '\uAC00' and <= '\uD7A3')   // Hangul syllables
            or (>= '\u1100' and <= '\u11FF')    // Hangul Jamo
            or (>= '\u3131' and <= '\u318E')    // Hangul compatibility Jamo
            or (>= '\u4E00' and <= '\u9FFF')    // CJK unified ideographs
            or (>= '\u3041' and <= '\u30FF');   // Hiragana and Katakana

    /// <summary>
    /// Joins already-validated patterns into <c>(?:p1)|(?:p2)|...</c> regexes of at most <paramref name="chunkRows"/> rows.
    /// Each row sits in its own non-capturing group, so its anchors, alternation and inline options (all scoped to the enclosing
    /// group) keep their per-row meaning, and every construct a row opens (group, class, <c>(?#</c> comment) is closed by that
    /// row because each row parsed alone. The one thing that crosses a group boundary is a line comment: under an inline
    /// <c>(?x)</c>, <c>#</c> runs to the next line feed, which can belong to a later row and swallow the row's own closing
    /// parenthesis while the join still parses. Rows containing <c>#</c>, LF or CR are therefore compiled alone, never joined.
    /// A chunk whose combination still does not parse falls back to one regex per row.
    /// </summary>
    internal static List<Regex> Combine(IReadOnlyList<string> patterns, int chunkRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkRows, 1);
        var result = new List<Regex>((patterns.Count / chunkRows) + 1);
        var joinable = new List<string>(Math.Min(patterns.Count, chunkRows));
        foreach (string pattern in patterns)
        {
            if (pattern.AsSpan().IndexOfAny('#', '\n', '\r') >= 0)
            {
                result.Add(CompileWarm(pattern));
                continue;
            }

            joinable.Add(pattern);
            if (joinable.Count == chunkRows) FlushChunk(joinable, result);
        }

        if (joinable.Count > 0) FlushChunk(joinable, result);
        return result;
    }

    private static void FlushChunk(List<string> rows, List<Regex> result)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) text.Append('|');
            text.Append("(?:").Append(rows[i]).Append(')');
        }

        Regex? combined = null;
        try
        {
            combined = new Regex(text.ToString(), PatternOptions, RegexTimeout);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
        }

        if (combined is not null)
        {
            WarmUp(combined);
            result.Add(combined);
        }
        else
        {
            foreach (string row in rows) result.Add(CompileWarm(row));
        }

        rows.Clear();
    }

    private static Regex CompileWarm(string pattern)
    {
        var regex = new Regex(pattern, PatternOptions, RegexTimeout);
        WarmUp(regex);
        return regex;
    }

    /// <summary>
    /// Builds the pattern's automaton now, at load, instead of on the first player-facing check. A timeout here only means the
    /// machine was busy: the states built so far are kept, so the next attempt continues the work.
    /// </summary>
    private static void WarmUp(Regex pattern)
    {
        for (int attempt = 0; attempt < WarmUpAttempts; attempt++)
        {
            try
            {
                pattern.IsMatch(WarmUpInput);
                return;
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
    }
}
