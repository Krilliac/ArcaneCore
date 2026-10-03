using System.Text.RegularExpressions;
using ArcaneCore.World.Crafting;
using Xunit;

namespace ArcaneCore.World.Tests.Crafting;

/// <summary>
/// Docs drift guard for the crafting lane: every <c>Crafting:*</c> / <c>Enchanting:*</c> configuration key named in the two crafting docs is a real option, and
/// every real option is named in the integration doc.
/// </summary>
public sealed partial class CraftingDocsTests
{
    [GeneratedRegex(@"`((?:Crafting|Enchanting):[A-Za-z]+)`")]
    private static partial Regex KeyPattern();

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ArcaneCore.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("ArcaneCore.slnx not found above the test binary");
    }

    private static IReadOnlySet<string> RealKeys()
    {
        var keys = new HashSet<string> { CraftingFeature.EnabledKey };
        foreach (System.Reflection.PropertyInfo property in typeof(EnchantingOptions).GetProperties())
        {
            keys.Add($"{EnchantingOptions.SectionName}:{property.Name}");
        }

        return keys;
    }

    [Theory]
    [InlineData("docs/integration/crafting.md")]
    [InlineData("docs/areas/crafting.md")]
    public void EveryKeyTheDocNames_IsARealOption(string relativePath)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        string[] named = [.. KeyPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct()];

        Assert.NotEmpty(named);
        IReadOnlySet<string> real = RealKeys();
        Assert.All(named, key => Assert.True(real.Contains(key), $"{relativePath} names {key}, which is not an option"));
    }

    [Fact]
    public void EveryRealOption_IsNamedInTheIntegrationDoc()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "docs/integration/crafting.md"));

        Assert.All(RealKeys(), key => Assert.Contains(key, text));
    }
}
