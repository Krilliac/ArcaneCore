using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Reads the <c>///</c> summary of a property from C# source text. No XML documentation sits beside the assemblies
/// (it is generated only so the build checks crefs, and stays in obj: CopyDocumentationFileToOutputDirectory is off), so the page text is
/// taken from the source. Only members declared directly in the type's own body are matched, so a nested class
/// with a same-named property cannot be confused with its parent; partial classes are searched in every file.
/// </summary>
internal sealed partial class ConfigSourceSummaries
{
    private readonly IReadOnlyList<string[]> _files;

    /// <summary>
    /// Every non-comment line that declares a type, by the type's name, in file then line order: the lines the per-type declaration
    /// pattern (<c>class|record|struct</c>, the name, no opening parenthesis) matches, found in one pass over the source instead of
    /// one pass per looked-up property (the catalog asks for several hundred properties of a few thousand files).
    /// </summary>
    private readonly Dictionary<string, List<(int File, int Line)>> _declarations = new(StringComparer.Ordinal);

    public ConfigSourceSummaries(IEnumerable<string> sourceTexts)
    {
        _files = [.. sourceTexts.Select(t => t.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int file = 0; file < _files.Count; file++)
        {
            string[] lines = _files[file];
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!(line.Contains("class", StringComparison.Ordinal) || line.Contains("record", StringComparison.Ordinal) || line.Contains("struct", StringComparison.Ordinal))
                    || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                seen.Clear();
                foreach (Match m in DeclaredTypeRegex().Matches(line))
                {
                    string name = m.Groups[1].Value;
                    if (!seen.Add(name))
                    {
                        continue;
                    }

                    if (!_declarations.TryGetValue(name, out List<(int File, int Line)>? at))
                    {
                        _declarations[name] = at = [];
                    }

                    at.Add((file, i));
                }
            }
        }
    }

    public string? Find(Type type, string property)
    {
        if (!_declarations.TryGetValue(type.Name, out List<(int File, int Line)>? declared))
        {
            return null;
        }

        var member = new Regex(@"^\s*public\s+[^=(;{]*\b" + Regex.Escape(property) + @"\s*(\{|=>|=|;)", RegexOptions.CultureInvariant);
        foreach ((int file, int line) in declared)
        {
            string? found = SearchBody(_files[file], line, member);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Zero-width, one match per type declared on the line (a lookahead, so <c>record struct X</c> still yields X); group 1 is the name.</summary>
    [GeneratedRegex(@"(?=\b(?:class|record|struct)\s+(\w+)\b(?!\s*\())", RegexOptions.CultureInvariant)]
    private static partial Regex DeclaredTypeRegex();

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
