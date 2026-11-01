using System.Text.RegularExpressions;
using ArcaneCore.World.Tests.Docs;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Ties the M15 phase closure criteria to the tests that cover them. The closure-evidence table in CLUSTERING_DESIGN.md maps every
/// "Required closure" clause to test names (Class.Method) and either marks it Verified or names the gap that remains; the clustering
/// phase table in ROADMAP.md must say the same. A cited test that is renamed or deleted, a phase with no evidence rows, or a roadmap row
/// that claims more (or less) than the table fails here instead of drifting silently.
/// </summary>
public sealed class PhaseClosureTests
{
    private const string DesignPath = "docs/CLUSTERING_DESIGN.md";
    private const string RoadmapPath = "docs/ROADMAP.md";
    private const string VerifiedStatus = "Verified";
    private const string GapPrefix = "Gap: ";

    private static readonly Regex Cite = new(@"`(\w+)\.(\w+)`", RegexOptions.CultureInvariant);
    private static readonly Regex PhaseKey = new(@"^M15\.\d$", RegexOptions.CultureInvariant);
    private static readonly Regex ClosureTablePhase = new(@"^\| (M15\.\d) —", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private sealed record EvidenceRow(string Phase, string Clause, string Tests, string Status);

    private static string Normalised(string relativePath) =>
        RepoRoot.ReadText(relativePath).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The text of one markdown section: from the line that starts with <paramref name="heading"/> up to the next line that starts with <paramref name="stop"/>.</summary>
    private static string Section(string text, string heading, string stop)
    {
        int start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{heading}' is missing");
        int end = text.IndexOf(stop, start + heading.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{stop}' does not follow '{heading}'");
        return text[start..end];
    }

    private static List<EvidenceRow> EvidenceRows()
    {
        string section = Section(Normalised(DesignPath), "### Closure evidence", "An integrator owns the shared contract");
        var rows = new List<EvidenceRow>();
        foreach (string line in section.Split('\n'))
        {
            if (!line.StartsWith("| M15.", StringComparison.Ordinal))
            {
                continue;
            }

            string[] cells = line.Trim().Trim('|').Split(" | ", StringSplitOptions.None);
            Assert.True(cells.Length == 4, $"closure-evidence row must have 4 cells: {line}");
            rows.Add(new EvidenceRow(cells[0].Trim(), cells[1].Trim(), cells[2].Trim(), cells[3].Trim()));
        }

        return rows;
    }

    private static List<string> TestSources()
    {
        string testsRoot = Path.Combine(RepoRoot.Find(), "tests");
        char sep = Path.DirectorySeparatorChar;
        var sources = new List<string>();
        foreach (string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}", StringComparison.Ordinal) || file.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            {
                continue;
            }

            sources.Add(File.ReadAllText(file));
        }

        return sources;
    }

    private static bool Declares(IReadOnlyList<string> sources, string typeName, string methodName)
    {
        var typePattern = new Regex(@"\bclass " + Regex.Escape(typeName) + @"\b", RegexOptions.CultureInvariant);
        var methodPattern = new Regex(@"\b" + Regex.Escape(methodName) + @"\s*[(<]", RegexOptions.CultureInvariant);
        foreach (string source in sources)
        {
            if (source.Contains(typeName, StringComparison.Ordinal)
                && source.Contains(methodName, StringComparison.Ordinal)
                && typePattern.IsMatch(source)
                && methodPattern.IsMatch(source))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasGap(EvidenceRow row) => row.Status.StartsWith(GapPrefix, StringComparison.Ordinal);

    [Fact]
    public void EveryClosureClause_IsVerifiedOrNamesItsGap_AndCitesAtLeastOneTest()
    {
        List<EvidenceRow> rows = EvidenceRows();
        Assert.NotEmpty(rows);

        var problems = new List<string>();
        foreach (EvidenceRow row in rows)
        {
            if (!PhaseKey.IsMatch(row.Phase))
            {
                problems.Add($"{row.Clause}: '{row.Phase}' is not a phase key");
            }

            if (!Cite.IsMatch(row.Tests))
            {
                problems.Add($"{row.Phase} / {row.Clause}: cites no test");
            }

            bool verified = string.Equals(row.Status, VerifiedStatus, StringComparison.Ordinal);
            bool namedGap = HasGap(row) && row.Status.Length > GapPrefix.Length + 20;
            if (!verified && !namedGap)
            {
                problems.Add($"{row.Phase} / {row.Clause}: status must be '{VerifiedStatus}' or '{GapPrefix}<what is missing>'");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryCitedTest_ExistsInTheTestTree()
    {
        List<EvidenceRow> rows = EvidenceRows();
        List<string> sources = TestSources();

        var missing = new List<string>();
        foreach (EvidenceRow row in rows)
        {
            foreach (Match cite in Cite.Matches(row.Tests))
            {
                string typeName = cite.Groups[1].Value;
                string methodName = cite.Groups[2].Value;
                if (!Declares(sources, typeName, methodName))
                {
                    missing.Add($"{row.Phase} / {row.Clause}: {typeName}.{methodName}");
                }
            }
        }

        Assert.True(missing.Count == 0, "cited tests that no longer exist:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryPhaseWithARequiredClosureCell_HasEvidenceRows()
    {
        string design = Normalised(DesignPath);
        string closureTable = Section(design, "## Phased implementation and acceptance", "An integrator owns the shared contract");
        string[] phases = [.. ClosureTablePhase.Matches(closureTable).Select(match => match.Groups[1].Value)];
        Assert.Equal(7, phases.Length);

        HashSet<string> evidenced = [.. EvidenceRows().Select(row => row.Phase)];
        string[] withoutEvidence = [.. phases.Where(phase => !evidenced.Contains(phase))];
        Assert.True(withoutEvidence.Length == 0, "phases with no closure-evidence rows: " + string.Join(", ", withoutEvidence));
        Assert.True(evidenced.IsSubsetOf(phases), "evidence rows name a phase the closure table does not have");
    }

    [Fact]
    public void EveryRoadmapPhaseRow_AgreesWithTheClosureEvidence_AndNoneIsLeftUnverified()
    {
        List<EvidenceRow> rows = EvidenceRows();
        string roadmap = Normalised(RoadmapPath);
        string table = Section(roadmap, "## Clustering phase status", "## Open work");

        int checkedRows = 0;
        var problems = new List<string>();
        foreach (string line in table.Split('\n'))
        {
            if (!line.StartsWith("| M15.", StringComparison.Ordinal))
            {
                continue;
            }

            checkedRows++;
            string phase = line.Split('|')[1].Trim();
            if (line.Contains("unverified", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not independently verified", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{phase}: still says unverified");
            }

            bool anyGap = rows.Any(row => row.Phase == phase && HasGap(row));
            string expected = anyGap ? "Closure gap:" : "Closure verified";
            string forbidden = anyGap ? "Closure verified" : "Closure gap:";
            if (!line.Contains(expected, StringComparison.Ordinal))
            {
                problems.Add($"{phase}: the design doc's evidence table implies '{expected}'");
            }

            if (line.Contains(forbidden, StringComparison.Ordinal))
            {
                problems.Add($"{phase}: says '{forbidden}' but the evidence table disagrees");
            }
        }

        Assert.Equal(7, checkedRows);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
