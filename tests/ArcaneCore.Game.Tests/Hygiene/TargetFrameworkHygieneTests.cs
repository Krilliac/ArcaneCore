using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.Game.Tests.Hygiene;

/// <summary>
/// Directory.Build.props states the target framework once. Some csprojs repeat it because Stryker.NET's analysis (tools/ops/quality/mutate.ps1)
/// reads it from the csproj itself; a repeated value that differs would build for one framework and mutate for another, so every
/// csproj-local value must equal the central one. A csproj that states none (inherits) is fine.
/// </summary>
public sealed partial class TargetFrameworkHygieneTests
{
    [GeneratedRegex(@"<TargetFrameworks?>\s*([^<]*?)\s*</TargetFrameworks?>")]
    private static partial Regex Tag();

    internal static string? Declared(string xml)
    {
        string live = Regex.Replace(xml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        Match m = Tag().Match(live);
        return m.Success ? m.Groups[1].Value : null;
    }

    [Fact]
    public void Declared_IgnoresCommentedOutTags()
    {
        Assert.Null(Declared("<Project><!-- <TargetFramework>net8.0</TargetFramework> --></Project>"));
        Assert.Equal("net11.0", Declared("<Project><!-- <TargetFramework>net8.0</TargetFramework> --><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>"));
    }

    [Fact]
    public void EveryCsprojLocalTargetFramework_EqualsTheCentralOne()
    {
        string root = RepositorySource.RequireRoot();
        string? central = Declared(File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        Assert.False(string.IsNullOrEmpty(central));

        char sep = Path.DirectorySeparatorChar;
        string[] projects = [.. Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                        && !f.Contains($"{sep}artifacts{sep}", StringComparison.Ordinal))];
        Assert.True(projects.Length > 10, $"only {projects.Length} csproj files were found");

        string[] wrong = [.. projects
            .Select(f => (File: Path.GetRelativePath(root, f).Replace('\\', '/'), Value: Declared(File.ReadAllText(f))))
            .Where(p => p.Value is not null && p.Value != central)
            .Select(p => $"{p.File}: {p.Value}")];
        Assert.True(wrong.Length == 0, $"csproj-local TargetFramework differs from Directory.Build.props ({central}):\n  " + string.Join("\n  ", wrong));
    }
}
