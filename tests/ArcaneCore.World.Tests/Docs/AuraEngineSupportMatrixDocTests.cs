using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// The aura support table of <c>docs/areas/aura-engine.md</c> (and the counts line above it) is rendered from
/// <see cref="AuraSupport.Entries"/> (<c>AuraSupportBaseline.cs</c>), which <c>AuraSupportWorldTests</c> keeps equal to the live registrations of a composed
/// world. The page is checked against that render and rewritten by <see cref="DocsGolden.RegenerateCommand"/>, so the doc cannot drift from the code again.
/// </summary>
public sealed class AuraEngineSupportMatrixDocTests
{
    private const string DocPath = "docs/areas/aura-engine.md";
    private const string TableHeader = "| Value | Aura type | Level | vmangos handler | Consumers | Owner of the gap |";

    private static readonly Regex CountsLine = new(@"Counts: Handler \d+, Referenced \d+, Unsupported \d+, NotAnAura \d+ \(\d+ types\)", RegexOptions.CultureInvariant);

    /// <summary>The counts sentence for the baseline, in the words of the doc.</summary>
    internal static string RenderCounts()
    {
        int Count(AuraSupportLevel level) => AuraSupport.Entries.Count(e => e.Level == level);
        return string.Create(CultureInfo.InvariantCulture,
            $"Counts: Handler {Count(AuraSupportLevel.Handler)}, Referenced {Count(AuraSupportLevel.Referenced)}, Unsupported {Count(AuraSupportLevel.Unsupported)}, "
            + $"NotAnAura {Count(AuraSupportLevel.NotAnAura)} ({AuraSupport.Entries.Count} types)");
    }

    /// <summary>The table, header to last row, one line per aura type in value order.</summary>
    internal static string RenderTable()
    {
        var table = new StringBuilder();
        table.Append(TableHeader).Append('\n');
        table.Append("|---|---|---|---|---|---|").Append('\n');
        foreach (AuraSupportEntry row in AuraSupport.Entries.OrderBy(e => (int)e.Type))
        {
            table.Append(CultureInfo.InvariantCulture,
                $"| {(int)row.Type} | {row.Type} | {row.Level} | `{row.VmangosHandler}` (SpellAuras.cpp:{row.VmangosLine}) | {row.Consumers} | {row.Owner} |").Append('\n');
        }

        return table.ToString();
    }

    /// <summary>The doc with the counts sentence and the table replaced by their render.</summary>
    internal static string Render(string doc)
    {
        string normalized = DocsGolden.Normalize(doc);
        if (!CountsLine.IsMatch(normalized))
        {
            throw new Xunit.Sdk.XunitException($"{DocPath} has no 'Counts: Handler N, Referenced N, Unsupported N, NotAnAura N (N types)' sentence to keep in sync");
        }

        string withCounts = CountsLine.Replace(normalized, RenderCounts(), 1);
        int start = withCounts.IndexOf(TableHeader, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new Xunit.Sdk.XunitException($"{DocPath} has no support table starting with '{TableHeader}'");
        }

        int end = start;
        while (end < withCounts.Length && withCounts[end] == '|')
        {
            int newline = withCounts.IndexOf('\n', end);
            end = newline < 0 ? withCounts.Length : newline + 1;
        }

        return string.Concat(withCounts.AsSpan(0, start), RenderTable(), withCounts.AsSpan(end));
    }

    [Fact]
    public void TheBaseline_HasTheCountsTheDocStates()
    {
        // 181 / 0 / 11 / 1 is the baseline the spells wiring review counted; a change of a row changes this line and the doc in one commit.
        Assert.Equal("Counts: Handler 181, Referenced 0, Unsupported 11, NotAnAura 1 (193 types)", RenderCounts());
    }

    [Fact]
    public void TheAuraEngineDoc_MatchesTheSupportBaseline()
        => DocsGolden.Verify(DocPath, Render(RepoRoot.ReadText(DocPath)));
}
