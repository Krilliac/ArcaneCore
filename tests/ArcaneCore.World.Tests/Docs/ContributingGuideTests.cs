using System.Text.RegularExpressions;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Handlers;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>docs/guide/contributing.md: the build commands equal CI's, the seams it lists exist, and the test and hygiene rules it states match the code that enforces them.</summary>
public sealed class ContributingGuideTests
{
    private const string Guide = "docs/guide/contributing.md";

    private static string Text => RepoRoot.ReadText(Guide).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The per-suite test step of the CI matrix: one solution filter of ArcaneCore.slnx per suite.</summary>
    private const string CiSuiteFilter = "tests/ci/${{ matrix.suite }}.slnf";

    /// <summary>
    /// The <c>run:</c> lines of the CI workflow (comments are not commands). The matrix's per-suite filter reads as the whole
    /// solution, which is what a contributor runs locally; <see cref="TheCiSuiteFilters_CoverEveryTestProjectExactlyOnce"/> holds the
    /// filters to that.
    /// </summary>
    private static string[] CiRunLines() => [.. RepoRoot.ReadText(".github/workflows/ci.yml").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
        .Select(l => Regex.Match(l, @"^\s+run:\s+(.+?)\s*$")).Where(m => m.Success)
        .Select(m => m.Groups[1].Value.Replace(CiSuiteFilter, "ArcaneCore.slnx", StringComparison.Ordinal))];

    [Fact]
    public void TheBuildCommands_AreExactlyTheRunLinesOfTheCiWorkflow()
    {
        string[] ci = CiRunLines();
        Assert.True(ci.Length == 4, "ci.yml should have the restore, build, test and mock-client steps; found " + ci.Length);

        Match fence = Regex.Match(Text, @"```\n(dotnet restore.*?)\n```", RegexOptions.Singleline);
        Assert.True(fence.Success, "the guide must show the CI commands in a code block");
        string[] guide = [.. fence.Groups[1].Value.Split('\n').Select(l => l.Trim())];
        Assert.Equal(ci, guide);
    }

    [Fact]
    public void TheCiSuiteFilters_CoverEveryTestProjectExactlyOnce()
    {
        string ci = RepoRoot.ReadText(".github/workflows/ci.yml").Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("run: dotnet test " + CiSuiteFilter + " ", ci, StringComparison.Ordinal);
        Match suites = Regex.Match(ci, @"^\s+suite:\s+\[(.+?)\]\s*$", RegexOptions.Multiline);
        Assert.True(suites.Success, "ci.yml should list the test suites as a matrix");
        string[] names = [.. suites.Groups[1].Value.Split(',').Select(n => n.Trim())];

        string[] filtered = [.. names.SelectMany(name =>
        {
            string filter = RepoRoot.ReadText($"tests/ci/{name}.slnf");
            Assert.Contains("\"path\": \"../../ArcaneCore.slnx\"", filter, StringComparison.Ordinal);
            return Regex.Matches(filter, @"""(tests/[^""]+\.csproj)""").Select(m => m.Groups[1].Value);
        })];
        string[] solution = [.. Regex.Matches(RepoRoot.ReadText("ArcaneCore.slnx"), @"Path=""(tests/[^""]+\.csproj)""").Select(m => m.Groups[1].Value)];

        Assert.NotEmpty(solution);
        Assert.Equal(solution.Order(StringComparer.Ordinal), filtered.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WarningsAsErrors_ComesFromDirectoryBuildProps_NotACommandLineFlag()
    {
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", RepoRoot.ReadText("Directory.Build.props"), StringComparison.Ordinal);
        string[] runLines = CiRunLines();
        Assert.DoesNotContain(runLines, l => l.Contains("warnaserror", StringComparison.OrdinalIgnoreCase) || l.Contains("TreatWarningsAsErrors", StringComparison.Ordinal));
        Assert.Contains("`Directory.Build.props` (`TreatWarningsAsErrors`), not a command-line flag", Text, StringComparison.Ordinal);
        Assert.Contains(runLines, l => l.Contains("-m:1", StringComparison.Ordinal));
    }

    [Fact]
    public void EverySeamInterfaceNamed_ExistsAsAType()
    {
        Type[] seams =
        [
            typeof(IOpcodeHandlerGroup), typeof(IWorldFeature), typeof(IChatMessageHandler), typeof(ICommandGroup), typeof(ICommandExtension),
            typeof(IDataModule), typeof(ICharacterHooks), typeof(ICharacterDataCleanup), typeof(ICharacterDeleteHook), typeof(IMapUpdater),
            typeof(IWorldTestServices),
        ];
        Assert.All(seams, t => Assert.True(t.IsInterface));
        foreach (Type seam in seams)
        {
            Assert.Contains("`" + seam.Name + "`", Text, StringComparison.Ordinal);
        }

        // Every backticked identifier shaped like an interface in the seam table must be one of them.
        string table = Text[Text.IndexOf("## Seams", StringComparison.Ordinal)..Text.IndexOf("## Schema modules", StringComparison.Ordinal)];
        string[] named = [.. Regex.Matches(table, @"`(I[A-Z][A-Za-z]+)`").Select(m => m.Groups[1].Value).Distinct()];
        Assert.Equal(seams.Select(s => s.Name).Order(), named.Order());
    }

    [Fact]
    public void TheVariablesAndHelpersNamed_ExistInTheCodeAsWritten()
    {
        string testDatabases = RepoRoot.ReadText("tests/ArcaneCore.Data.Tests/TestDatabases.cs");
        foreach (string token in new[] { "ARCANECORE_TEST_MARIADB", "ARCANECORE_TEST_POSTGRES", "AvailableProviders" })
        {
            Assert.Contains(token, testDatabases, StringComparison.Ordinal);
            Assert.Contains(token, Text, StringComparison.Ordinal);
        }

        Assert.Contains(DocsGolden.UpdateVariable, Text, StringComparison.Ordinal);
        Assert.Contains(DocsGolden.RegenerateCommand, Text, StringComparison.Ordinal);
        string ci = RepoRoot.ReadText(".github/workflows/ci.yml");
        Assert.Contains("ARCANECORE_TEST_MARIADB", ci, StringComparison.Ordinal);
        Assert.Contains("ARCANECORE_TEST_POSTGRES", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProviderAndFlakeRules_AreStatedInTheTermsOfTheStandingRules()
    {
        string text = Text;
        foreach (string phrase in new[]
        {
            "run **only on the hosted CI**",
            "MariaDB DDL is not transactional and implicitly commits",
            "PostgreSQL DDL is transactional",
            "Npgsql pooling returns the same physical connection",
            "re-entrant",
            "Identifier quoting and case folding differ",
            "wall-clock timing",
            "single packet arriving within a fixed window",
            "generous deadline",
            "repeatedly",
        })
        {
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSchemaRules_MatchTheComposer()
    {
        // The composer rejects a gap and a duplicate, which is what the guide says.
        var first = new FixtureModule(5);
        Assert.Throws<InvalidOperationException>(() => DataModules.Compose(DatabaseComponent.World, "world", ["t"], [], [first]));
        Assert.Throws<InvalidOperationException>(() => DataModules.Compose(DatabaseComponent.World, "world", ["t"], [new SchemaStep(2, [])], [new FixtureModule(2)]));
        Assert.Contains("contiguous sequence starting at 2", Text, StringComparison.Ordinal);
        Assert.Contains("`<Context>.Schema.CurrentVersion`", Text, StringComparison.Ordinal);
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= 2);
    }

    private sealed class FixtureModule(int version) : IDataModule
    {
        public DatabaseComponent Component => DatabaseComponent.World;

        public int SchemaVersion { get; } = version;

        public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

        public void ConfigureModel(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
        {
        }

        public void AddServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
        }
    }

    [Fact]
    public void TheHygieneExtensionList_EqualsTheScannersForbiddenExtensions()
    {
        string scanner = RepoRoot.ReadText("tests/ArcaneCore.Game.Tests/Hygiene/RepoHygieneScanner.cs");
        Match block = Regex.Match(scanner, @"ForbiddenExtensions = new\([^)]*\)\s*\{(.*?)\};", RegexOptions.Singleline);
        Assert.True(block.Success, "the scanner's ForbiddenExtensions set was not found");
        string[] forbidden = [.. Regex.Matches(block.Groups[1].Value, @"""(\.[a-z0-9]+)""").Select(m => m.Groups[1].Value)];
        Assert.True(forbidden.Length >= 10);

        string text = Text;
        string section = text[text.IndexOf("## Repository hygiene", StringComparison.Ordinal)..text.IndexOf("## Documentation", StringComparison.Ordinal)];
        string[] named = [.. Regex.Matches(section, @"`(\.[a-z0-9]+)`").Select(m => m.Groups[1].Value)];
        Assert.Equal(forbidden.Order(), named.Order());
        Assert.Contains("MaxSqlBytes = 64 * 1024", scanner, StringComparison.Ordinal);
        Assert.Contains("64 KiB", section, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuide_DoesNotNameAPrivateInstructionFile_AndKeysAndPathsExist()
    {
        string text = Text;
        Assert.DoesNotContain("CLAUDE.md", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".claude", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AGENTS.md", text, StringComparison.Ordinal);

        string[] catalog = [.. ConfigProduction.Catalog.Entries.Select(e => e.Path), .. ConfigExceptionTable.AdHocKeys.Select(a => a.Path)];
        Assert.True(GuideFactChecker.UnknownKeys(text, catalog).Count == 0, string.Join("\n", GuideFactChecker.UnknownKeys(text, catalog)));
        Assert.True(GuideFactChecker.MissingPaths(text, RepoRoot.Find()).Count == 0, string.Join("\n", GuideFactChecker.MissingPaths(text, RepoRoot.Find())));
        Assert.Contains("guide/contributing.md", RepoRoot.ReadText("docs/README.md"), StringComparison.Ordinal);
    }
}
