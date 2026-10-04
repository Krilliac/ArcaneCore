using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Reads the <c>///</c> summary of a property from C# source text. The assemblies carry no XML documentation
/// (GenerateDocumentationFile is off, and turning it on would trip CS1591 under warnings-as-errors), so the page text is
/// taken from the source. Only members declared directly in the type's own body are matched, so a nested class
/// with a same-named property cannot be confused with its parent; partial classes are searched in every file.
/// </summary>
internal sealed partial class ConfigSourceSummaries
{
    private readonly IReadOnlyList<string[]> _files;

    public ConfigSourceSummaries(IEnumerable<string> sourceTexts) => _files = [.. sourceTexts.Select(t => t.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))];

    public string? Find(Type type, string property)
    {
        var declaration = new Regex(@"\b(class|record|struct)\s+" + Regex.Escape(type.Name) + @"\b(?!\s*\()", RegexOptions.CultureInvariant);
        var member = new Regex(@"^\s*public\s+[^=(;{]*\b" + Regex.Escape(property) + @"\s*(\{|=>|=|;)", RegexOptions.CultureInvariant);
        foreach (string[] lines in _files)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) || !declaration.IsMatch(lines[i]))
                {
                    continue;
                }

                string? found = SearchBody(lines, i, member);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? SearchBody(string[] lines, int declarationLine, Regex member)
    {
        int depth = 0;
        bool entered = false;
        for (int j = declarationLine; j < lines.Length; j++)
        {
            string code = lines[j].TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : lines[j];
            if (entered && depth == 1 && member.IsMatch(code))
            {
                return ExtractSummary(lines, j);
            }

            foreach (char c in code)
            {
                if (c == '{')
                {
                    depth++;
                    entered = true;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }

            if (entered && depth <= 0)
            {
                return null;
            }
        }

        return null;
    }

    private static string? ExtractSummary(string[] lines, int memberLine)
    {
        var doc = new List<string>();
        for (int k = memberLine - 1; k >= 0; k--)
        {
            string t = lines[k].Trim();
            if (t.StartsWith("///", StringComparison.Ordinal))
            {
                doc.Insert(0, t[3..]);
            }
            else if (!t.StartsWith('['))
            {
                break;
            }
        }

        if (doc.Count == 0)
        {
            return null;
        }

        Match summary = SummaryRegex().Match(string.Join(' ', doc));
        return summary.Success ? Render(summary.Groups[1].Value) : null;
    }

    /// <summary>Renders the XML inside a summary as one line of markdown (cref, c and paramref become backticks).</summary>
    public static string Render(string xml)
    {
        string text = CrefRegex().Replace(xml, m => "`" + m.Groups[1].Value.Replace('{', '<').Replace('}', '>') + "`");
        text = LangwordRegex().Replace(text, m => "`" + m.Groups[1].Value + "`");
        text = ParamRefRegex().Replace(text, m => "`" + m.Groups[1].Value + "`");
        text = CodeRegex().Replace(text, m => "`" + m.Groups[1].Value + "`");
        text = TagRegex().Replace(text, " ");
        text = text.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        var escaped = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            escaped.Append(c switch { '|' => "\\|", '<' => "&lt;", '>' => "&gt;", _ => c.ToString() });
        }

        return escaped.ToString();
    }

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"<see\s+cref=""(?:[A-Z]:)?([^""]+)""\s*/>")]
    private static partial Regex CrefRegex();

    [GeneratedRegex(@"<see\s+langword=""([^""]+)""\s*/>")]
    private static partial Regex LangwordRegex();

    [GeneratedRegex(@"<paramref\s+name=""([^""]+)""\s*/>")]
    private static partial Regex ParamRefRegex();

    [GeneratedRegex(@"<(?:c|code)>(.*?)</(?:c|code)>", RegexOptions.Singleline)]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
