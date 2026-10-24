using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>The <c>AllowDangerousDebug</c> gate (plan I8): off by default, and on the live server only when the key is set explicitly.</summary>
public sealed class DangerousDebugGateTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void TheOptionPath_IsInTheGmCommandsSection_AndDefaultsOff()
    {
        Assert.Equal("World:GmCommands:AllowDangerousDebug", DangerousDebugGate.OptionPath);
        Assert.False(new GmOptions().AllowDangerousDebug);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, false)]
    public void Allowed_FollowsTheRule(bool configured, bool explicitlySet, bool live, bool allowed)
        => Assert.Equal(allowed, DangerousDebugGate.Create(configured, explicitlySet, live).Allowed);

    [Fact]
    public void NoConfiguration_IsOff()
    {
        Assert.False(DangerousDebugGate.Create((IConfiguration?)null, liveServer: false).Allowed);
        Assert.False(DangerousDebugGate.Create((IConfiguration?)null, liveServer: true).Allowed);
    }

    [Fact]
    public void AnAbsentKey_IsOff_InBothModes()
    {
        IConfiguration empty = Config();

        Assert.False(DangerousDebugGate.Create(empty, liveServer: false).Allowed);
        Assert.False(DangerousDebugGate.Create(empty, liveServer: true).Allowed);
    }

    [Fact]
    public void AnExplicitTrue_Allows_OnADevBox_AndOnTheLiveServer()
    {
        IConfiguration on = Config((DangerousDebugGate.OptionPath, "true"));
        DangerousDebugGate dev = DangerousDebugGate.Create(on, liveServer: false);
        DangerousDebugGate live = DangerousDebugGate.Create(on, liveServer: true);

        Assert.True(dev.Allowed);
        Assert.True(live.Allowed);
        Assert.True(live.ExplicitlySet);
        Assert.Contains("live server", live.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitFalse_IsOff()
    {
        DangerousDebugGate gate = DangerousDebugGate.Create(Config((DangerousDebugGate.OptionPath, "false")), liveServer: true);

        Assert.False(gate.Allowed);
        Assert.True(gate.ExplicitlySet);
        Assert.False(gate.Configured);
    }

    [Fact]
    public void ANonBooleanValue_FailsClosed()
    {
        Assert.False(DangerousDebugGate.Create(Config((DangerousDebugGate.OptionPath, "yes please")), liveServer: false).Allowed);
    }

    [Fact]
    public void Describe_SaysWhyItIsOff()
    {
        Assert.Equal("AllowDangerousDebug: off", DangerousDebugGate.Create(false, false, false).Describe());
        Assert.Contains("live server", DangerousDebugGate.Create(true, false, true).Describe(), StringComparison.Ordinal);
        Assert.Equal("AllowDangerousDebug: on", DangerousDebugGate.Create(true, true, false).Describe());
    }

    [Fact]
    public async Task TryAllow_RefusesAndMarksTheCommandFailed_WhenTheOptionIsOff()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DDGONE", "Ddgone", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DDGONE"))!.Id)!;
        var replies = new List<string>();
        bool allowed = true;
        string? failure = null;

        await host.OnWorldAsync(() =>
        {
            CommandContext context = CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);
            allowed = DangerousDebugGate.TryAllow(context);
            failure = context.Failure;
        });

        Assert.False(allowed);
        Assert.Equal(DangerousDebugGate.FailureReason, failure);
        Assert.Contains(replies, l => l.Contains(DangerousDebugGate.OptionPath, StringComparison.Ordinal));
    }

    [Fact]
    public void GmOptionsBind_ReadsTheKey()
    {
        Assert.True(GmOptions.Bind(Config((DangerousDebugGate.OptionPath, "true"))).AllowDangerousDebug);
        Assert.False(GmOptions.Bind(Config()).AllowDangerousDebug);
    }
}
