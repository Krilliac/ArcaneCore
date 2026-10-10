using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Conf;

public sealed class ConfCommandLineTests
{
    private static readonly ConfCatalog Catalog = ConfFixtureCatalog.Create();

    private static DaemonCommand Parse(ConfigDaemon daemon, params string[] args) => DaemonCommandLine.Parse(args, daemon, Catalog);

    private static DaemonCommand World(params string[] args) => Parse(ConfigDaemon.World, args);

    [Fact]
    public void NoArguments_Runs()
    {
        DaemonCommand command = World();
        Assert.Equal(DaemonCommandKind.Run, command.Kind);
        Assert.Empty(command.HostArgs);
        Assert.Null(command.ConfigPath);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("/h")]
    [InlineData("--HELP")]
    public void HelpTokens_PrintUsage_AndExit64(string token)
    {
        Assert.Equal(DaemonCommandKind.Help, World(token).Kind);
        Assert.Equal(DaemonCommandKind.Help, World("--Fixture:Port=1", token).Kind);
        Assert.Equal(DaemonCommandKind.Help, World("check-config", token).Kind);
    }

    [Fact]
    public void UnknownBareWord_IsUsage64_WithAVerbSuggestion()
    {
        DaemonCommand command = World("chek-config");
        Assert.Equal(DaemonCommandKind.UsageError, command.Kind);
        Assert.Contains("Did you mean 'check-config'?", command.Message, StringComparison.Ordinal);
        Assert.Equal(DaemonCommandKind.UsageError, World("start").Kind);
        Assert.Equal(DaemonCommandKind.UsageError, World("--Fixture:Port=1", "check-config").Kind);
    }

    [Theory]
    [InlineData("--Fixtur:Port=1")]
    [InlineData("/x")]
    [InlineData("-x")]
    [InlineData("--Fixture.Port=1")]
    [InlineData("--Auth:Port=1")]
    [InlineData("--Fixture:Sub=1")]
    [InlineData("--Fixture:Port")]
    public void UnknownOrSilentlyDroppedOption_IsUsage64_WithClosestKey(string option)
    {
        DaemonCommand command = World(option);
        Assert.Equal(DaemonCommandKind.UsageError, command.Kind);
        Assert.False(string.IsNullOrEmpty(command.Message));
        if (option == "--Fixtur:Port=1")
        {
            Assert.Contains("Did you mean Fixture:Port?", command.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--Fixture:Port=1")]
    [InlineData("--Fixture:Port", "1")]
    [InlineData("/Fixture:Port=1")]
    [InlineData("Fixture:Port=1")]
    [InlineData("--environment", "Production")]
    [InlineData("--contentRoot", "d")]
    [InlineData("--Logging:LogLevel:Default=Debug")]
    [InlineData("--Fixture:Map:zeta=3")]
    [InlineData("--Logging:LogLevel:Microsoft.EntityFrameworkCore=Warning")]
    public void RecognisedOptions_Run(string first, string? second = null)
    {
        string[] args = second is null ? [first] : [first, second];
        DaemonCommand command = World(args);
        Assert.Equal(DaemonCommandKind.Run, command.Kind);
        Assert.Equal(args, command.HostArgs);
    }

    [Fact]
    public void ConfigOption_IsRemovedFromHostArgs_AndNeedsAValue()
    {
        DaemonCommand spaced = World("--config", "my.conf", "--Fixture:Port=1");
        Assert.Equal("my.conf", spaced.ConfigPath);
        Assert.Equal(["--Fixture:Port=1"], spaced.HostArgs);

        DaemonCommand equals = World("--config=other.conf");
        Assert.Equal("other.conf", equals.ConfigPath);
        Assert.Empty(equals.HostArgs);

        Assert.Equal(DaemonCommandKind.UsageError, World("--config").Kind);
        Assert.Equal(DaemonCommandKind.UsageError, World("--config=").Kind);
    }

    [Fact]
    public void Verbs_AreRecognisedFirst_AndKeepTheirOptions()
    {
        DaemonCommand check = World("check-config", "--config", "x.conf", "--Fixture:Port=2");
        Assert.Equal(DaemonCommandKind.Verb, check.Kind);
        Assert.Equal("check-config", check.Verb);
        Assert.Equal("x.conf", check.ConfigPath);
        Assert.Equal(["--Fixture:Port=2"], check.HostArgs);

        DaemonCommand write = World("write-config", "--defaults", "out.conf", "--force");
        Assert.Equal(DaemonCommandKind.Verb, write.Kind);
        Assert.Equal(WriteConfigMode.Defaults, write.WriteMode);
        Assert.Equal("out.conf", write.OutputPath);
        Assert.True(write.Force);

        DaemonCommand current = World("write-config", "--current", "-");
        Assert.Equal(WriteConfigMode.Current, current.WriteMode);
        Assert.Equal("-", current.OutputPath);
        Assert.False(current.Force);
    }

    [Fact]
    public void WriteConfig_ArgumentErrors_Are64()
    {
        Assert.Equal(DaemonCommandKind.UsageError, World("write-config").Kind);
        Assert.Equal(DaemonCommandKind.UsageError, World("write-config", "--defaults").Kind);
        Assert.Equal(DaemonCommandKind.UsageError, World("write-config", "--defaults", "a", "--current", "b").Kind);
        Assert.Equal(DaemonCommandKind.UsageError, World("write-config", "extra", "--defaults", "a").Kind);
        Assert.Equal(DaemonCommandKind.UsageError, World("write-config", "--bogus", "a").Kind);
        Assert.Equal(DaemonCommandKind.UsageError, World("check-config", "--defaults", "a").Kind);
    }

    [Fact]
    public void Usage_NamesTheDaemonVerbsAndConfigOption()
    {
        string world = DaemonCommandLine.Usage(ConfigDaemon.World);
        Assert.Contains("ArcaneCore.World", world, StringComparison.Ordinal);
        Assert.Contains("check-config", world, StringComparison.Ordinal);
        Assert.Contains("write-config", world, StringComparison.Ordinal);
        Assert.Contains("--config", world, StringComparison.Ordinal);
        Assert.Contains("world.conf", world, StringComparison.Ordinal);
        Assert.Contains("ARCANECORE_CONFIG", world, StringComparison.Ordinal);

        string realm = DaemonCommandLine.Usage(ConfigDaemon.Realm);
        Assert.Contains("ArcaneCore.Realm", realm, StringComparison.Ordinal);
        Assert.Contains("realm.conf", realm, StringComparison.Ordinal);
    }

    [Fact]
    public void RealmDaemon_AcceptsItsOwnKeys_AndRefusesTheWorldsWithTheOwner()
    {
        Assert.Equal(DaemonCommandKind.Run, Parse(ConfigDaemon.Realm, "--Auth:Port=1").Kind);
        DaemonCommand command = Parse(ConfigDaemon.Realm, "--Fixture:Port=1");
        Assert.Equal(DaemonCommandKind.UsageError, command.Kind);
        Assert.Contains("world daemon", command.Message, StringComparison.Ordinal);
    }
}
