using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ArcaneCore.Analyzers;

/// <summary>
/// The allow-list of violations that existed when a rule was introduced (<c>analyzers/safety-baseline.txt</c>, passed to the compiler as an
/// AdditionalFile). One line per distinct finding: <c>RULE|repo-relative path|Type.Member|detail|count</c>; <c>#</c> starts a comment.
/// A finding the baseline covers is silent; one beyond its count is reported; a count above what the code has is reported (ARC9001), so the
/// file can only shrink. Immutable after <see cref="Load"/>; safe to read from concurrent analyzer callbacks.
/// </summary>
internal sealed class SafetyBaseline
{
    public const string FileName = "safety-baseline.txt";

    private readonly string? _root;

    private SafetyBaseline(string? root, Dictionary<string, int> allowed, List<(string Text, Location Where)> malformed)
    {
        _root = root;
        Allowed = allowed;
        Malformed = malformed;
    }

    /// <summary>Key (<c>RULE|path|member|detail</c>) to the number of occurrences allowed.</summary>
    public Dictionary<string, int> Allowed { get; }

    /// <summary>Lines that did not parse, with where they are.</summary>
    public List<(string Text, Location Where)> Malformed { get; }

    public static SafetyBaseline Load(AnalyzerOptions options, CancellationToken cancellationToken)
    {
        var allowed = new Dictionary<string, int>(StringComparer.Ordinal);
        var malformed = new List<(string Text, Location Where)>();
        string? root = null;

        foreach (AdditionalText file in options.AdditionalFiles)
        {
            string normalized = Normalize(file.Path);
            if (!normalized.EndsWith("/" + FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // analyzers/safety-baseline.txt sits one directory below the repository root.
            string directory = normalized.Substring(0, normalized.Length - FileName.Length - 1);
            int slash = directory.LastIndexOf('/');
            root = slash >= 0 ? directory.Substring(0, slash + 1) : string.Empty;

            SourceText? text = file.GetText(cancellationToken);
            if (text is null)
            {
                continue;
            }

            foreach (TextLine line in text.Lines)
            {
                string raw = line.ToString().Trim();
                if (raw.Length == 0 || raw[0] == '#')
                {
                    continue;
                }

                string[] parts = raw.Split('|');
                if (parts.Length != 5 || !int.TryParse(parts[4], out int count) || count < 1)
                {
                    malformed.Add((raw, Location.Create(file.Path, line.Span, text.Lines.GetLinePositionSpan(line.Span))));
                    continue;
                }

                string key = parts[0] + "|" + parts[1] + "|" + parts[2] + "|" + parts[3];
                allowed.TryGetValue(key, out int existing);
                allowed[key] = existing + count;
            }
        }

        return new SafetyBaseline(root, allowed, malformed);
    }

    /// <summary>The path as the baseline writes it: relative to the repository root with forward slashes (the full path when no baseline is configured).</summary>
    public string Relative(string fullPath)
    {
        string normalized = Normalize(fullPath);
        if (!string.IsNullOrEmpty(_root) && normalized.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Substring(_root!.Length);
        }

        return normalized;
    }

    public static string Normalize(string path) => path.Replace('\\', '/');
}
