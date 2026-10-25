using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>The <c>AllowDangerousDebug</c> gate (plan I8): off by default, on only when the option is true, and never on the live server.</summary>
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
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void Allowed_FollowsTheRule_AndTheLiveServerOverridesTheOption(bool configured, bool live, bool allowed)
        => Assert.Equal(allowed, DangerousDebugGate.Create(configured, live).Allowed);

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
    public void AnExplicitTrue_Allows_OnADevBox_ButNeverOnTheLiveServer()
    {
        IConfiguration on = Config((DangerousDebugGate.OptionPath, "true"));
        DangerousDebugGate dev = DangerousDebugGate.Create(on, liveServer: false);
        DangerousDebugGate live = DangerousDebugGate.Create(on, liveServer: true);

        Assert.True(dev.Allowed);
        Assert.False(live.Allowed);
        Assert.True(live.Configured);
        Assert.True(live.LiveServer);
        Assert.Contains("live server", live.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitFalse_IsOff()
    {
        DangerousDebugGate gate = DangerousDebugGate.Create(Config((DangerousDebugGate.OptionPath, "false")), liveServer: false);

        Assert.False(gate.Allowed);
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
        Assert.Equal("AllowDangerousDebug: off", DangerousDebugGate.Create(false, false).Describe());
        Assert.Contains("live server", DangerousDebugGate.Create(true, true).Describe(), StringComparison.Ordinal);
        Assert.Equal("AllowDangerousDebug: on", DangerousDebugGate.Create(true, false).Describe());
    }

    [Fact]
    public async Task Allow_RefusesAndMarksTheCommandFailed_WhenTheOptionIsOff()
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
            allowed = DangerousDebugGate.Allow(context, "time offset");
            failure = context.Failure;
        });

        Assert.False(allowed);
        Assert.Equal(DangerousDebugGate.FailureReason, failure);
        Assert.Contains(replies, l => l.Contains(DangerousDebugGate.OptionPath, StringComparison.Ordinal) && l.Contains("time offset", StringComparison.Ordinal));
    }

    [Fact]
    public void GmOptionsBind_ReadsTheKey()
    {
        Assert.True(GmOptions.Bind(Config((DangerousDebugGate.OptionPath, "true"))).AllowDangerousDebug);
        Assert.False(GmOptions.Bind(Config()).AllowDangerousDebug);
    }
}

/// <summary>Swaps the static live-server probe, so these tests run alone (no other test may see the swapped value).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DangerousDebugProbeCollection
{
    public const string Name = "Dangerous debug live probe";
}

[Collection(DangerousDebugProbeCollection.Name)]
public sealed class DangerousDebugGateProbeTests
{
    [Fact]
    public async Task TheFeature_ReadsTheConfiguredValue_AndAllowPasses_ForConsoleAndPlayer()
    {
        // The probe is pinned to "not live" for the whole test, so the outcome never depends on the test process's own environment.
        Func<bool> original = DangerousDebugGate.LiveServerProbe;
        DangerousDebugGate.LiveServerProbe = () => false;
        try
        {
            await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>(DangerousDebugGate.OptionPath, "true")]).Build()));
            await using WorldTestClient client = await host.EnterWorldAsync("DDHONE", "Ddhone", AccountSecurity.Administrator);
            var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DDHONE"))!.Id)!;
            var replies = new List<string>();
            List<(bool Allowed, string? Failure)> results = [];

            await host.OnWorldAsync(() =>
            {
                CommandContext console = CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);
                results.Add((DangerousDebugGate.Allow(console, "inject"), console.Failure));
                CommandContext player = new(session, session.Player!, ChatCommands.CreateTable());
                results.Add((DangerousDebugGate.Allow(player, "inject"), player.Failure));
            });

            Assert.True(host.WorldServices.GetRequiredService<GmAuditFeature>().DangerousDebug.Allowed);
            Assert.Equal([(true, (string?)null), (true, (string?)null)], results);
            Assert.Empty(replies);
        }
        finally
        {
            DangerousDebugGate.LiveServerProbe = original;
        }
    }

    [Fact]
    public async Task Allow_RefusesOnTheLiveServer_EvenWhenTheOptionIsOn()
    {
        Func<bool> original = DangerousDebugGate.LiveServerProbe;
        var replies = new List<string>();
        List<bool> results = [];
        string? failure = null;

        try
        {
            DangerousDebugGate.LiveServerProbe = () => false;
            await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>(DangerousDebugGate.OptionPath, "true")]).Build()));
            await using WorldTestClient client = await host.EnterWorldAsync("DDGPRB", "Ddgprb", AccountSecurity.Administrator);
            var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DDGPRB"))!.Id)!;

            await host.OnWorldAsync(() =>
            {
                CommandContext context = CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);
                DangerousDebugGate.LiveServerProbe = () => false;
                results.Add(DangerousDebugGate.Allow(context, "inject"));
                DangerousDebugGate.LiveServerProbe = () => true;
                results.Add(DangerousDebugGate.Allow(context, "inject"));
                failure = context.Failure;
            });
        }
        finally
        {
            DangerousDebugGate.LiveServerProbe = original;
        }

        Assert.Equal([true, false], results);
        Assert.Equal(DangerousDebugGate.FailureReason, failure);
        Assert.Contains(replies, l => l.StartsWith(DangerousDebugGate.RefusedLiveText, StringComparison.Ordinal));
    }
}
