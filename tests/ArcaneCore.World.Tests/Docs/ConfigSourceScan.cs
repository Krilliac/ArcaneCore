using System.Text.RegularExpressions;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>What the source scan checks bindings against.</summary>
internal sealed record ConfigCoverage(
    IReadOnlyDictionary<string, string> Holders,
    IReadOnlySet<string> CatalogPaths,
    IReadOnlySet<string> OptionTypeNames,
    IReadOnlyList<string> FrameworkSections,
    IReadOnlySet<string> FrameworkTypes);

/// <summary>
/// A ratchet over the C# source: every place that binds a configuration section or reads a key by name must be covered by the
/// catalog or the exception table, so a lane that adds a new binding without documenting it fails here with the file
/// and the key. It knows the binding styles in use (<c>GetSection(...)</c>, <c>Configure&lt;T&gt;</c>, string-literal
/// indexers, <c>ConfigKey</c> constants). It is a guard against the known styles, not a proof: a key built at run time
/// (interpolated, or held in a variable) is not seen.
/// </summary>
internal static partial class ConfigSourceScan
{
    public static IReadOnlyList<string> FindUncovered(IEnumerable<(string Path, string Text)> sources, ConfigCoverage coverage)
    {
        var problems = new List<string>();
        foreach ((string path, string text) in sources)
        {
            foreach (Match m in GetSectionRegex().Matches(text))
            {
                CheckSectionArgument(path, text, m.Groups[1].Value.Trim(), coverage, problems);
            }

            foreach (Match m in KeyIndexerRegex().Matches(text))
            {
                CheckKey(path, m.Groups[1].Value, coverage, problems);
            }

            foreach (Match m in ConfigKeyRegex().Matches(text))
            {
                CheckKey(path, m.Groups[1].Value, coverage, problems);
            }

            foreach (Match m in TypedBindingRegex().Matches(text))
            {
                string type = m.Groups[1].Value.Split('.')[^1];
                if (!coverage.OptionTypeNames.Contains(type) && !coverage.FrameworkTypes.Contains(type))
                {
                    problems.Add($"{path}: binds options type {type}, which is not in the configuration catalog (add its section, or an exception-table row)");
                }
            }
        }

        return problems;
    }

    private static void CheckSectionArgument(string file, string text, string argument, ConfigCoverage coverage, List<string> problems)
    {
        Match literal = LiteralRegex().Match(argument);
        if (literal.Success && literal.Index == 0 && literal.Length == argument.Length)
        {
            CheckKey(file, literal.Groups[1].Value, coverage, problems);
            return;
        }

        Match qualified = QualifiedRegex().Match(argument);
        if (qualified.Success)
        {
            string holder = qualified.Groups[1].Value.Split('.')[^1];
            string suffix = qualified.Groups[2].Success ? qualified.Groups[2].Value : string.Empty;
            if (!coverage.Holders.TryGetValue(holder, out string? section))
            {
                problems.Add($"{file}: GetSection({argument}): {holder} carries no SectionName constant in the catalogued assemblies");
                return;
            }

            CheckKey(file, section + suffix, coverage, problems);
            return;
        }

        if (BareRegex().IsMatch(argument))
        {
            // A feature that owns the constant itself: the file must declare a class the catalog knows.
            bool known = DeclaredClassRegex().Matches(text).Any(c => coverage.Holders.ContainsKey(c.Groups[1].Value));
            if (!known)
            {
                problems.Add($"{file}: GetSection({argument}): no class declared in this file carries a catalogued SectionName");
            }

            return;
        }

        // A lower-case identifier is a variable (a section chosen at run time); the scan cannot follow it.
        if (!VariableRegex().IsMatch(argument))
        {
            problems.Add($"{file}: GetSection({argument}) is a binding style the scan does not understand; add it to the scan or the exception table");
        }
    }

    private static void CheckKey(string file, string key, ConfigCoverage coverage, List<string> problems)
    {
        string head = key.Split(':')[0];
        if (coverage.FrameworkSections.Contains(head, StringComparer.Ordinal))
        {
            return;
        }

        if (coverage.CatalogPaths.Contains(key) || coverage.CatalogPaths.Any(p => p.StartsWith(key + ":", StringComparison.Ordinal)))
        {
            return;
        }

        problems.Add($"{file}: reads configuration key {key}, which is not in the configuration catalog (add the option class to the catalog, or an exception-table row)");
    }

    [GeneratedRegex(@"GetSection\(\s*((?:[^()]|\([^()]*\))*?)\s*\)")]
    private static partial Regex GetSectionRegex();

    [GeneratedRegex(@"(?:onfiguration|config)\??\[\s*""([^""$]+)""\s*\]")]
    private static partial Regex KeyIndexerRegex();

    [GeneratedRegex(@"\bConfigKey\s*=\s*""([^""]+)""")]
    private static partial Regex ConfigKeyRegex();

    [GeneratedRegex(@"\b(?:Configure|PostConfigure|AddOptions)<([\w.]+)>")]
    private static partial Regex TypedBindingRegex();

    [GeneratedRegex(@"^""([^""]+)""")]
    private static partial Regex LiteralRegex();

    [GeneratedRegex(@"^([A-Za-z_][\w.]*)\.(?:SectionName|Section)(?:\s*\+\s*""([^""]*)"")?$")]
    private static partial Regex QualifiedRegex();

    [GeneratedRegex(@"^SectionName$")]
    private static partial Regex BareRegex();

    [GeneratedRegex(@"^[a-z_]\w*$")]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"\bclass\s+(\w+)")]
    private static partial Regex DeclaredClassRegex();
}
