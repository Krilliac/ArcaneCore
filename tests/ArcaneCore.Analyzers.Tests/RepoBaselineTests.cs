namespace ArcaneCore.Analyzers.Tests;

/// <summary>The committed analyzers/safety-baseline.txt: well formed, and every entry names a file that still exists.</summary>
public sealed class RepoBaselineTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ArcaneCore.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ArcaneCore.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void EveryBaselineEntry_IsWellFormed_AndNamesAnExistingFile()
    {
        string root = RepoRoot();
        string[] lines = [.. File.ReadAllLines(Path.Combine(root, "analyzers", "safety-baseline.txt")).Where(l => l.Trim().Length > 0 && l.Trim()[0] != '#')];
        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            string[] parts = line.Split('|');
            Assert.True(parts.Length == 5 && int.TryParse(parts[4], out int count) && count >= 1, "malformed baseline line: " + line);
            Assert.Matches("^ARC000[1-6]$", parts[0]);
            // "assembly:<Name>" is the assembly-level finding (AllowUnsafeBlocks without an assembly audit); it has no file.
            Assert.True(parts[1].StartsWith("assembly:", StringComparison.Ordinal) ? parts[1].Length > "assembly:".Length : File.Exists(Path.Combine(root, parts[1])), "baseline names a missing file: " + parts[1]);
        }

        Assert.Equal(lines.Length, lines.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The baseline only shrinks: a ceiling per rule, in entries and in occurrences, that is lowered when entries go and never raised
    /// (docs/ops/memory-safety.md). A rule not listed has no entries. Raising a number here to fit a new finding defeats the file.
    /// </summary>
    [Fact]
    public void TheBaseline_NeverGrowsPastItsPerRuleCeiling()
    {
        var ceilings = new Dictionary<string, (int Entries, int Occurrences)>(StringComparer.Ordinal)
        {
            ["ARC0002"] = (14, 22),
            ["ARC0003"] = (2, 2),
            ["ARC0004"] = (2, 2),
            ["ARC0006"] = (64, 77),
        };

        string root = RepoRoot();
        string[] lines = [.. File.ReadAllLines(Path.Combine(root, "analyzers", "safety-baseline.txt")).Where(l => l.Trim().Length > 0 && l.Trim()[0] != '#')];
        Assert.NotEmpty(lines);
        foreach (IGrouping<string, string[]> rule in lines.Select(l => l.Split('|')).GroupBy(p => p[0], StringComparer.Ordinal))
        {
            Assert.True(ceilings.TryGetValue(rule.Key, out (int Entries, int Occurrences) ceiling), rule.Key + " has baseline entries but no ceiling: a rule's baseline is recorded when the rule lands, here, with its count");
            int entries = rule.Count();
            int occurrences = rule.Sum(p => int.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(entries <= ceiling.Entries, $"{rule.Key}: {entries} baseline entries, ceiling {ceiling.Entries}. Fix the code instead of adding a line.");
            Assert.True(occurrences <= ceiling.Occurrences, $"{rule.Key}: {occurrences} baselined occurrences, ceiling {ceiling.Occurrences}. Fix the code instead of raising a count.");
        }
    }

    /// <summary>
    /// Every suppression of a safety rule in analysed code says why. A <c>#pragma warning disable ARC000n</c> needs a <c>//</c> reason of
    /// at least 15 characters on the same line, and an <c>[AuditedUnsafe("...")]</c> needs a non-blank reason (the analyzer accepts any
    /// string). Analysed code is everything the analyzer runs on: src, tools (except vendored code) and tests/ArcaneCore.TestSupport.
    /// </summary>
    [Fact]
    public void EverySafetySuppression_CarriesAReason()
    {
        string root = RepoRoot();
        var pragma = new System.Text.RegularExpressions.Regex(@"^\s*#pragma\s+warning\s+disable\s+(?<ids>[^/\r\n]*\bARC000[1-6]\b[^/\r\n]*)(?<rest>.*)$");
        var audited = new System.Text.RegularExpressions.Regex(@"\[AuditedUnsafe\(\s*""(?<reason>(?:[^""\\]|\\.)*)""\s*\)\]");
        var offenders = new List<string>();
        int pragmas = 0, auditedUses = 0;
        foreach (string top in new[] { "src", "tools", Path.Combine("tests", "ArcaneCore.TestSupport") })
        {
            string dir = Path.Combine(root, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.StartsWith("src/ArcaneCore.Analyzers/", StringComparison.Ordinal)
                    || relative.StartsWith("tools/hotmodule-template/", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] text = File.ReadAllLines(file);
                for (int i = 0; i < text.Length; i++)
                {
                    System.Text.RegularExpressions.Match p = pragma.Match(text[i]);
                    if (p.Success)
                    {
                        pragmas++;
                        string rest = p.Groups["rest"].Value.Trim();
                        if (!rest.StartsWith("//", StringComparison.Ordinal) || rest.TrimStart('/').Trim().Length < 15)
                        {
                            offenders.Add($"{relative}:{i + 1}: pragma without a reason comment");
                        }
                    }

                    if (text[i].TrimStart().StartsWith("[AuditedUnsafe", StringComparison.Ordinal))
                    {
                        auditedUses++;
                        System.Text.RegularExpressions.Match a = audited.Match(text[i]);
                        if (!a.Success || a.Groups["reason"].Value.Trim().Length < 15)
                        {
                            offenders.Add($"{relative}:{i + 1}: [AuditedUnsafe] without a one-line string reason of 15 characters or more");
                        }
                    }
                }
            }
        }

        // The scan sees code at all: this tree has reasoned pragmas and audited members, so finding none means the roots or the patterns broke.
        Assert.True(pragmas >= 5, $"only {pragmas} ARC pragmas were seen");
        Assert.True(auditedUses >= 10, $"only {auditedUses} [AuditedUnsafe] uses were seen");
        Assert.Empty(offenders);
    }

    [Fact]
    public void NonExhaustiveSwitchExpressions_AreNamedAsBuildErrors_AndNothingSilencesThem()
    {
        string root = RepoRoot();
        string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Assert.Contains("<WarningsAsErrors>$(WarningsAsErrors);CS8509;CS8524</WarningsAsErrors>", props, StringComparison.Ordinal);

        // Any later way of turning the two diagnostics down: a NoWarn or WarningsNotAsErrors, an editorconfig severity, or a pragma / [SuppressMessage] in shipping code.
        var noWarn = new System.Text.RegularExpressions.Regex(@"<NoWarn>[^<]*\b(CS)?85(09|24)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var severity = new System.Text.RegularExpressions.Regex(@"dotnet_diagnostic\.(CS)?85(09|24)\.severity\s*=\s*(none|silent|suggestion|warning)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var notErrors = new System.Text.RegularExpressions.Regex(@"<WarningsNotAsErrors>[^<]*\b(CS)?85(09|24)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var suppress = new System.Text.RegularExpressions.Regex(@"SuppressMessage\s*\([^)]*""\b(CS)?85(09|24)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var pragma = new System.Text.RegularExpressions.Regex(@"#pragma\s+warning\s+(disable|restore)\s+[^\r\n]*\b(CS)?85(09|24)\b");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.StartsWith("tests/ThirdParty/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal))
            {
                continue;
            }

            string name = Path.GetFileName(file);
            bool project = name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".props", StringComparison.Ordinal) || name.EndsWith(".targets", StringComparison.Ordinal);
            bool config = name == ".editorconfig" || name.EndsWith(".globalconfig", StringComparison.Ordinal);
            bool source = name.EndsWith(".cs", StringComparison.Ordinal) && !relative.StartsWith("tests/", StringComparison.Ordinal);
            if (!project && !config && !source)
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if ((project && (noWarn.IsMatch(text) || notErrors.IsMatch(text))) || (config && severity.IsMatch(text)) || (source && (pragma.IsMatch(text) || suppress.IsMatch(text))))
            {
                offenders.Add(relative);
            }
        }

        Assert.Empty(offenders);
    }
}
