using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>A link found in a markdown file.</summary>
internal sealed record MarkdownLink(string File, int Line, string Target);

/// <summary>Reads the repository's markdown: link targets, heading anchors, backticked tokens (code fences and HTML comments are skipped).</summary>
internal static partial class MarkdownDocs
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git", ".vs", ".claude"];

    /// <summary>Repo-relative, '/'-separated paths of every markdown file under the root.</summary>
    public static IReadOnlyList<string> Files(string root)
    {
        var files = new List<string>();
        Collect(root, root, files);
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void Collect(string root, string dir, List<string> files)
    {
        foreach (string file in Directory.EnumerateFiles(dir, "*.md"))
        {
            files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            if (!SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                Collect(root, sub, files);
            }
        }
    }

    /// <summary>The lines of a markdown text outside fenced code blocks (fenced lines come back empty so line numbers hold).</summary>
    public static IReadOnlyList<string> ProseLines(string text)
    {
        var lines = new List<string>();
        bool fenced = false;
        bool comment = false;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw;
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                lines.Add(string.Empty);
                continue;
            }

            if (fenced)
            {
                lines.Add(string.Empty);
                continue;
            }

            if (comment)
            {
                comment = !line.Contains("-->", StringComparison.Ordinal);
                lines.Add(string.Empty);
                continue;
            }

            if (trimmed.StartsWith("<!--", StringComparison.Ordinal) && !line.Contains("-->", StringComparison.Ordinal))
            {
                comment = true;
                lines.Add(string.Empty);
                continue;
            }

            lines.Add(line);
        }

        return lines;
    }

    /// <summary>Inline links <c>[text](target)</c> outside code; targets with a title keep only the path part.</summary>
    public static IReadOnlyList<MarkdownLink> Links(string file, string text)
    {
        var links = new List<MarkdownLink>();
        IReadOnlyList<string> lines = ProseLines(text);
        for (int i = 0; i < lines.Count; i++)
        {
            string noCode = InlineCodeRegex().Replace(lines[i], string.Empty);
            foreach (Match m in LinkRegex().Matches(noCode))
            {
                string target = m.Groups[1].Value.Trim();
                int space = target.IndexOf(' ', StringComparison.Ordinal);
                links.Add(new MarkdownLink(file, i + 1, space > 0 ? target[..space] : target));
            }
        }

        return links;
    }

    /// <summary>The GitHub anchors of every heading, with the -1, -2 suffixes duplicates get.</summary>
    public static IReadOnlySet<string> Anchors(string text)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in ProseLines(text))
        {
            Match m = HeadingRegex().Match(line);
            if (!m.Success)
            {
                continue;
            }

            string slug = Slug(m.Groups[1].Value);
            int seen = counts.GetValueOrDefault(slug);
            counts[slug] = seen + 1;
            anchors.Add(seen == 0 ? slug : slug + "-" + seen);
        }

        return anchors;
    }

    public static string Slug(string heading)
    {
        string text = LinkTextRegex().Replace(heading, "$1").Replace("`", string.Empty, StringComparison.Ordinal).Trim().TrimEnd('#').Trim();
        var sb = new StringBuilder();
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append('-');
            }
        }

        return sb.ToString();
    }

    /// <summary>Backticked tokens outside fenced code, with their line numbers.</summary>
    public static IEnumerable<(int Line, string Token)> CodeSpans(string text)
    {
        IReadOnlyList<string> lines = ProseLines(text);
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (Match m in InlineCodeRegex().Matches(lines[i]))
            {
                yield return (i + 1, m.Groups[1].Value);
            }
        }
    }

    [GeneratedRegex(@"`+([^`]*)`+")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"(?<!!)\[[^\]]*\]\(([^)\s][^)]*)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^#{1,6}\s+(.*?)\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkTextRegex();
}
