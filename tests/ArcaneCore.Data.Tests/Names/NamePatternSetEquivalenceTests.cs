using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Tests.Skills;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Data.Tests.Names;

/// <summary>Serialises the heap-measuring and 770 MB-oracle tests: <c>GC.GetTotalMemory</c> counts the whole process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NamePatternHeavyCollection
{
    public const string Name = "NamePatternHeavy";
}

/// <summary>
/// The combined <see cref="NamePatternSet"/> must answer exactly what the old loop over one regex per DBC row answered. The
/// oracle here is that old loop (each row compiled alone with the original options). The real-DBC facts are skipped, naming
/// ARCANECORE_TEST_DBC_DIR, when it is unset.
/// </summary>
[Collection(NamePatternHeavyCollection.Name)]
public sealed class NamePatternSetEquivalenceTests(ITestOutputHelper output)
{
    private const RegexOptions OldOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly string[] RealFiles = ["NamesProfanity.dbc", "NamesReserved.dbc"];

    [Fact]
    public void AnchorsAndInlineOptions_KeepTheirPerRowMeaning_InsideAnAlternation()
    {
        string[] rows =
        [
            "^abc$", "xyz$", "^foo", "bar", "(?-i)Baz", "a.c", "ab|cd", "^", "[0-9]{3}", "\\^lit\\$", "k", "\\<angle\\>", "\uD55C\uAE00", "18\uB144", "x\\-y",
            "(?x)q # comment",
        ];
        List<string> patterns = NamesDbcReader.ReadPatternStrings(DbcFile.Parse(Image(rows)), "NamesReserved.dbc");
        string[] names =
        [
            "abc", "xabc", "abcx", "ABC", "wxyz", "xyzw", "foo", "xfoo", "foox", "BAR", "xbarx", "baz", "Baz", "BAZ", "aXc", "ac", "cd", "ab", "", "x",
            "123", "12", "lit", "^lit$", "Kevin", "\u212Aevin", "<angle>", "angle", "\uD55C\uAE00", "x\uD55C\uAE00x", "18\uB144", "18", "x-y", "xy", "q",
            "Q#", "q # comment",
        ];

        IReadOnlyList<Regex> oracle = OldPerRow(patterns);
        foreach (int chunk in new[] { 1, 2, 3, 5, 256 })
        {
            foreach (bool literals in new[] { false, true })
            {
                NamePatternSet set = NamesDbcReader.Build(patterns, chunk, literals);
                foreach (string name in names)
                    Assert.True(OldAny(oracle, name) == set.IsMatch(name), $"chunk={chunk} literals={literals} name='{name}'");
            }
        }
    }

    [Fact]
    public void ARowThatIsValidAloneButNotInsideAGroup_FallsBackToItsOwnRegex()
    {
        // In (?x) mode "#" comments out everything to the next newline. Joined, "(?x)ab#" swallows its own ")" and, when a later
        // row starts with a newline, still parses as "(?:(?x)ab cd)", silently changing what the row matches. Rows with "#", LF or
        // CR therefore never join a chunk; every other row construct is closed by its own parentheses (see Combine).
        string[][] cases =
        [
            ["(?x)ab#", "\ncd"],
            ["(?x)ab#", "\rcd"],
            ["(?x)ab # c", "\n  cd"],
            ["abc", "(?x)q # c", "def"],
            ["a(?#note)b", "cd", "(?x) e f ", "g\\#h", "[#]i"],
            ["(?x)ab#", "xyz", "\ncd", "(?-x:w v)"],
        ];
        string[] names = ["ab", "AB", "cd", "abcd", "ab cd", "abc", "xxq", "DEF", "zzz", "ab\ncd", "ef", "e f", "g#h", "#i", "i", "wv", "w v", "xyz", "nb"];
        foreach (string[] rows in cases)
        {
            IReadOnlyList<Regex> oracle = OldPerRow(rows);
            foreach (int chunk in new[] { 256, 2 })
            {
                NamePatternSet set = NamesDbcReader.Build(rows, chunk, useLiterals: false);
                foreach (string name in names)
                    Assert.True(OldAny(oracle, name) == set.IsMatch(name), $"rows=[{string.Join(" ; ", rows)}] chunk={chunk} name='{name}'");
            }
        }

        // The excluded row is compiled alone; its neighbours still share one regex.
        NamePatternSet mixed = NamesDbcReader.Build(["abc", "(?x)q # c", "def"], 256, useLiterals: false);
        Assert.Equal(2, mixed.Regexes.Count);
        Assert.True(mixed.IsMatch("xxq"));
        Assert.True(mixed.IsMatch("DEF"));
        Assert.False(mixed.IsMatch("zzz"));
    }

    [Theory]
    [InlineData("\uD55C\uAE00", "\uD55C\uAE00")]
    [InlineData("18\uB144", "18\uB144")]
    [InlineData("\\<x\\>", null)]
    [InlineData("\\<\uB8E8\uC77C\\>", "<\uB8E8\uC77C>")]
    [InlineData("a", null)]
    [InlineData("K", null)]
    [InlineData("", null)]
    [InlineData("\uD55C.\uAE00", null)]
    [InlineData("\uD55C*", null)]
    [InlineData("^\uD55C", null)]
    [InlineData("\uD55C \uAE00", null)]
    [InlineData("\uD55C\\", null)]
    [InlineData("\\d", null)]
    public void CaselessLiteralDetection(string pattern, string? expected)
    {
        bool ok = NamesDbcReader.TryGetCaselessLiteral(pattern, out string? literal);
        Assert.Equal(expected is not null, ok);
        Assert.Equal(expected, literal);
    }

    [Fact]
    public void LiteralCharacters_NeverHaveACaseOrEquivalent()
    {
        // The literal fast path is only sound for characters the case-insensitive regex treats as themselves. Ask the regex
        // engine itself (not the casing tables): one interpreted IgnoreCase | CultureInvariant class over every accepted char
        // must match no char outside that set, so nothing folds onto an accepted char, and a future runtime change is caught.
        var acceptedChars = new List<char>();
        for (int c = 0; c <= 0xFFFF; c++)
            if (NamesDbcReader.TryGetCaselessLiteral(((char)c).ToString(), out _)) acceptedChars.Add((char)c);
        int accepted = acceptedChars.Count;

        var set = new StringBuilder("^[");
        for (int i = 0; i < acceptedChars.Count;)
        {
            int j = i;
            while (j + 1 < acceptedChars.Count && acceptedChars[j + 1] == acceptedChars[j] + 1) j++;
            set.Append("\\u").Append(((int)acceptedChars[i]).ToString("X4")).Append("-\\u").Append(((int)acceptedChars[j]).ToString("X4"));
            i = j + 1;
        }

        var folds = new Regex(set.Append("]$").ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var members = new HashSet<char>(acceptedChars);
        for (int c = 0; c <= 0xFFFF; c++)
        {
            char ch = (char)c;
            if (!members.Contains(ch)) Assert.False(folds.IsMatch(ch.ToString()), $"U+{c:X4} folds onto an accepted literal char");
        }

        Assert.True(accepted > 20_000);
    }

    [RealDbcFact]
    public void RealDbcs_CombinedSetEqualsPerRowAnyMatch()
    {
        string dir = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        int total = 0;
        foreach (string file in RealFiles)
        {
            DbcFile dbc = DbcFile.Parse(File.ReadAllBytes(Path.Combine(dir, file)));
            List<string> patterns = NamesDbcReader.ReadPatternStrings(dbc, file);
            NamePatternSet set = NamesDbcReader.Read(dbc, file);
            Assert.Equal(patterns.Count, set.Rows);
            // Guard against a silent per-row fallback, which would bring the memory back.
            Assert.True(set.Regexes.Count <= (patterns.Count / NamesDbcReader.DefaultChunkRows) + 1, $"{file}: {set.Regexes.Count} regexes");

            IReadOnlyList<Regex> oracle = OldPerRow(patterns);
            List<string> corpus = BuildCorpus(patterns);
            int hits = 0;
            foreach (string name in corpus)
            {
                bool expected = OldAny(oracle, name);
                bool actual = set.IsMatch(name);
                if (expected) hits++;
                Assert.True(expected == actual, $"{file}: '{name}' per-row={expected} combined={actual}");
            }

            // Every row must still be reachable by its own text (checks no row was dropped by the split).
            for (int row = 0; row < patterns.Count; row++)
                foreach (string probe in RowProbes(patterns[row]))
                    if (oracle[row].IsMatch(probe)) Assert.True(set.IsMatch(probe), $"{file} row {row} '{patterns[row]}' probe '{probe}' missed");

            output.WriteLine($"{file}: rows={patterns.Count} corpus={corpus.Count} hits={hits} regexes={set.Regexes.Count}");
            Assert.True(hits > 0, $"{file}: corpus produced no positive match, so the equivalence proves nothing");
            total += corpus.Count;
        }

        Assert.True(total > 10_000);
    }

    [RealDbcFact]
    public void RealDbcs_RetainedHeapIsFarBelowThePerRowCost()
    {
        string dir = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        DbcFile[] files = [.. RealFiles.Select(f => DbcFile.Parse(File.ReadAllBytes(Path.Combine(dir, f))))];
        long before = SettledHeap();
        var sw = Stopwatch.StartNew();
        NamePatternSet[] sets = [.. files.Select((f, i) => NamesDbcReader.Read(f, RealFiles[i]))];
        long loadMs = sw.ElapsedMilliseconds;
        long retained = SettledHeap() - before;
        GC.KeepAlive(sets);
        output.WriteLine($"retained={retained / 1048576.0:F1} MB load={loadMs} ms");
        // Measured about 35 MB combined; the old one-regex-per-row layout retained about 770 MB. The bound is loose for noise
        // from tests running in parallel.
        Assert.True(retained < 200L * 1024 * 1024, $"retained {retained / 1048576.0:F1} MB");
    }

    private static long SettledHeap()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }

    private static List<Regex> OldPerRow(IReadOnlyList<string> patterns)
        => [.. patterns.Select(p => new Regex(p, OldOptions, NamesDbcReader.RegexTimeout))];

    private static bool OldAny(IReadOnlyList<Regex> regexes, string name)
    {
        foreach (Regex regex in regexes)
            if (regex.IsMatch(name)) return true;
        return false;
    }

    /// <summary>Texts a row is likely to match: its pattern with regex syntax removed, cased and embedded variants.</summary>
    private static IEnumerable<string> RowProbes(string pattern)
    {
        string bare = new([.. pattern.Where(c => !"\\^$.*+?|()[]{}".Contains(c))]);
        yield return pattern;
        yield return bare;
        yield return bare.ToUpperInvariant();
        yield return "Xq" + bare + "Qx";
        yield return bare.Replace('k', '\u212A').Replace('K', '\u212A');
        yield return new string([.. bare.Select(c => c is >= '!' and <= '~' ? (char)(c + 0xFEE0) : c)]);
        yield return new string([.. bare.Select(c => c is >= '\u3041' and <= '\u3096' ? (char)(c + 0x60) : c is >= '\u30A1' and <= '\u30F6' ? (char)(c - 0x60) : c)]);
        if (bare.Length > 1) yield return bare[..^1];
    }

    private static List<string> BuildCorpus(IReadOnlyList<string> patterns)
    {
        var corpus = new HashSet<string>(StringComparer.Ordinal)
        {
            "Thrall", "Warmupname", "Admin", "Gm", "Arthas", "Xyzzy", "Abcdefghijkl", "Aaa", "Kkkk",
        };
        foreach (string pattern in patterns)
            foreach (string probe in RowProbes(pattern))
                corpus.Add(probe);

        // Generator output shaped like the playerbot name generator (letters only, 4 to 12, first letter capitalised).
        string[] onsets = ["b", "d", "f", "g", "h", "k", "l", "m", "n", "p", "r", "s", "t", "v", "z", "br", "dr", "gr", "kr", "tr", "th", "sh", "fl", "gl", "st", "sl", "ar", "el", "or"];
        string[] vowels = ["a", "e", "i", "o", "u", "ae", "ia", "ou", "ei"];
        string[] codas = ["", "", "", "n", "r", "l", "s", "th", "k", "m", "nd", "rn"];
        var random = new Random(26);
        for (int i = 0; i < 4000; i++)
        {
            var name = new StringBuilder();
            int syllables = random.Next(2, 4);
            for (int s = 0; s < syllables; s++)
                name.Append(onsets[random.Next(onsets.Length)]).Append(vowels[random.Next(vowels.Length)]).Append(codas[random.Next(codas.Length)]);
            if (name.Length > 12) name.Length = 12;
            name[0] = char.ToUpperInvariant(name[0]);
            corpus.Add(name.ToString());
        }

        // Optional extra corpus (for example every ClassicDB creature name), one name per line.
        string? extra = Environment.GetEnvironmentVariable("ARCANECORE_TEST_NAME_CORPUS");
        if (!string.IsNullOrWhiteSpace(extra) && File.Exists(extra))
            foreach (string line in File.ReadLines(extra))
                if (line.Length > 0) corpus.Add(line);
        return [.. corpus];
    }

    private static byte[] Image(string[] patterns)
    {
        var strings = new List<byte> { 0 };
        var offsets = new List<uint>();
        foreach (string pattern in patterns)
        {
            offsets.Add((uint)strings.Count);
            strings.AddRange(Encoding.UTF8.GetBytes(pattern));
            strings.Add(0);
        }

        byte[] image = new byte[20 + (patterns.Length * 8) + strings.Count];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)patterns.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)strings.Count);
        for (int i = 0; i < patterns.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (i * 8) + 4), offsets[i]);
        strings.ToArray().CopyTo(image, 20 + (patterns.Length * 8));
        return image;
    }
}
