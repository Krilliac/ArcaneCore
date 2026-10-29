using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.AntiCheat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Net.SessionTrace;

/// <summary>
/// The anti-cheat lines of a trace (session-trace spec, "Anti-cheat shadow"). The blink is the one AntiCheatWorldTests uses to prove
/// the checks are live: a client at 0.7 yards per 100 ms that then moves 80 yards in 100 ms is a Teleport finding for a player the feature checks.
/// For a player the feature never checks, the trace shows what would have been found, marked exempt, and changes nothing in the feature.
/// </summary>
public sealed class SessionTraceAntiCheatTests
{
    private const int NoBaselineGapMs = 600_000; // as AntiCheatWorldTests: a thread-pool stall must not turn the blink into a fresh baseline

    private static AntiCheatFeature AntiCheatOf(WorldTestHost host) => host.WorldServices.GetRequiredService<AntiCheatFeature>();

    private static Task ApplyAsync(WorldTestHost host, AntiCheatOptions options)
    {
        options.BaselineGapMs = NoBaselineGapMs;
        return host.OnWorldAsync(() => AntiCheatOf(host).ApplyOptions(options));
    }

    /// <summary>Starts walking, then blinks 80 yards in one 100 ms step; returns once the world has stored the block.</summary>
    private static async Task BlinkAsync(WorldTestHost host, WorldTestClient client, string name)
    {
        TraceMover mover = await SessionTraceTestKit.MoverAsync(host, client, name);
        await mover.SendAsync(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward);
        await mover.StepAsync(100, 80f);
        await SessionTraceTestKit.SettledAsync(host, name, mover);
        await host.OnWorldAsync(() => 0);
    }

    [Fact]
    public async Task AStaffPlayerTheFeatureNeverChecks_IsTracedAsExempt_AndNothingIsEnforced()
    {
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STAC1", "Exemptgm", AccountSecurity.GameMaster);
        AntiCheatFeature anticheat = AntiCheatOf(host);
        await ApplyAsync(host, new AntiCheatOptions()); // defaults: enabled, ExemptSecurity Moderator, Action Log
        int id = (int)await SessionTraceTestKit.LowGuidAsync(host, "Exemptgm");
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Exemptgm", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session on"), StringComparison.Ordinal);
        long recorded = await host.OnWorldAsync(() => anticheat.Recorded);

        await BlinkAsync(host, gm, "Exemptgm");

        TraceLine line = await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Event == "anticheat" && l.Detail.StartsWith("Teleport ", StringComparison.Ordinal), "the exempt Teleport line");
        Assert.StartsWith($"{AntiCheatViolation.Teleport} ", line.Detail, StringComparison.Ordinal);
        Assert.Contains(" weight=", line.Detail, StringComparison.Ordinal);
        Assert.Contains(" detail=", line.Detail, StringComparison.Ordinal);
        Assert.EndsWith(" exempt reason=staff (not enforced)", line.Detail, StringComparison.Ordinal);

        // The feature is untouched: nothing recorded, no score, no kick, no rubberband (the world still holds the blinked position).
        Assert.Equal(recorded, await host.OnWorldAsync(() => anticheat.Recorded));
        Assert.Equal(0, await host.OnWorldAsync(() => anticheat.Scores.Count));
        Assert.Equal(0f, await host.OnWorldAsync(() => anticheat.ScoreOf(id)));
        Assert.NotNull(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Exemptgm")));
        await gm.SendAsync(WorldOpcode.CmsgPing, [1, 0, 0, 0, 0, 0, 0, 0]);
        await gm.ReadUntilAsync(WorldOpcode.SmsgPong); // the connection is open and the session still serves
    }

    [Fact]
    public async Task APlayerWhenChecksAreOff_IsTracedAsExemptWithTheChecksOffReason()
    {
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STAC2", "Offchecksgm", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("STAC3", "Offchecksone");
        AntiCheatFeature anticheat = AntiCheatOf(host);
        await ApplyAsync(host, new AntiCheatOptions { Enabled = false });
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Offchecksone", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Offchecksone on"), StringComparison.Ordinal);
        long recorded = await host.OnWorldAsync(() => anticheat.Recorded);

        await BlinkAsync(host, player, "Offchecksone");

        TraceLine line = await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Event == "anticheat" && l.Detail.StartsWith("Teleport ", StringComparison.Ordinal), "the exempt Teleport line");
        Assert.StartsWith($"{AntiCheatViolation.Teleport} ", line.Detail, StringComparison.Ordinal);
        Assert.EndsWith(" exempt reason=checks-off (not enforced)", line.Detail, StringComparison.Ordinal);
        Assert.Equal(recorded, await host.OnWorldAsync(() => anticheat.Recorded));
        Assert.Equal(0, await host.OnWorldAsync(() => anticheat.Scores.Count));
    }

    [Fact]
    public async Task ACheckedPlayer_GetsExactlyOneEnforcedLine_AndNoExemptDuplicate()
    {
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STAC4", "Checkedgm", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("STAC5", "Checkedone");
        AntiCheatFeature anticheat = AntiCheatOf(host);
        await ApplyAsync(host, new AntiCheatOptions());
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Checkedone", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Checkedone on"), StringComparison.Ordinal);
        long recorded = await host.OnWorldAsync(() => anticheat.Recorded);

        await BlinkAsync(host, player, "Checkedone");

        TraceLine line = await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Event == "anticheat" && l.Detail.StartsWith("Teleport ", StringComparison.Ordinal), "the enforced Teleport line");
        Assert.StartsWith($"{AntiCheatViolation.Teleport} ", line.Detail, StringComparison.Ordinal);
        Assert.Contains(" weight=", line.Detail, StringComparison.Ordinal);
        Assert.Contains($" action={AntiCheatAction.Log} ", line.Detail + " ", StringComparison.Ordinal);
        Assert.Contains(" score=", line.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("exempt", line.Detail, StringComparison.Ordinal);
        Assert.True(await host.OnWorldAsync(() => anticheat.Recorded) > recorded, "the real feature recorded the finding");
        Assert.Single(SessionTraceTestKit.Lines(logs), l => l.Event == "anticheat" && l.Detail.StartsWith("Teleport ", StringComparison.Ordinal));
    }
}
