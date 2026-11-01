using System.Text.RegularExpressions;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net.SessionTrace;
using Xunit;
using Trace = global::ArcaneCore.World.Net.SessionTrace.SessionTrace;

namespace ArcaneCore.World.Tests.Net.SessionTrace;

/// <summary>
/// <c>.debug trace session [$name] on|off [#minutes]</c> and <c>.debug trace session list</c> (session-trace spec, "Command" and "Options"):
/// the security level, the exact reply strings, the minutes default and clamp, the cap on traced characters, the off switch, and that the
/// session is attached while the trace is on.
/// </summary>
public sealed class SessionTraceCommandTests
{
    [Fact]
    public void Defaults_FollowTheSpec()
    {
        Assert.Equal("Diagnostics:SessionTrace", SessionTraceOptions.SectionName);
        var options = new SessionTraceOptions();
        Assert.True(options.Enabled);
        Assert.Equal(30, options.DefaultMinutes);
        Assert.Equal(240, options.MaxMinutes);
        Assert.Equal(50, options.LinesPerSecond);
        Assert.Equal(200, options.BurstLines);
        Assert.Equal(1000, options.SlowHandlerMicroseconds);
        Assert.Equal(16, options.MaxTracedCharacters);
    }

    [Fact]
    public void Level_ResolvesForAGameMaster_AndNotBelow()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "debug trace session", "debug trace session list" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Player));
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task OnListOff_Toggle_AttachTheSession_AndAnswerWithTheSpecStrings()
    {
        var logs = new CapturingLoggerProvider();
        var clock = new FakeTimeProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(clock, logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STCMD1", "Tracegm", AccountSecurity.GameMaster);
        await using WorldTestClient target = await host.EnterWorldAsync("STCMD2", "Traceme");
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        uint low = await SessionTraceTestKit.LowGuidAsync(host, "Traceme");
        await gm.CollectAsync();

        Assert.Equal("No session traces.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session list"));
        Assert.Equal("Traceme is not traced.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Traceme off"));
        Assert.Null(SessionTraceTestKit.SessionOf(host, "Traceme").SessionTrace);

        // Default minutes: 30.
        DateTimeOffset before = clock.GetUtcNow();
        Assert.Equal($"Session trace on for Traceme (guid {low}) for 30 min.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Traceme on"));
        Trace trace = await host.OnWorldAsync(() => Assert.Single(feature.Active));
        Assert.True(await host.OnWorldAsync(() => feature.IsTraced(trace.Guid)));
        Assert.Equal(low, trace.Guid.Low);
        Assert.Equal(before + TimeSpan.FromMinutes(30), trace.ExpiresAt);
        Assert.Same(trace, SessionTraceTestKit.SessionOf(host, "Traceme").SessionTrace);

        // A row for the character: name, guid and the connection's endpoint (loopback in the harness).
        string[] rows = await SessionTraceTestKit.CommandLinesAsync(gm, ".debug trace session list");
        string row = Assert.Single(rows, r => r.Contains("Traceme", StringComparison.Ordinal));
        Assert.Contains(low.ToString(), row, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", row, StringComparison.Ordinal);

        // Turning it on again replaces the expiry: the clamp for an explicit value, and the lower bound of 1 minute.
        Assert.Equal($"Session trace on for Traceme (guid {low}) for 5 min.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Traceme on 5"));
        Assert.Equal(before + TimeSpan.FromMinutes(5), (await host.OnWorldAsync(() => Assert.Single(feature.Active))).ExpiresAt);
        Assert.Equal($"Session trace on for Traceme (guid {low}) for 240 min (clamped to 240 min).", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Traceme on 999"));
        Assert.Equal(before + TimeSpan.FromMinutes(240), (await host.OnWorldAsync(() => Assert.Single(feature.Active))).ExpiresAt);
        string lower = await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Traceme on 0");
        Assert.StartsWith($"Session trace on for Traceme (guid {low}) for 1 min", lower, StringComparison.Ordinal);
        Assert.Equal(before + TimeSpan.FromMinutes(1), (await host.OnWorldAsync(() => Assert.Single(feature.Active))).ExpiresAt);

        string off = await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Traceme off");
        Assert.Matches(new Regex(@"^Session trace off for Traceme: \d+ lines, \d+ dropped\.$"), off);
        Assert.Empty(await host.OnWorldAsync(() => feature.Active.ToArray()));
        Assert.False(await host.OnWorldAsync(() => feature.IsTraced(trace.Guid)));
        Assert.Null(SessionTraceTestKit.SessionOf(host, "Traceme").SessionTrace);
        Assert.Equal("Traceme is not traced.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Traceme off"));
        Assert.Equal("No session traces.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session list"));
    }

    [Fact]
    public async Task WithoutAName_TheSelectedPlayerOrSelfIsTraced_AndAnUnknownNameTracesNobody()
    {
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STSELF", "Traceself", AccountSecurity.GameMaster);
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        uint low = await SessionTraceTestKit.LowGuidAsync(host, "Traceself");
        await gm.CollectAsync();

        Assert.Equal($"Session trace on for Traceself (guid {low}) for 30 min.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session on"));
        Assert.Equal(low, (await host.OnWorldAsync(() => Assert.Single(feature.Active))).Guid.Low);
        Assert.Matches(new Regex(@"^Session trace off for Traceself: \d+ lines, \d+ dropped\.$"), await SessionTraceTestKit.CommandAsync(gm, ".debug trace session off"));
        Assert.Empty(await host.OnWorldAsync(() => feature.Active.ToArray()));

        string unknown = await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Nobodyhere on");
        Assert.DoesNotContain("Session trace on", unknown, StringComparison.Ordinal);
        Assert.Empty(await host.OnWorldAsync(() => feature.Active.ToArray()));
    }

    [Fact]
    public async Task Configuration_BindsMaxMinutesAndMaxTracedCharacters_AndTheCapRefusesTheNextOne()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Diagnostics:SessionTrace:MaxMinutes"] = "10",
            ["Diagnostics:SessionTrace:DefaultMinutes"] = "7",
            ["Diagnostics:SessionTrace:MaxTracedCharacters"] = "1",
        };
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs, settings);
        await using WorldTestClient gm = await host.EnterWorldAsync("STCAP1", "Capgm", AccountSecurity.GameMaster);
        await using WorldTestClient other = await host.EnterWorldAsync("STCAP2", "Capother");
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        Assert.Equal(10, feature.Options.MaxMinutes);
        Assert.Equal(7, feature.Options.DefaultMinutes);
        Assert.Equal(1, feature.Options.MaxTracedCharacters);
        uint gmLow = await SessionTraceTestKit.LowGuidAsync(host, "Capgm");
        await gm.CollectAsync();

        Assert.Equal($"Session trace on for Capgm (guid {gmLow}) for 7 min.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Capgm on"));
        Assert.Equal("At most 1 characters can be traced at once.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Capother on"));
        Assert.Single(await host.OnWorldAsync(() => feature.Active.ToArray()));

        // Re-tracing the character that already is traced is not a second slot.
        Assert.Equal($"Session trace on for Capgm (guid {gmLow}) for 10 min (clamped to 10 min).", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Capgm on 11"));
    }

    [Fact]
    public async Task WhenDisabled_TheCommandRefuses_AndNothingIsTraced()
    {
        var settings = new Dictionary<string, string?> { ["Diagnostics:SessionTrace:Enabled"] = "false" };
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs, settings);
        await using WorldTestClient gm = await host.EnterWorldAsync("STOFF", "Offgm", AccountSecurity.GameMaster);
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        Assert.False(feature.Options.Enabled);
        await gm.CollectAsync();

        Assert.Equal("Session tracing is disabled by Diagnostics:SessionTrace:Enabled.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Offgm on"));
        Assert.Empty(await host.OnWorldAsync(() => feature.Active.ToArray()));
        Assert.Null(SessionTraceTestKit.SessionOf(host, "Offgm").SessionTrace);
        Assert.DoesNotContain(logs.Entries, e => e.Category == SessionTraceFeature.LogCategory);
    }

    [Fact]
    public async Task AGameMaster_CannotTraceAnAdministratorByName_AndSeesNoRowOfOne()
    {
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STSEC1", "Secgm", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("STSEC2", "Secadmin", AccountSecurity.Administrator);
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        await gm.CollectAsync();
        await admin.CollectAsync();

        string refused = await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Secadmin on");
        Assert.DoesNotContain("Session trace on", refused, StringComparison.Ordinal);
        Assert.Empty(await host.OnWorldAsync(() => feature.Active.ToArray()));

        // The administrator traces itself; the lower-security GM neither sees the row nor can turn it off.
        Assert.StartsWith("Session trace on for Secadmin", await SessionTraceTestKit.CommandAsync(admin, ".debug trace session on"), StringComparison.Ordinal);
        Assert.Equal("No session traces.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session list"));
        Assert.Single(await host.OnWorldAsync(() => feature.Active.ToArray()));
        string notOff = await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Secadmin off");
        Assert.DoesNotContain("Session trace off", notOff, StringComparison.Ordinal);
        Assert.Single(await host.OnWorldAsync(() => feature.Active.ToArray()));
    }
}
