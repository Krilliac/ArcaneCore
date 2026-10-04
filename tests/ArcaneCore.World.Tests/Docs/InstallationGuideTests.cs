using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcaneCore.Data;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>docs/guide/installation.md names keys, paths, tool verbs, flags, ports and defaults; every one of them is checked against the code.</summary>
public sealed class InstallationGuideTests
{
    private const string Guide = "docs/guide/installation.md";

    private static string Text => RepoRoot.ReadText(Guide).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string[] CatalogPaths => [.. ConfigProduction.Catalog.Entries.Select(e => e.Path), .. ConfigExceptionTable.AdHocKeys.Select(a => a.Path)];

    [Fact]
    public void Checker_FlagsAnUnknownKey_AMissingPath_AndAnUnknownFlag()
    {
        const string text = "Set `World:Nope` and `World:Port`. See `src/Nothing/Here.cs` and `src/ArcaneCore.World`. Run `powershell -File scripts/missing.ps1`.\n"
            + "```\ndotnet run --project tools/ArcaneCore.AccountTool -- frobnicate\n```\n";
        Assert.Single(GuideFactChecker.UnknownKeys(text, ["World:Port"]));
        string[] missing = [.. GuideFactChecker.MissingPaths(text, RepoRoot.Find())];
        Assert.Equal(2, missing.Length);
        Assert.Contains(GuideFactChecker.ToolVerbs(text), v => v.Verb == "frobnicate");
        Assert.Contains("Reuse", GuideFactChecker.ScriptParameters(RepoRoot.ReadText("scripts/dev-runner.ps1")));
        Assert.DoesNotContain("Frobnicate", GuideFactChecker.ScriptParameters(RepoRoot.ReadText("scripts/dev-runner.ps1")));
    }

    [Fact]
    public void EveryConfigurationKeyNamed_IsReadByTheCode() =>
        Assert.True(GuideFactChecker.UnknownKeys(Text, CatalogPaths).Count == 0, string.Join("\n", GuideFactChecker.UnknownKeys(Text, CatalogPaths)));

    [Fact]
    public void EveryRepositoryPathNamed_Exists() =>
        Assert.True(GuideFactChecker.MissingPaths(Text, RepoRoot.Find()).Count == 0, string.Join("\n", GuideFactChecker.MissingPaths(Text, RepoRoot.Find())));

    [Fact]
    public void EveryToolVerbRun_IsInThatToolsUsageText()
    {
        string accountSource = RepoRoot.ReadText("tools/ArcaneCore.AccountTool/Program.cs");
        var usage = new Dictionary<string, string>
        {
            ["tools/ArcaneCore.AccountTool"] = accountSource,
            ["tools/ArcaneCore.ContentImporter"] = ContentImporterCli.Usage,
            ["tools/ArcaneCore.DbUpgrade"] = DbUpgradeCli.Usage,
        };
        (string Project, string Verb)[] verbs = [.. GuideFactChecker.ToolVerbs(Text)];
        Assert.True(verbs.Length >= 6, "the guide runs the account tool and the importer; found " + verbs.Length + " verbs");
        foreach ((string project, string verb) in verbs)
        {
            Assert.True(usage.TryGetValue(project, out string? text), project + " is not a tool this test knows");
            Assert.Contains(verb, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDevRunnerFlagNamed_IsInTheScriptsParamBlock()
    {
        IReadOnlySet<string> parameters = GuideFactChecker.ScriptParameters(RepoRoot.ReadText("scripts/dev-runner.ps1"));
        string[] flags = [.. GuideFactChecker.NamedFlags(Text).Where(f => !f.Equals("File", StringComparison.OrdinalIgnoreCase))];
        Assert.True(flags.Length >= 8, "the guide names the dev-runner flags; found " + flags.Length);
        foreach (string flag in flags)
        {
            Assert.Contains(flag, parameters);
        }
    }

    [Fact]
    public void NamedPortsAndDefaults_EqualTheOptionsDefaults()
    {
        Assert.Equal(3724, new AuthOptions().Port);
        Assert.Equal(8085, new WorldOptions().Port);
        Assert.False(new AuthOptions().AutocreateAccounts);
        Assert.Equal(SchemaPolicy.Always, new DatabaseUpgradeOptions().Policy);
        string text = Text;
        Assert.Contains("port 3724", text, StringComparison.Ordinal);
        Assert.Contains("port 8085", text, StringComparison.Ordinal);
        Assert.Contains("`Auth:AutocreateAccounts` is `false`", text, StringComparison.Ordinal);
        Assert.Contains("`Database:Upgrade:Policy` defaults to `Always`", text, StringComparison.Ordinal);
        Assert.Contains("exits with 78", text, StringComparison.Ordinal);
        Assert.Equal(78, ArcaneCore.Kernel.Ops.ExitCodes.InvalidConfiguration);
        Assert.Contains("exit code 2", text, StringComparison.Ordinal);
        Assert.Equal(2, ArcaneCore.Kernel.Ops.ExitCodes.Restart);
    }

    /// <summary>ContentImporterCli.ResolveTarget throws a usage error when a target verb has neither --database nor --provider; the env variable supplies only the connection string.</summary>
    [Fact]
    public void EveryImporterExample_NeedingATarget_NamesOne()
    {
        string[] targetVerbs = ["import", "import-dbc", "verify"];
        string[] lines = [.. Text.Split('\n').Where(l => l.Contains("tools/ArcaneCore.ContentImporter -- ", StringComparison.Ordinal))];
        Assert.Contains(lines, l => l.Contains(" -- import-dbc ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(" -- verify", StringComparison.Ordinal));
        foreach (string line in lines)
        {
            string verb = line[(line.IndexOf(" -- ", StringComparison.Ordinal) + 4)..].Split(' ')[0];
            if (!targetVerbs.Contains(verb) || line.Contains("--dry-run", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(line.Contains("--database ", StringComparison.Ordinal) || line.Contains("--provider ", StringComparison.Ordinal), "no target given: " + line);
        }

        Assert.Contains("still needs `--provider`", Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProviderValueNamed_IsADatabaseProvider()
    {
        HashSet<string> providers = [.. Enum.GetNames<DatabaseProvider>()];
        IEnumerable<string> named = Regex.Matches(Text, @"""Provider"":\s*""([A-Za-z]+)""").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(Text, @"^\| `([A-Za-z]+)` \| ", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
        string[] values = [.. named.Distinct()];
        Assert.True(values.Length >= 4);
        Assert.All(values, v => Assert.Contains(v, providers));
        Assert.Equal(providers.Order(), values.Where(providers.Contains).Order());
    }

    [Fact]
    public void TheDatabaseExample_BindsThroughDatabaseOptions()
    {
        Match fence = Regex.Match(Text, @"```json\n(.*?)\n```", RegexOptions.Singleline);
        Assert.True(fence.Success, "the guide must show the Database configuration");
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(fence.Groups[1].Value))).Build();
        DatabaseOptions options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!;
        Assert.NotNull(options.Auth);
        Assert.NotNull(options.Characters);
        Assert.NotNull(options.World);
        Assert.All(new[] { options.Auth!, options.Characters!, options.World! }, c =>
        {
            Assert.Equal(DatabaseProvider.MariaDb, c.Provider);
            Assert.StartsWith("Server=", c.ConnectionString, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheGuideContainsNoRealSecret_OnlyTheShippedDevelopmentPlaceholders()
    {
        foreach (Match m in Regex.Matches(Text, @"Password=([^;""\s]*)"))
        {
            Assert.Contains(m.Groups[1].Value, new[] { "arcane", "..." });
        }

        Assert.DoesNotContain("AKIA", Text, StringComparison.Ordinal);
        // The placeholder is said to be a placeholder, next to the sample.
        Assert.Contains("development placeholder", Text, StringComparison.Ordinal);
        using JsonDocument realm = JsonDocument.Parse(RepoRoot.ReadText("src/ArcaneCore.Realm/appsettings.json"));
        Assert.Contains("User=arcane;Password=arcane;", realm.RootElement.GetProperty("Database").GetProperty("ConnectionString").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheckConfigVerb_ExistsAndTheGuideIsLinkedFromTheIndex()
    {
        Assert.Contains("check-config", ArcaneCore.World.Ops.Cli.OpsCli.Verbs);
        Assert.Contains("guide/installation.md", RepoRoot.ReadText("docs/README.md"), StringComparison.Ordinal);
    }
}
