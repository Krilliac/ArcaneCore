using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// War Mode removed <c>World:Playerbots:Social:WarMode:Enabled</c> (the one switch is <c>World:WarMode:Enabled</c>). A world.conf that still carries
/// the old key must start: the conf key check reports it as exactly one warning (never an error), and the options still bind.
/// </summary>
public sealed class WarModeObsoleteKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-warobs-" + Guid.NewGuid().ToString("N")[..8]);

    public WarModeObsoleteKeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A late watcher handle on Windows: the temp directory is not the subject of the test.
        }
    }

    [Fact]
    public void ConfWithTheRemovedSocialWarModeEnabledKey_StartsAndLogsExactlyOneWarning()
    {
        string path = Path.Combine(_dir, "world.conf");
        File.WriteAllText(path, "World.WarMode.Enabled = false\nWorld.Playerbots.Social.WarMode.Enabled = true\nWorld.Playerbots.Social.WarMode.HuntRadiusYards = 80\n");
        IConfigurationRoot root = new ConfigurationBuilder().Add(new ConfConfigurationSource { Path = path }).Build();

        ConfigIssue[] issues = [.. new ConfKeyCheck(ConfCatalog.Embedded, ConfigDaemon.World).Check(root)];

        ConfigIssue issue = Assert.Single(issues);
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
        Assert.Contains("World.Playerbots.Social.WarMode.Enabled", issue.ToString(), StringComparison.Ordinal);

        var hunt = new PlayerbotWarModeOptions();
        root.GetSection(PlayerbotOptions.SectionName + ":Social:WarMode").Bind(hunt);
        Assert.Equal(80, hunt.HuntRadiusYards);
        hunt.Validate();
    }
}
