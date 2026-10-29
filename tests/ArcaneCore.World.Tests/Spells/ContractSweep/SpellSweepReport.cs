using System.Text;
using System.Text.Json;
using ArcaneCore.TestSupport;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>Writes the sweep's machine-readable report (JSON) and its markdown summary; never under source control.</summary>
internal static class SpellSweepReport
{
    /// <summary>Directory override for the report (default: <c>spell-contract-sweep</c> beside the test assembly, inside bin/).</summary>
    public const string OutVariable = "ARCANECORE_SPELL_SWEEP_OUT";

    public static string OutputDirectory => Environment.GetEnvironmentVariable(OutVariable) is { Length: > 0 } custom
        ? custom
        : Path.Combine(AppContext.BaseDirectory, "spell-contract-sweep");

    public static (string Json, string Markdown) Write(SweepResult result, string name, IReadOnlyCollection<string> newKeys, IReadOnlyCollection<string> fixedKeys)
    {
        string directory = OutputDirectory;
        Directory.CreateDirectory(directory);
        string json = Path.Combine(directory, name + ".json");
        string markdown = Path.Combine(directory, name + ".md");
        var document = new
        {
            result.Full,
            result.RowsTotal,
            Swept = result.Outcomes.Count,
            ElapsedSeconds = Math.Round(result.Elapsed.TotalSeconds, 1),
            result.UndrivenInterruptBits,
            NewAgainstBaseline = newKeys,
            FixedAgainstBaseline = fixedKeys,
            Violations = result.Violations.Select(v => new { v.Category, v.SpellId, v.Detail, Gating = SweepCategory.IsGating(v.Category) }),
            Spells = result.Outcomes,
        };
        File.WriteAllText(json, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false }));
        File.WriteAllText(markdown, Summary(result, newKeys, fixedKeys));
        return (json, markdown);
    }

    public static string Summary(SweepResult result, IReadOnlyCollection<string> newKeys, IReadOnlyCollection<string> fixedKeys)
    {
        var text = new StringBuilder();
        text.AppendLine("# Spell contract sweep");
        text.AppendLine();
        text.AppendLine($"Selection: {(result.Full ? "full" : "default (player-castable + seeded sample)")}. Spell.dbc rows: {result.RowsTotal}, swept: {result.Outcomes.Count}, not selected: {result.RowsTotal - result.Outcomes.Count}. Time: {result.Elapsed.TotalSeconds:F0} s.");
        text.AppendLine($"Against the baseline: {newKeys.Count} new, {fixedKeys.Count} fixed.");
        text.AppendLine();
        text.AppendLine("## Cast results");
        text.AppendLine();
        foreach (IGrouping<string, SweepSpellOutcome> group in result.Outcomes.GroupBy(o => o.CastResult).OrderByDescending(g => g.Count()).Take(15))
        {
            text.AppendLine($"- {group.Key}: {group.Count()}");
        }

        text.AppendLine();
        text.AppendLine("## Violations by category");
        text.AppendLine();
        text.AppendLine("| Category | Spells | Gating | Examples |");
        text.AppendLine("|---|---:|---|---|");
        foreach (IGrouping<string, SweepViolation> group in result.Violations.GroupBy(v => v.Category).OrderByDescending(g => g.Select(v => v.SpellId).Distinct().Count()))
        {
            string examples = string.Join(", ", group.Select(v => v.SpellId).Distinct().Take(5));
            text.AppendLine($"| {group.Key} | {group.Select(v => v.SpellId).Distinct().Count()} | {(SweepCategory.IsGating(group.Key) ? "yes" : "no")} | {examples} |");
        }

        text.AppendLine();
        text.AppendLine("## Top details");
        text.AppendLine();
        foreach (IGrouping<string, SweepViolation> group in result.Violations.GroupBy(v => v.Category + " " + StripNumbers(v.Detail))
            .OrderByDescending(g => g.Count()).Take(30))
        {
            text.AppendLine($"- {group.Key}: {group.Count()} (e.g. {string.Join(", ", group.Select(v => v.SpellId).Distinct().Take(4))})");
        }

        text.AppendLine();
        text.AppendLine("## By player class");
        text.AppendLine();
        Dictionary<uint, string> classOf = result.Outcomes.ToDictionary(o => o.SpellId, o => o.PlayerClass);
        text.AppendLine("| Class | Swept | Spells with a violation | Top category |");
        text.AppendLine("|---|---:|---:|---|");
        foreach (IGrouping<string, SweepSpellOutcome> group in result.Outcomes.GroupBy(o => o.PlayerClass).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            SweepViolation[] mine = [.. result.Violations.Where(v => classOf.GetValueOrDefault(v.SpellId) == group.Key)];
            string top = mine.GroupBy(v => v.Category).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "-";
            text.AppendLine($"| {group.Key} | {group.Count()} | {mine.Select(v => v.SpellId).Distinct().Count()} | {top} |");
        }

        text.AppendLine();
        text.AppendLine("## Checks that could not run");
        text.AppendLine();
        foreach (IGrouping<string, string> group in result.Outcomes.SelectMany(o => o.Checks.Select(c => c.Key + " " + c.Value))
            .Where(s => s.Contains("not-run", StringComparison.Ordinal)).GroupBy(s => s).OrderByDescending(g => g.Count()).Take(25))
        {
            text.AppendLine($"- {group.Key}: {group.Count()}");
        }

        text.AppendLine();
        text.AppendLine("AuraInterruptFlags bits no driver fires (spells with the bit): "
            + string.Join(", ", result.UndrivenInterruptBits.Select(kv => $"{kv.Key}={kv.Value}")));
        return text.ToString();
    }

    private static string StripNumbers(string detail)
    {
        var text = new StringBuilder(detail.Length);
        foreach (char c in detail)
        {
            text.Append(char.IsDigit(c) ? '#' : c);
        }

        return text.ToString();
    }
}

/// <summary>
/// The checked-in list of known non-gating violations (<c>spell-contract-baseline.txt</c> next to this file). The sweep fails on a
/// violation that is not listed (a regression) and on a listed one for a swept spell that no longer happens (fixed: remove the line,
/// the ratchet only tightens). Refresh: run the full sweep with <c>ARCANECORE_SPELL_SWEEP_WRITE_BASELINE=1</c> (see the file header).
/// </summary>
internal sealed class SpellSweepBaseline
{
    public const string WriteVariable = "ARCANECORE_SPELL_SWEEP_WRITE_BASELINE";

    public const string FileName = "spell-contract-baseline.txt";

    private SpellSweepBaseline(string path, IReadOnlyList<string> header, HashSet<string> keys)
    {
        Path = path;
        Header = header;
        Keys = keys;
    }

    public string Path { get; }

    public IReadOnlyList<string> Header { get; }

    public HashSet<string> Keys { get; }

    public static string SourcePath => RepositorySource.FindFile("tests", "ArcaneCore.World.Tests", "Spells", "ContractSweep", FileName)
        ?? throw new FileNotFoundException("the spell contract baseline is missing", FileName);

    public static SpellSweepBaseline Load(string path)
    {
        string[] lines = File.ReadAllLines(path);
        List<string> header = [.. lines.TakeWhile(l => l.StartsWith('#'))];
        HashSet<string> keys = [.. lines.Where(l => l.Length > 0 && !l.StartsWith('#'))];
        return new SpellSweepBaseline(path, header, keys);
    }

    /// <summary>
    /// The ratchet: <paramref name="found"/> keys not in the baseline are new; baseline keys of a swept spell (and a contract that ran)
    /// not found are fixed.
    /// </summary>
    public (string[] New, string[] Fixed) Compare(IEnumerable<SweepViolation> found, IReadOnlySet<uint> swept, string contracts = "abcdefghi")
    {
        HashSet<string> current = [.. found.Where(v => !SweepCategory.IsGating(v.Category)).Select(v => v.Key)];
        string[] added = [.. current.Where(k => !Keys.Contains(k)).Order(StringComparer.Ordinal)];
        string[] fixedKeys = [.. Keys.Where(k => !current.Contains(k) && swept.Contains(SpellOf(k)) && contracts.Contains(k[0]))
            .Order(StringComparer.Ordinal)];
        return (added, fixedKeys);
    }

    /// <summary>Rewrite the baseline with the header kept and the non-gating violations of a full run, sorted.</summary>
    public void Write(IEnumerable<SweepViolation> found)
    {
        IEnumerable<string> keys = found.Where(v => !SweepCategory.IsGating(v.Category)).Select(v => v.Key).Distinct().Order(StringComparer.Ordinal);
        // LF on every host, so a refresh on Windows and one on Linux produce the same diff.
        File.WriteAllText(Path, string.Join('\n', [.. Header, .. keys]) + "\n");
    }

    private static uint SpellOf(string key)
    {
        string[] parts = key.Split('\t');
        return parts.Length > 1 && uint.TryParse(parts[1], out uint id) ? id : 0;
    }
}
