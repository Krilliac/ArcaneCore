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

    public static NameCatalogSource Read(string path, string kind, out IReadOnlyList<Regex> patterns)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException($"{kind} exceeds the {MaxFileBytes}-byte file limit");
        if (stream.Length > int.MaxValue) throw new InvalidDataException($"{kind} is too large to load");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        patterns = Read(DbcFile.Parse(bytes), kind, path);
        return new NameCatalogSource(kind, Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)), patterns.Count);
    }

    public static IReadOnlyList<Regex> Read(DbcFile file, string kind, string source = "synthetic")
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount || file.RecordSize != FieldCount * 4)
            throw new InvalidDataException($"{kind} requires the build-5875 two-field ds layout");
        if (file.RecordCount > MaxRecords)
            throw new InvalidDataException($"{kind} exceeds the {MaxRecords}-record limit");

        var result = new List<Regex>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            string pattern = file.GetStringStrict(row, 1);
            if (pattern.Length > MaxPatternLength)
                throw new InvalidDataException($"{kind} row {row} exceeds the pattern limit");
            pattern = pattern.Replace("\\^", "^").Replace("\\$", "$");
            Regex compiled;
            try
            {
                compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    RegexTimeout);
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException($"invalid {kind} regex at row {row}", error);
            }
            catch (NotSupportedException error)
            {
                throw new InvalidDataException($"unsupported {kind} regex at row {row}", error);
            }

            WarmUp(compiled);
            result.Add(compiled);
        }
        return result;
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
