using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>The <c>AllowDangerousDebug</c> gate (plan I8) wired into a host: both outcomes of <see cref="DangerousDebugGate.Allow"/>, every security level, and the value parsing.</summary>
public sealed class DangerousDebugGateHostTests
{
    private static IConfiguration Config(string? value)
        => new ConfigurationBuilder().AddInMemoryCollection(value is null ? [] : [new KeyValuePair<string, string?>(DangerousDebugGate.OptionPath, value)]).Build();

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("on", false)]
    [InlineData("", false)]
    [InlineData("tru", false)]
    public void TheRawValue_IsParsedAsABoolean_AndAnythingElseIsOff_AndTheLiveServerIsAlwaysOff(string value, bool allowed)
    {
        Assert.Equal(allowed, DangerousDebugGate.Create(Config(value), liveServer: false).Allowed);
        Assert.False(DangerousDebugGate.Create(Config(value), liveServer: true).Allowed);
    }

    [Fact]
    public void AnEmptyValue_IsOff()
    {
        DangerousDebugGate gate = DangerousDebugGate.Create(Config(string.Empty), liveServer: true);

        Assert.False(gate.Configured);
        Assert.False(gate.Allowed);
    }

    [Fact]
    public void Create_ReportsTheLiveFlag_AndDescribeSaysOffWhenTheKeyIsAbsent()
    {
        DangerousDebugGate gate = DangerousDebugGate.Create(Config(null), liveServer: true);

        Assert.True(gate.LiveServer);
        Assert.False(gate.Configured);
        Assert.Contains("off", gate.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalTexts_NameTheKeyAndRestart_AndTheLiveOneHasNoOverride()
    {
        Assert.Contains(DangerousDebugGate.OptionPath, DangerousDebugGate.RefusedText, StringComparison.Ordinal);
        Assert.Contains("restart", DangerousDebugGate.RefusedText, StringComparison.Ordinal);
        Assert.Equal("Refused: dangerous debug commands are disabled on the live server.", DangerousDebugGate.RefusedLiveText);
        Assert.DoesNotContain(DangerousDebugGate.OptionPath, DangerousDebugGate.RefusedLiveText, StringComparison.Ordinal);
        Assert.Equal("dangerous-debug-disabled", DangerousDebugGate.FailureReason);
    }

    [Fact]
    public void Allow_RejectsANullContext()
    {
        Assert.Throws<ArgumentNullException>(() => DangerousDebugGate.Allow(null!, "inject"));
    }

    [Fact]
    public async Task TheFeature_DefaultsToOff_WithoutConfiguration()
    {
        await using var host = WorldTestHost.Start();

        Assert.False(host.WorldServices.GetRequiredService<GmAuditFeature>().DangerousDebug.Allowed);
    }

    [Theory]
    [InlineData(AccountSecurity.GameMaster)]
    [InlineData(AccountSecurity.Administrator)]
    public async Task Allow_RefusesEverySecurityLevel_WhenOff_WithTheReplyOnThatInvoker(AccountSecurity security)
    {
        string account = "DDH" + security.ToString()[..3].ToUpperInvariant();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(Config("false")));
        await using WorldTestClient client = await host.EnterWorldAsync(account, "Ddh" + security.ToString()[..3], security);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync(account))!.Id)!;
        var replies = new List<string>();
        bool allowed = true;
        string? failure = null;

        await host.OnWorldAsync(() =>
        {
            CommandContext context = CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);
            allowed = DangerousDebugGate.Allow(context, "stall");
            failure = context.Failure;
        });

        Assert.False(allowed);
        Assert.Equal(DangerousDebugGate.FailureReason, failure);
        Assert.Contains(replies, l => l.Contains("restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Allow_AFailedGate_DoesNotLeaveAnythingInTheWriteLog()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DDHTWO", "Ddhtwo", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DDHTWO"))!.Id)!;
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();

        await host.OnWorldAsync(() => DangerousDebugGate.Allow(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "inject"));

        Assert.Equal(0, audit.DebugWrites.Total);
    }
}
