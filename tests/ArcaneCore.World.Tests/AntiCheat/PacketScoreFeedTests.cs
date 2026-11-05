using System.Globalization;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.MapFeed;
using ArcaneCore.World.Tests.PacketHeuristics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// <c>World:PacketHeuristics:FeedAntiCheatScore</c>: packet findings of real players reach the AntiCheat score only when the option is on, only for
/// the checks whose only honest cause is a modified client (shape, malformed, dev-opcode), never for staff, bots or tool traffic, and through a bounded queue applied on the world thread. Written without a build in the
/// lane that added them: unproven until the integration gate runs them.
/// </summary>
public sealed class PacketScoreFeedTests
{
    private static readonly byte[] BadStandState = [0, 0, 0, 0, 9]; // CMSG_STANDSTATECHANGE is 4 bytes: one trailing byte is a shape finding

    private static string DevOpcodeKey => "packet/" + PacketChecks.Code(PacketCheck.DevOpcode);

    private static async Task<AntiCheatFeature> QuietAntiCheatAsync(WorldTestHost host)
    {
        AntiCheatFeature antiCheat = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        await host.OnWorldAsync(() => antiCheat.ApplyOptions(new AntiCheatOptions { Action = AntiCheatAction.Log, DecayPerSecond = 0 }));
        return antiCheat;
    }

    private static Task DrainAsync(WorldTestHost host, AntiCheatFeature antiCheat) => host.OnWorldAsync(antiCheat.DrainPacketScores);

    [Fact]
    public async Task WithTheOptionOff_AShapeFinding_IsReported_AndNeverScores()
    {
        await using var host = WorldTestHost.Start();
        AntiCheatFeature antiCheat = await QuietAntiCheatAsync(host);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PSOFF", "Psoffone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, _) = PacketHeuristicsTestKit.Enable(host);
            Assert.False(feature.Options.FeedAntiCheatScore);

            await client.SendAsync(WorldOpcode.CmsgStandstatechange, BadStandState);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            await DrainAsync(host, antiCheat);

            Assert.Equal(1, feature.Count(PacketCheck.Shape, PacketTraffic.Player)); // the finding exists
            Assert.Equal(0f, antiCheat.ScoreOf((int)session.Player!.Guid.Low));
            Assert.Equal(0, antiCheat.Recorded);
        }
    }

    [Fact]
    public async Task WithTheOptionOn_AShapeFinding_AddsTheWeight_AndAWrongStateOneAddsNothing()
    {
        await using var host = WorldTestHost.Start();
        AntiCheatFeature antiCheat = await QuietAntiCheatAsync(host);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PSON", "Psonone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, _) = PacketHeuristicsTestKit.Enable(host, o =>
            {
                o.FeedAntiCheatScore = true;
                o.ScoreWeight = 7;
            });
            int characterId = (int)session.Player!.Guid.Low;

            await client.SendAsync(WorldOpcode.CmsgCharEnum, []); // wrong-state in the world: reported, never scored
            await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.WrongState, PacketTraffic.Player) >= 1, "the wrong-state finding");
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            await DrainAsync(host, antiCheat);
            Assert.Equal(0f, antiCheat.ScoreOf(characterId));

            await client.SendAsync(WorldOpcode.CmsgStandstatechange, BadStandState);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            await DrainAsync(host, antiCheat);

            Assert.Equal(1, feature.Count(PacketCheck.Shape, PacketTraffic.Player));
            Assert.Equal(7f, antiCheat.ScoreOf(characterId));
            Assert.Equal(1, antiCheat.Recorded);
        }
    }

    [Fact]
    public async Task WithTheOptionOn_AStaffSession_NeverScores_EvenWhenItsTrafficIsChecked()
    {
        await using var host = WorldTestHost.Start();
        AntiCheatFeature antiCheat = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        // AntiCheat's own staff exemption is off, so only the packet side's traffic guard can keep the staff session from scoring.
        await host.OnWorldAsync(() => antiCheat.ApplyOptions(new AntiCheatOptions { Action = AntiCheatAction.Log, DecayPerSecond = 0, ExemptSecurity = AccountSecurity.Player }));
        (WorldTestClient staff, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PSGM", "Psgmone", AccountSecurity.GameMaster);
        await using (staff)
        {
            (PacketHeuristicsFeature feature, _) = PacketHeuristicsTestKit.Enable(host, o =>
            {
                o.FeedAntiCheatScore = true;
                o.ExemptStaff = false; // the staff's traffic is checked and counted, but never scored
            });

            await staff.SendAsync(WorldOpcode.CmsgStandstatechange, BadStandState);
            await PacketHeuristicsTestKit.SettleAsync(host, staff, session);
            await DrainAsync(host, antiCheat);

            Assert.Equal(1, feature.Count(PacketCheck.Shape, PacketTraffic.Staff));
            Assert.Equal(0f, antiCheat.ScoreOf((int)session.Player!.Guid.Low));
            Assert.Equal(0, antiCheat.Recorded);

            // Control: the same session queued directly is scored, so the AntiCheat side exempts nothing here and the zero above came from the guard.
            Assert.True(antiCheat.EnqueuePacketFinding(session, 1f, "packet shape"));
            await DrainAsync(host, antiCheat);
            Assert.Equal(1, antiCheat.Recorded);
        }
    }

    [Fact]
    public async Task WithTheOptionOn_AToolInjectedPacket_NeverScoresItsTarget_ButTheTargetsOwnPacketDoes()
    {
        var permit = new PacketToolsPermit(
            Options.Create(new WorldPacketToolsOptions { Enabled = true }),
            Options.Create(new WorldMapFeedOptions { Enabled = true }),
            new TestEnvironment(),
            liveServer: false);
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton(permit));
        AntiCheatFeature antiCheat = await QuietAntiCheatAsync(host);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PSTOOL", "Pstoolone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host, o =>
            {
                o.FeedAntiCheatScore = true;
                o.ScoreWeight = 7;
            });
            using AnomalyCapture capture = ledger.Capture();
            int characterId = (int)session.Player!.Guid.Low;

            // Tool traffic, first on this session: nothing is throttled, so the finding is reported and reaches FeedScore, where only the traffic guard stops it.
            InjectVerdict verdict = await session.InjectAsync(WorldOpcode.CmsgGodmode, [], TimeSpan.FromSeconds(5), CancellationToken.None);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            await DrainAsync(host, antiCheat);

            Assert.Equal(InjectVerdict.Handled, verdict);
            Assert.Equal(1, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Tool));
            AnomalyNotice toolReport = Assert.Single(capture.Notices, n => n.Key == DevOpcodeKey);
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"tool {session.AccountId}"), toolReport.Subject);
            Assert.Equal(0, antiCheat.Recorded);
            Assert.Equal(0f, antiCheat.ScoreOf(characterId));

            // Control: the same session's own stripped opcode (another opcode, so its own throttle slot) does score, so the zero above came from the guard.
            await client.SendAsync(WorldOpcode.CmsgCheatSetmoney, []);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            await DrainAsync(host, antiCheat);

            Assert.Equal(1, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Player));
            Assert.Equal(2, capture.Notices.Count(n => n.Key == DevOpcodeKey));
            Assert.Equal(1, antiCheat.Recorded);
            Assert.Equal(7f, antiCheat.ScoreOf(characterId));
        }
    }

    [Fact]
    public async Task WithTheOptionOn_AnUnexemptedBot_NeverScores()
    {
        await using var host = WorldTestHost.Start();
        AntiCheatFeature antiCheat = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        // AntiCheat does not exempt managed bots here, so only the packet side's traffic guard can keep this finding out of the score.
        await host.OnWorldAsync(() => antiCheat.ApplyOptions(new AntiCheatOptions { Action = AntiCheatAction.Log, DecayPerSecond = 0, ExemptManagedBots = false }));
        (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host, o =>
        {
            o.FeedAntiCheatScore = true;
            o.ExemptBots = false; // the bot's findings are checked and counted
        });
        using AnomalyCapture capture = ledger.Capture();
        WorldSession bot = await PacketHeuristicsTestKit.EnterBotAsync(host, "PSBOT", "Psbotone");
        try
        {
            // A stripped developer opcode through the managed action path, first on this session, so it is reported and reaches FeedScore.
            bool sent = await host.World.InvokeAsync(() => bot.TryManagedAction(WorldOpcode.CmsgGodmode, []));
            await DrainAsync(host, antiCheat);

            Assert.False(sent);
            Assert.Equal(1, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Bot));
            Assert.Single(capture.Notices, n => n.Key == DevOpcodeKey);
            Assert.Equal(0, antiCheat.Recorded);
            Assert.Equal(0f, antiCheat.ScoreOf((int)bot.Player!.Guid.Low));

            // Control: the same bot queued directly is scored, so the AntiCheat side exempts nothing here and the zero above came from the guard.
            Assert.True(antiCheat.EnqueuePacketFinding(bot, 1f, "packet shape"));
            await DrainAsync(host, antiCheat);
            Assert.Equal(1, antiCheat.Recorded);
        }
        finally
        {
            bot.Kick("test over");
            await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task TheStatusLine_KeepsReportOnly_WithTheFeedOff_AndNamesTheWeight_WithItOn()
    {
        await using var host = WorldTestHost.Start();
        (PacketHeuristicsFeature feature, _) = PacketHeuristicsTestKit.Enable(host);
        Assert.StartsWith("Packet heuristics: ON (report only). Bots", feature.StatusLines()[0], StringComparison.Ordinal);

        PacketHeuristicsTestKit.Enable(host, o =>
        {
            o.FeedAntiCheatScore = true;
            o.ScoreWeight = 7;
        });
        Assert.StartsWith("Packet heuristics: ON (reports; AntiCheat score feed ON, weight 7). Bots", feature.StatusLines()[0], StringComparison.Ordinal);
    }

    /// <summary>One shape finding through the feed under the real decay (2 per second), the default thresholds and a ceiling above Log; the verdict it got.</summary>
    private static async Task<(float Score, AntiCheatAction Action)> OneFindingUnderTheRealDecayAsync(string account, string character, int weight)
    {
        await using var host = WorldTestHost.Start();
        AntiCheatFeature antiCheat = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        await host.OnWorldAsync(() => antiCheat.ApplyOptions(new AntiCheatOptions { Action = AntiCheatAction.Kick }));
        var verdicts = new List<(float Score, AntiCheatAction Action)>();
        antiCheat.FindingRecorded += (_, finding, score, action) =>
        {
            if (finding.Type == AntiCheatViolation.Packet)
            {
                lock (verdicts)
                {
                    verdicts.Add((score, action));
                }
            }
        };

        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, account, character);
        await using (client)
        {
            (PacketHeuristicsFeature feature, _) = PacketHeuristicsTestKit.Enable(host, o =>
            {
                o.FeedAntiCheatScore = true;
                o.ScoreWeight = weight;
            });
            await client.SendAsync(WorldOpcode.CmsgStandstatechange, BadStandState);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            await DrainAsync(host, antiCheat);
            Assert.Equal(1, feature.Count(PacketCheck.Shape, PacketTraffic.Player));
        }

        lock (verdicts)
        {
            Assert.Single(verdicts);
            return verdicts[0];
        }
    }

    [Fact]
    public async Task UnderTheRealDecay_AFindingAtTheDefaultWeight_PeaksFarBelowTheAlert_SoItStaysAtLog()
    {
        (float score, AntiCheatAction action) = await OneFindingUnderTheRealDecayAsync("PSDEF", "Psdefone", new PacketHeuristicsOptions().ScoreWeight);

        Assert.Equal(AntiCheatAction.Log, action);
        Assert.InRange(score, 2.9f, 3.0f);
    }

    [Fact]
    public async Task UnderTheRealDecay_AFindingAtTheAlertWeight_ReachesTheGmAlert_ThroughTheFeed()
    {
        (float score, AntiCheatAction action) = await OneFindingUnderTheRealDecayAsync("PSALR", "Psalrone", (int)new AntiCheatOptions().ScoreGmAlert);

        Assert.True(action >= AntiCheatAction.GmAlert, $"score {score}, action {action}");
    }

    [Fact]
    public async Task TheQueue_IsBounded_AndEverythingQueuedIsAppliedOnTheWorldThread()
    {
        await using var host = WorldTestHost.Start();
        AntiCheatFeature antiCheat = await QuietAntiCheatAsync(host);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PSQ", "Psqone");
        await using (client)
        {
            (int accepted, int refused, long dropped, long recorded, bool acceptsAgain) = await host.OnWorldAsync(() =>
            {
                // On the world thread no tick can drain in between, so the cap is exact.
                int ok = 0, no = 0;
                for (int i = 0; i < AntiCheatFeature.MaxQueuedPacketScores + 1; i++)
                {
                    if (antiCheat.EnqueuePacketFinding(session, 0.1f, "packet shape"))
                    {
                        ok++;
                    }
                    else
                    {
                        no++;
                    }
                }

                antiCheat.DrainPacketScores();
                return (ok, no, antiCheat.PacketScoresDropped, antiCheat.Recorded, antiCheat.EnqueuePacketFinding(session, 0.1f, "packet shape"));
            });

            Assert.Equal(AntiCheatFeature.MaxQueuedPacketScores, accepted);
            Assert.Equal(1, refused);
            Assert.Equal(1, dropped);
            Assert.Equal(AntiCheatFeature.MaxQueuedPacketScores, recorded);
            Assert.True(acceptsAgain); // the drain freed the queue
        }
    }

    [Fact]
    public void ScoreWeightAndTheFeedSwitch_AreValidated_AndOffByDefault()
    {
        Assert.False(new PacketHeuristicsOptions().FeedAntiCheatScore);
        Assert.Empty(new PacketHeuristicsOptions { FeedAntiCheatScore = true, ScoreWeight = 100 }.Validate());
        Assert.NotEmpty(new PacketHeuristicsOptions { ScoreWeight = 0 }.Validate());
        Assert.NotEmpty(new PacketHeuristicsOptions { ScoreWeight = 101 }.Validate());
    }

    [Fact]
    public void OnlyTheChecksWithNoHonestFalsePositive_AreScored()
    {
        // cadence (a crowded city's query burst), orphan-ack (a late acknowledgement after a superseding change) and replay (a legitimately repeated
        // payload) are listed with honest false-positive risks in docs/areas/packet-heuristics.md, so they report but never score.
        PacketCheck[] scored = [PacketCheck.Shape, PacketCheck.DevOpcode, PacketCheck.Malformed];
        foreach (PacketCheck check in Enum.GetValues<PacketCheck>())
        {
            Assert.Equal(scored.Contains(check), PacketHeuristicsFeature.IsScoredCheck(check));
            if (scored.Contains(check))
            {
                Assert.StartsWith("packet ", PacketChecks.ScoreDetail(check), StringComparison.Ordinal);
            }
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "PacketScoreFeedTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
