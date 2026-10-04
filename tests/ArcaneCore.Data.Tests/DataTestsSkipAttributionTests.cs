using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The wave-4 integration report attributes every environment-gated skip of this project to the variable that lifts
/// it. The attribution is checked against the gating attributes themselves (their public <c>Variable</c> constant), so
/// a renamed or differently spelt variable, or a test class moved under another attribute, fails here instead of
/// sending a reader to set a variable that does not lift the skip. The report is found by walking up from the test
/// binary; not finding it is a failure, not a skip (a check that cannot read its input must not pass).
/// </summary>
public sealed class DataTestsSkipAttributionTests
{
    private const string ReportPath = "docs/integration/wave4-integration.md";

    private const string ParagraphStart = "Data.Tests skips are all environment-gated facts";

    /// <summary>A back-ticked environment variable of this project, as the report writes them.</summary>
    private static readonly Regex VariableToken = new(@"`(ARCANE[A-Z_]+)`", RegexOptions.CultureInvariant);

    [Fact]
    public void GatedTestClasses_AreAttributedToTheVariableTheirAttributeReads()
    {
        IReadOnlyDictionary<string, string> gated = GatedTestClasses();
        Assert.True(gated.Count >= 11, "expected at least the 11 dump- and DBC-gated classes, found: " + string.Join(", ", gated.Keys.Order(StringComparer.Ordinal)));

        string paragraph = SkipParagraph();
        var wrong = new List<string>();
        foreach ((string testClass, string variable) in gated.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            int at = paragraph.IndexOf($"`{testClass}`", StringComparison.Ordinal);
            Assert.True(at >= 0, $"{testClass} is gated by {variable} but the skip paragraph of {ReportPath} does not name it");

            // The report lists classes after the variable that gates them: the nearest variable before the class name.
            Match? nearest = VariableToken.Matches(paragraph[..at]).LastOrDefault();
            if (nearest is null)
            {
                throw new Xunit.Sdk.XunitException($"no variable precedes {testClass} in the skip paragraph");
            }

            if (nearest.Groups[1].Value != variable)
            {
                wrong.Add($"{testClass} is listed under {nearest.Groups[1].Value} but its attribute reads {variable}");
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void WorldStateDumpAttribute_ReadsItsOwnSpelling()
    {
        // The three WorldState classes share this attribute; it is the one whose variable is not ARCANECORE_CLASSICDB_DUMP.
        Assert.Equal("ARCANE_CLASSICDB_DUMP", WorldState.ClassicDbDumpFactAttribute.Variable);
        Assert.Equal("ARCANE_CLASSICDB_DUMP", GatedTestClasses()["WorldStateDataTests"]);
        Assert.Equal("ARCANE_CLASSICDB_DUMP", GatedTestClasses()["WeatherImportCliTests"]);
        Assert.Equal("ARCANE_CLASSICDB_DUMP", GatedTestClasses()["GameEventImporterTests"]);
        Assert.Equal("ARCANECORE_CLASSICDB_DUMP", GatedTestClasses()["CreatureSpawnEntryTests"]);
    }

    /// <summary>
    /// Every test class of this assembly with a method under a <see cref="FactAttribute"/> subclass that declares a public
    /// <c>Variable</c> constant, mapped to that variable. A class under two different variables is a failure.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> GatedTestClasses()
    {
        var gated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Type type in typeof(DataTestsSkipAttributionTests).Assembly.GetTypes().Where(t => t.IsClass))
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (FactAttribute fact in method.GetCustomAttributes<FactAttribute>(inherit: true))
                {
                    FieldInfo? field = fact.GetType().GetField("Variable", BindingFlags.Public | BindingFlags.Static);
                    if (field is null || !field.IsLiteral || field.GetRawConstantValue() is not string variable)
                    {
                        continue;
                    }

                    if (gated.TryGetValue(type.Name, out string? other) && other != variable)
                    {
                        throw new Xunit.Sdk.XunitException($"{type.Name} is gated by both {other} and {variable}; the report lists each class once");
                    }

                    gated[type.Name] = variable;
                }
            }
        }

        return gated;
    }

    private static string SkipParagraph()
    {
        string report = ReadReport();
        int start = report.IndexOf(ParagraphStart, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{ReportPath} has no paragraph starting \"{ParagraphStart}\"");
        string rest = report[start..].ReplaceLineEndings("\n");
        int end = rest.IndexOf("\n\n", StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    private static string ReadReport()
    {
        for (string? dir = AppContext.BaseDirectory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "docs", "integration", "wave4-integration.md");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new Xunit.Sdk.XunitException(ReportPath + " was not found above " + AppContext.BaseDirectory);
    }
}
