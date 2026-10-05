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
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

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
            try
            {
                result.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    RegexTimeout));
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException($"invalid {kind} regex at row {row}", error);
            }
            catch (NotSupportedException error)
            {
                throw new InvalidDataException($"unsupported {kind} regex at row {row}", error);
            }
        }
        return result;
    }
}
