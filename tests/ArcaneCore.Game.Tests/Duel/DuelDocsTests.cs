using System.Reflection;
using ArcaneCore.Game.Combat;
using Xunit;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>The duel docs name every option the code declares and every file they claim to edit exists.</summary>
public sealed class DuelDocsTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ArcaneCore.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void TheAreaDoc_NamesEveryDuelOption()
    {
        string doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "areas", "duels.md"));

        Assert.Contains(DuelOptions.SectionName, doc);
        foreach (PropertyInfo option in typeof(DuelOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.Contains($"`{option.Name}`", doc);
        }
    }

    [Fact]
    public void TheIntegrationDoc_OnlyNamesSharedFilesThatExist()
    {
        string root = RepoRoot();
        string doc = File.ReadAllText(Path.Combine(root, "docs", "integration", "duels.md"));
        var files = System.Text.RegularExpressions.Regex.Matches(doc, @"^\| `((?:Game|World)/[^`]+|docs/[^`]+)` \|", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            foreach (string part in file.Split(',', StringSplitOptions.TrimEntries).Select(p => p.Trim('`', ' ')))
            {
                string path = part.StartsWith("docs/", StringComparison.Ordinal)
                    ? Path.Combine(root, part)
                    : Path.Combine(root, "src", part.StartsWith("Game/", StringComparison.Ordinal) ? "ArcaneCore.Game" : "ArcaneCore.World", part[(part.IndexOf('/') + 1)..]);
                Assert.True(File.Exists(path), $"{part} does not exist");
            }
        }
    }
}
