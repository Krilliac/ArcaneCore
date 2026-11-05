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
            Assert.True(File.Exists(Path.Combine(root, parts[1])), "baseline names a missing file: " + parts[1]);
        }

        Assert.Equal(lines.Length, lines.Distinct(StringComparer.Ordinal).Count());
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
