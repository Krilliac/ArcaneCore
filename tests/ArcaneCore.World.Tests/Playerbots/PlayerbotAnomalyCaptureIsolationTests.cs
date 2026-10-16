using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Every test world names its bot "Controlone" (guid 1), and the anomaly ledger is process-wide, so a capture that matched on the bot's
/// subject alone heard every other test world's reports about its namesake. A bot report carries the world it was made in, and a bot
/// capture keeps only its own world's: two live worlds with identically named bots do not see each other.
/// </summary>
public sealed class PlayerbotAnomalyCaptureIsolationTests
{
    private const string Area = "isolation";

    [Fact]
    public async Task TwoLiveWorlds_WithIdenticallyNamedBots_DoNotSeeEachOthersAnomalies()
    {
        await using WorldTestHost hostA = WorldTestHost.Start();
        await using WorldTestHost hostB = WorldTestHost.Start();
        WorldSession sessionA = await PlayerbotMovementControlTests.EnterAsync(hostA);
        WorldSession sessionB = await PlayerbotMovementControlTests.EnterAsync(hostB);
        try
        {
            Player botA = sessionA.Player!;
            Player botB = sessionB.Player!;
            Assert.Equal(PlayerbotAnomalies.SubjectOf(botA), PlayerbotAnomalies.SubjectOf(botB)); // the premise: the same subject in both worlds
            Assert.NotSame(botA.Map!.WorldIdentity, botB.Map!.WorldIdentity);

            using AnomalyCapture seenByA = PlayerbotAnomalies.CaptureFor(botA);
            using AnomalyCapture seenByB = PlayerbotAnomalies.CaptureFor(botB);

            await hostA.World.InvokeAsync(() => PlayerbotAnomalies.Report(botA, Area, "only-in-a", "world A failed a check"));
            await hostB.World.InvokeAsync(() => PlayerbotAnomalies.Report(botB, Area, "only-in-b", "world B failed a check"));

            Assert.True(seenByA.Any(PlayerbotAnomalies.Key(Area, "only-in-a")), seenByA.Describe());
            Assert.True(seenByB.Any(PlayerbotAnomalies.Key(Area, "only-in-b")), seenByB.Describe());
            Assert.False(seenByA.Any(PlayerbotAnomalies.Key(Area, "only-in-b")), "world A heard world B: " + seenByA.Describe());
            Assert.False(seenByB.Any(PlayerbotAnomalies.Key(Area, "only-in-a")), "world B heard world A: " + seenByB.Describe());
        }
        finally
        {
            sessionA.Kick();
            sessionB.Kick();
            await sessionA.ManagedClosed;
            await sessionB.ManagedClosed;
        }
    }

    [Fact]
    public async Task AWorldCapture_KeepsOnlyReportsStampedWithThatWorld()
    {
        await using WorldTestHost hostA = WorldTestHost.Start();
        await using WorldTestHost hostB = WorldTestHost.Start();
        WorldSession sessionA = await PlayerbotMovementControlTests.EnterAsync(hostA);
        WorldSession sessionB = await PlayerbotMovementControlTests.EnterAsync(hostB);
        try
        {
            using AnomalyCapture inA = PlayerbotAnomalies.CaptureIn(hostA.World, n => n.Key == PlayerbotAnomalies.Key(Area, "world-scoped"));

            await hostB.World.InvokeAsync(() => PlayerbotAnomalies.Report(sessionB.Player!, Area, "world-scoped", "reported in B"));
            Assert.False(inA.Any(PlayerbotAnomalies.Key(Area, "world-scoped")), inA.Describe());

            await hostA.World.InvokeAsync(() => PlayerbotAnomalies.Report(sessionA.Player!, Area, "world-scoped", "reported in A"));
            Assert.Equal(1, inA.Count(PlayerbotAnomalies.Key(Area, "world-scoped")));
        }
        finally
        {
            sessionA.Kick();
            sessionB.Kick();
            await sessionA.ManagedClosed;
            await sessionB.ManagedClosed;
        }
    }

    [Fact]
    public async Task ABotOnNoMap_WhenCaptured_HearsNoStampedReportFromAnyWorld()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        await using WorldTestHost hostTwin = WorldTestHost.Start();
        WorldSession sessionTwin = await PlayerbotMovementControlTests.EnterAsync(hostTwin);
        try
        {
            Player bot = session.Player!;
            await host.World.InvokeAsync(() => { bot.Map = null; return true; });
            Assert.Null(PlayerbotAnomalies.OriginOf(bot));
            using AnomalyCapture mapless = PlayerbotAnomalies.CaptureFor(bot); // resolved with no world: it must not fall back to the name alone

            await hostTwin.World.InvokeAsync(() => PlayerbotAnomalies.Report(sessionTwin.Player!, Area, "stamped-elsewhere", "a bot of the same name in another world"));
            Assert.False(mapless.Any(PlayerbotAnomalies.Key(Area, "stamped-elsewhere")), mapless.Describe());
        }
        finally
        {
            session.Kick();
            sessionTwin.Kick();
            await session.ManagedClosed;
            await sessionTwin.ManagedClosed;
        }
    }

    [Fact]
    public async Task ACapture_KeepsItsOwnWorld_WhenTheBotLeavesItsMapMidTest()
    {
        await using WorldTestHost hostA = WorldTestHost.Start();
        await using WorldTestHost hostB = WorldTestHost.Start();
        WorldSession sessionA = await PlayerbotMovementControlTests.EnterAsync(hostA);
        WorldSession sessionB = await PlayerbotMovementControlTests.EnterAsync(hostB);
        try
        {
            Player botA = sessionA.Player!;
            using AnomalyCapture seenByA = PlayerbotAnomalies.CaptureFor(botA); // the world is resolved here, once

            await hostA.World.InvokeAsync(() => { botA.Map = null; return true; }); // teardown: a live Map read would now be null
            await hostB.World.InvokeAsync(() => PlayerbotAnomalies.Report(sessionB.Player!, Area, "after-leave", "world B reported"));

            Assert.False(seenByA.Any(PlayerbotAnomalies.Key(Area, "after-leave")), seenByA.Describe());
        }
        finally
        {
            sessionA.Kick();
            sessionB.Kick();
            await sessionA.ManagedClosed;
            await sessionB.ManagedClosed;
        }
    }

    [Fact]
    public async Task TheAnomalyHistory_DoesNotKeepTheOriginToken()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var ledger = new AnomalyLedger();
        using var history = new PlayerbotAnomalyHistory(ledger);
        try
        {
            string subject = PlayerbotAnomalies.SubjectOf(session.Player!);
            ledger.Report(PlayerbotAnomalies.Prefix + Area, "kept", subject, "detail", AnomalySeverity.Warning, new object());
            PlayerbotAnomalyRecord record = Assert.Single(history.ForSubject(subject));
            Assert.Null(record.Notice.Origin);
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }
}
