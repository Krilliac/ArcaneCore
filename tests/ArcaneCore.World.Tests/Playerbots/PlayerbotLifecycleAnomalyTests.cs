using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// <c>bot-lifecycle/session-lost</c> (ManagedPlayerbotFeature.ReportSessionLostOnce) and <c>bot-brain/think-slow</c>
/// (<see cref="PlayerbotSlowThinkLog"/>) (Hardening Wave H1): each fires once on the failure it names and stays silent on the legitimate path.
/// </summary>
public sealed class PlayerbotLifecycleAnomalyTests
{
    private const string SessionLost = "bot-lifecycle/session-lost";
    private const string ThinkSlow = "bot-brain/think-slow";

    // --- session-lost ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SessionLost_Fires_OnceWhenARunningBotsSessionClosesUnderneathIt()
    {
        await using WorldTestHost host = Party.PartyTestHost.Start();
        Guid id = await Party.PartyTestHost.StartBotAsync(host, "Lostsession");
        ManagedPlayerbotFeature feature = Party.PartyTestHost.Feature(host);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == SessionLost && n.Detail.Contains(id.ToString("D"), StringComparison.Ordinal));
        WorldSession session = (await host.OnWorldAsync(() => feature.FindSession(id)))!;

        session.Kick();
        await session.ManagedClosed;

        await host.WaitForWorldAsync(() => capture.Any(SessionLost), "the session-lost report");
        // Many ticks later (and a checkpoint pass, which also looks at the closed session) it is still one report.
        await Task.Delay(1_000); // real clock: a few dozen world ticks
        await host.OnWorldAsync(() => true);
        AnomalyNotice notice = capture.Notices.Single();
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Contains("session lost while registered active", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("Lostsession", notice.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionLost_StaysSilent_WhenTheOperatorStopsTheBot_AndWhileItRuns()
    {
        await using WorldTestHost host = Party.PartyTestHost.Start();
        Guid id = await Party.PartyTestHost.StartBotAsync(host, "Stoppedfine");
        ManagedPlayerbotFeature feature = Party.PartyTestHost.Feature(host);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == SessionLost && n.Detail.Contains(id.ToString("D"), StringComparison.Ordinal));

        await Task.Delay(500);
        await host.OnWorldAsync(() => true);
        Assert.Empty(capture.Notices); // running: silent

        Assert.True((await feature.StopAsync(id.ToString())).Success);
        await Task.Delay(1_000); // real clock: a few dozen world ticks
        await host.OnWorldAsync(() => true);
        Assert.Empty(capture.Notices); // an operator stop closes the session on purpose: silent
    }

    // --- think-slow ------------------------------------------------------------------------------------------------------------------

    private static PlayerbotThinkProbe SpentProbe(double minimumMs)
        => PlayerbotThinkProbe.SpentForTest(minimumMs);

    [Fact]
    public void ThinkSlow_Fires_ForASlowThink_NamingTheBotWhenNoPlayerIsKnown()
    {
        var botId = Guid.NewGuid();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == ThinkSlow && n.Subject.Contains(botId.ToString(), StringComparison.Ordinal));
        PlayerbotThinkProbe probe = SpentProbe(8);

        bool slow = new PlayerbotSlowThinkLog(NullLogger.Instance).Observe(probe, 2, 1_000, botId, "Slowwithoutplayer", "brain", "Explore");

        Assert.True(slow);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Equal($"bot Slowwithoutplayer (id {botId})", notice.Subject);
        Assert.Contains("over 2.0 ms) driver=brain goal=Explore slowestPhase=", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThinkSlow_Fires_OnTheBotsOwnSubject_WhenThePlayerIsKnown()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            using AnomalyCapture capture = BotAnomalyCapture.For(player);
            await host.OnWorldAsync(() =>
            {
                PlayerbotThinkProbe probe = SpentProbe(8);
                new PlayerbotSlowThinkLog(NullLogger.Instance).Observe(probe, 2, 1_000, Guid.NewGuid(), player.Name, "controller", "n/a", player);
                return true;
            });

            AnomalyNotice notice = Assert.Single(capture.Notices);
            Assert.Equal(ThinkSlow, notice.Key);
            Assert.Equal(PlayerbotAnomalies.SubjectOf(player), notice.Subject);
            Assert.Contains("map ", notice.Detail, StringComparison.Ordinal);
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Theory]
    [InlineData(10_000.0)] // a think well under the threshold
    [InlineData(0.0)]      // the check is off
    [InlineData(-1.0)]
    public void ThinkSlow_StaysSilent_ForAFastThink_AndWhenTheCheckIsOff(double thresholdMs)
    {
        var botId = Guid.NewGuid();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == ThinkSlow && n.Subject.Contains(botId.ToString(), StringComparison.Ordinal));
        PlayerbotThinkProbe probe = SpentProbe(0);

        bool slow = new PlayerbotSlowThinkLog(NullLogger.Instance).Observe(probe, thresholdMs, 1_000, botId, "Fastthinker", "brain", "Explore");

        Assert.False(slow);
        Assert.Empty(capture.Notices);
    }
}
