using System.Text.RegularExpressions;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Mechanical fact checks for hand-written guides: every configuration key, repository path and tool command a guide names must exist.
/// Each method returns the list of problems (empty when the guide is consistent) so the tests can show all of them at once.
/// </summary>
internal static partial class GuideFactChecker
{
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*(?::[A-Za-z][A-Za-z0-9]*)+$")]
    private static partial Regex KeyShape();

    [GeneratedRegex(@"^(?:src|tools|scripts|docs|tests|\.github)/[A-Za-z0-9_./-]*[A-Za-z0-9_]$")]
    private static partial Regex PathShape();

    [GeneratedRegex(@"dotnet run --project (tools/[A-Za-z.]+) -- ([a-z-]+)")]
    private static partial Regex ToolRunShape();

    /// <summary>Configuration keys named in code spans (outside fences) that are not in the catalog.</summary>
    public static IReadOnlyList<string> UnknownKeys(string guideText, IEnumerable<string> catalogPaths)
    {
        string[] paths = [.. catalogPaths];
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((int line, string token) in MarkdownDocs.CodeSpans(guideText))
        {
            string key = token.Trim().TrimEnd(':', '.', ',');
            if (KeyShape().IsMatch(key) && !DocKeyAuditTests.Resolves(key, paths))
            {
                problems.Add($"line {line}: configuration key {key} is not read by any options class");
            }
        }

        return [.. problems];
    }

    /// <summary>Repository paths named in code spans that do not exist under <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> MissingPaths(string guideText, string root)
    {
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((int line, string token) in MarkdownDocs.CodeSpans(guideText))
        {
            string path = token.Trim();
            if (!PathShape().IsMatch(path))
            {
                continue;
            }

            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                problems.Add($"line {line}: path {path} does not exist");
            }
        }

        // Paths inside fenced command lines too (dotnet run --project <path>, powershell -File <path>).
        foreach (Match m in Regex.Matches(guideText, @"(?:--project|-File)\s+((?:src|tools|scripts)/[A-Za-z0-9_./-]+)"))
        {
            string path = m.Groups[1].Value;
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                problems.Add($"path {path} (in a command) does not exist");
            }
        }

        return [.. problems];
    }

    /// <summary>
    /// The verbs the guide runs through <c>dotnet run --project tools/X -- verb</c>, grouped by project directory, so a test can check each against that
    /// tool's usage text.
    /// </summary>
    public static IReadOnlyList<(string Project, string Verb)> ToolVerbs(string guideText) =>
        [.. ToolRunShape().Matches(guideText).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Distinct()];

    /// <summary>The <c>-Flag</c> parameters of a PowerShell script's param block.</summary>
    public static IReadOnlySet<string> ScriptParameters(string scriptText)
    {
        Match block = Regex.Match(scriptText, @"param\s*\((.*?)\n\)", RegexOptions.Singleline);
        if (!block.Success)
        {
            throw new InvalidOperationException("the script has no param block");
        }

        return new HashSet<string>(Regex.Matches(block.Groups[1].Value, @"\$([A-Za-z]+)").Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The <c>-Flag</c> tokens a guide names (code spans and fences).</summary>
    public static IReadOnlySet<string> NamedFlags(string guideText) =>
        new HashSet<string>(Regex.Matches(guideText, @"(?<![\w-])-([A-Z][A-Za-z]+)\b").Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
}
