using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Hygiene;

/// <summary>
/// The target framework is configured in Directory.Build.props, not in individual .csproj files.
/// Removing local declarations prevents developers who target different SDKs from continually
/// rewriting the project files and creating unrelated version-switch commits.
/// The standalone hotcode spike has its own Directory.Build.props to opt out of the repo build settings.
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
        Assert.Equal("net10.0", Declared("<Project><!-- <TargetFramework>net8.0</TargetFramework> --><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"));
    }

    [Fact]
    public void EveryCsprojInheritsItsTargetFrameworkFromBuildProps()
    {
        string root = RepositorySource.RequireRoot();
        // The default is conditional: the first TargetFramework tag selects
        // ArcaneTargetFramework, while the fallback supplies net10.0.
        // A regex that only matches an attribute-free tag incorrectly returns null.
        XDocument buildProps = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        XElement? defaultFramework = buildProps.Descendants("TargetFramework")
            .SingleOrDefault(element => (string?)element.Attribute("Condition") == "'$(TargetFramework)' == ''");
        Assert.NotNull(defaultFramework);
        Assert.Equal("net10.0", defaultFramework.Value);
        Assert.Equal("net10.0", Declared(File.ReadAllText(Path.Combine(root, "tools/hotcode-spike/Directory.Build.props"))));

        char sep = Path.DirectorySeparatorChar;
        string[] projects = [.. Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                        && !f.Contains($"{sep}artifacts{sep}", StringComparison.Ordinal))];
        Assert.True(projects.Length > 10, $"only {projects.Length} csproj files were found");

        // src/ArcaneCore.Analyzers is loaded inside the compiler, which only accepts netstandard2.0; it can never follow the central value.
        const string AnalyzerProject = "src/ArcaneCore.Analyzers/ArcaneCore.Analyzers.csproj";
        Assert.Equal("netstandard2.0", Declared(File.ReadAllText(Path.Combine(root, AnalyzerProject))));

        string[] wrong = [.. projects
            .Select(f => (File: Path.GetRelativePath(root, f).Replace('\\', '/'), Value: Declared(File.ReadAllText(f))))
            .Where(p => p.File != AnalyzerProject)
            .Where(p => p.Value is not null)
            .Select(p => $"{p.File}: {p.Value}")];
        Assert.True(wrong.Length == 0, "csproj-local TargetFramework must be inherited from Directory.Build.props:\n  " + string.Join("\n  ", wrong));
    }
}
