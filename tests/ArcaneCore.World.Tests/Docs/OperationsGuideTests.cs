using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.HotCode;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// docs/guide/operations.md: its release-caveat register is asserted against the options defaults (flipping a default in code fails until the
/// register is reviewed) and the commands, tool verbs and exit behaviour it names must exist. The retail citations are authored text and are
/// not asserted here (hosted CI has no reference clones).
/// </summary>
public sealed class OperationsGuideTests
{
    private const string Guide = "docs/guide/operations.md";

    private static string Text => RepoRoot.ReadText(Guide).Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class FixtureProbe(string environment) : IRuntimeProbe
    {
        public string EnvironmentName { get; } = environment;

        public bool MetadataUpdatesSupported => false;

        public string? GetEnvironmentVariable(string name) => null;
    }

    [Fact]
    public void EveryRegisterKey_ResolvesInTheCatalog_AndItsRecordedDefaultEqualsTheCodeDefault()
    {
        Dictionary<string, ConfigEntry> catalog = ConfigProduction.Catalog.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        Assert.True(DeviationRegister.All.Count >= 20);
        foreach (Deviation d in DeviationRegister.All)
        {
            Assert.True(catalog.TryGetValue(d.Key, out ConfigEntry? entry), d.Key + " is not an option key");
            string actual = entry.Default.Trim('"');
            Assert.True(actual == d.Default, $"{d.Key}: the register records '{d.Default}' but the code default is '{actual}'; review the guide and the register");
        }

        Assert.Equal(DeviationRegister.All.Count, DeviationRegister.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheNonRetailDefaults_AreTheOnesTheCodeDocumentsAsNotRetail()
    {
        // The register's NonRetailDefault rows must be exactly the keys whose retail value differs from the shipped default as recorded in the source comments.
        string[] nonRetail = [.. DeviationRegister.All.Where(d => d.Stance == Stance.NonRetailDefault).Select(d => d.Key)];
        Assert.Contains("Bans:ProtectHigherSecurity", nonRetail);
        Assert.Contains("World:GmCommands:LowerSecurity", nonRetail);
        Assert.Contains("Database:Upgrade:Policy", nonRetail);
        Assert.Contains("World:MaxQueuedWorldPackets", nonRetail);
        Assert.DoesNotContain("Auth:MaxConnections", nonRetail);
        Assert.True(new ArcaneCore.World.Gm.Core.GmOptions().LowerSecurity);
        Assert.True(new ArcaneCore.World.Bans.BanOptions().ProtectHigherSecurity);

        // The shared per-address connection cap (netguard lane) is on by default with no vmangos equivalent, while the
        // daemon caps stay 0: the register must say so and name 0 as the switch that restores retail.
        Assert.Contains("Net:Protection:MaxConnectionsPerIp", nonRetail);
        Assert.Equal(16, new NetProtectionOptions().MaxConnectionsPerIp);
        Deviation cap = DeviationRegister.All.Single(d => d.Key == "Net:Protection:MaxConnectionsPerIp");
        Assert.Equal("16", cap.Default);
        Assert.Contains("Set 0", cap.Advice, StringComparison.Ordinal);
        Assert.Contains("On by default", cap.Advice, StringComparison.Ordinal);
        Assert.DoesNotContain("World:MaxConnectionsPerIp", nonRetail);
        Assert.DoesNotContain("Auth:MaxConnectionsPerIp", nonRetail);
    }

    [Fact]
    public void TheGuidesRegisterBlock_EqualsTheRegister()
    {
        string guide = Text;
        string updated = DeviationRegister.Replace(guide);
        if (Environment.GetEnvironmentVariable(DocsGolden.UpdateVariable) == "1" && updated != guide)
        {
            File.WriteAllText(Path.Combine(RepoRoot.Find(), "docs", "guide", "operations.md"), updated, new UTF8Encoding(false));
            guide = updated;
        }

        Assert.True(updated == guide, "the register table in " + Guide + " is stale. Regenerate: " + DocsGolden.RegenerateCommand);
    }

    [Fact]
    public void EveryCommandNamed_ExistsInTheCommandTable()
    {
        HashSet<string> paths = [.. CommandReferenceRenderer.Nodes(reloadEnabled: true).Select(n => n.Path)];
        string[] named = [.. MarkdownDocs.CodeSpans(Text).Select(s => s.Token).Where(t => Regex.IsMatch(t, @"^\.[a-z]+( [a-z]+)?$"))];
        Assert.True(named.Length >= 10, "the guide names the stop, ban and monitoring commands; found " + named.Length);
        foreach (string command in named)
        {
            Assert.Contains(command[1..], paths);
        }

        // The stop and restart commands need the Administrator account level, as the guide says.
        CommandNode shutdown = CommandReferenceRenderer.Nodes(reloadEnabled: false).Single(n => n.Path == "server shutdown");
        Assert.Equal(ArcaneCore.Kernel.Accounts.AccountSecurity.Administrator, CommandReferenceRenderer.MinimumAccount(shutdown.RequiredLevel, new ArcaneCore.World.Gm.Core.GmOptions()));
        Assert.Contains("Administrator", Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUpgradeSequence_UsesCommandsAndOptionsOfTheTool()
    {
        string text = Text;
        foreach (string token in new[] { "status", "plan", "upgrade", "--confirm-backup", "check", "backup-info", "--backup-dir", "--script" })
        {
            Assert.Contains(token, DbUpgradeCli.Usage, StringComparison.Ordinal);
            Assert.Contains(token, text, StringComparison.Ordinal);
        }

        Assert.Contains("auth, characters, world", text, StringComparison.Ordinal);
        Assert.Contains("auth, characters, world", DbUpgradeCli.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHotCodeLaunchGate_RefusesOutsideDevelopmentAndStagingAsTheRegisterSays()
    {
        HotCodeVerdict production = HotCodeGuard.Evaluate(new HotCodeOptions { Enabled = true }, new FixtureProbe("Production"));
        Assert.False(production.Allowed);
        Assert.Contains("Development or Staging", string.Join(" ", production.Refusals), StringComparison.Ordinal);
        Assert.True(HotCodeGuard.Evaluate(new HotCodeOptions { Enabled = true }, new FixtureProbe("Staging")).Allowed);
        Assert.Contains("return 78; // EX_CONFIG", RepoRoot.ReadText("src/ArcaneCore.World/Program.cs"), StringComparison.Ordinal);
        Assert.Contains("Development or Staging", DeviationRegister.All.Single(d => d.Key == "World:HotCode:Enabled").Advice, StringComparison.Ordinal);
        Assert.Contains("78", DeviationRegister.All.Single(d => d.Key == "World:HotCode:Enabled").Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMissingThrottleAndConsoleSender_AreStatedAsLimits_NotClaimedDelivered()
    {
        string text = Text;
        Assert.Contains("wrong-password throttle", text, StringComparison.Ordinal);
        Assert.Contains("console command sender", text, StringComparison.Ordinal);
        Assert.Contains("limits and not options", text, StringComparison.Ordinal);
        Assert.Contains("realmd.conf.dist.in:208-209", text, StringComparison.Ordinal);
        Assert.Contains("Chat.h:66-70", text, StringComparison.Ordinal);

        // No option for either exists: if one is added, the guide must stop calling it a limit.
        string[] keys = [.. ConfigProduction.Catalog.Entries.Select(e => e.Path)];
        Assert.DoesNotContain(keys, k => k.Contains("WrongPass", StringComparison.OrdinalIgnoreCase) || k.Contains("Throttle", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(CommandReferenceRenderer.Nodes(reloadEnabled: false), n => n.Path.StartsWith("console", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheRetailVersusShippedDefaultSplit_IsRecordedWithItsCitations()
    {
        string text = Text;
        Assert.Contains("World.cpp:556", text, StringComparison.Ordinal);
        Assert.Contains("mangosd.conf.dist.in", text, StringComparison.Ordinal);
        Deviation say = DeviationRegister.All.Single(d => d.Key == "World:ListenRangeSay");
        Assert.Contains("25", say.Retail, StringComparison.Ordinal);
        Assert.Contains("40", say.Retail, StringComparison.Ordinal);
        Assert.Equal(25f, new ArcaneCore.Game.Maps.WorldRuntimeOptions().ListenRangeSay);
    }

    [Fact]
    public void KeysAndPathsNamed_Exist_AndTheGuideIsIndexed()
    {
        string[] catalog = [.. ConfigProduction.Catalog.Entries.Select(e => e.Path), .. ConfigExceptionTable.AdHocKeys.Select(a => a.Path)];
        Assert.True(GuideFactChecker.UnknownKeys(Text, catalog).Count == 0, string.Join("\n", GuideFactChecker.UnknownKeys(Text, catalog)));
        Assert.True(GuideFactChecker.MissingPaths(Text, RepoRoot.Find()).Count == 0, string.Join("\n", GuideFactChecker.MissingPaths(Text, RepoRoot.Find())));
        Assert.Contains("guide/operations.md", RepoRoot.ReadText("docs/README.md"), StringComparison.Ordinal);
    }
}
