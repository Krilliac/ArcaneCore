using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>
/// The packet heuristics on the managed bot path (<c>WorldSession.TryManagedAction</c>, which bypasses <c>DispatchAsync</c>): exempt by default,
/// every check when bots are included, the report throttle, and the proof that nothing about what the action returns or throws changes.
/// </summary>
public sealed class PacketHeuristicsBotTests
{
    private static string Key(PacketCheck check) => "packet/" + PacketChecks.Code(check);

    private static Task<T> OnWorld<T>(WorldTestHost host, Func<T> action) => host.World.InvokeAsync(action);

    private static async Task Finish(WorldSession session)
    {
        session.Kick("test over");
        await session.ManagedClosed;
    }

    [Fact]
    public async Task BotsAreExemptByDefault_AndRecordNothingEvenWhenTheFeatureIsOn()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsKit.EnterBotAsync(host, "PHBOTX", "Phbotxone");
        try
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host); // ExemptBots is true by default
            using AnomalyCapture capture = ledger.Capture();

            await OnWorld(host, () =>
            {
                for (int i = 0; i < 30; i++)
                {
                    Assert.False(bot.TryManagedAction(WorldOpcode.CmsgBootme, []));
                    bot.TryManagedAction(WorldOpcode.CmsgStandstatechange, [0, 0, 0, 0, 9]);
                    bot.TryManagedAction(WorldOpcode.CmsgEmote, PacketHeuristicsKit.U32(3));
                }

                return true;
            });

            Assert.Empty(capture.Notices);
            foreach (PacketCheck check in Enum.GetValues<PacketCheck>())
            {
                Assert.Equal(0, PacketHeuristicsKit.Total(feature, check));
            }

            Assert.Null(bot.HeuristicsState);
        }
        finally
        {
            await Finish(bot);
        }
    }

    [Fact]
    public async Task OffByDefault_EvenWithBotsIncludedInTheOptions_NothingIsRecorded()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsKit.EnterBotAsync(host, "PHBOTO", "Phbotoone");
        try
        {
            PacketHeuristicsFeature feature = PacketHeuristicsKit.FeatureOf(host);
            var ledger = new AnomalyLedger();
            feature.Ledger = ledger;
            Assert.True(feature.TryApply(new PacketHeuristicsOptions { Enabled = false, ExemptBots = false }, out _));
            using AnomalyCapture capture = ledger.Capture();

            await OnWorld(host, () =>
            {
                for (int i = 0; i < 30; i++)
                {
                    bot.TryManagedAction(WorldOpcode.CmsgBootme, []);
                }

                return true;
            });

            Assert.Empty(capture.Notices);
            Assert.Null(bot.HeuristicsState);
        }
        finally
        {
            await Finish(bot);
        }
    }

    [Fact]
    public async Task WithBotsIncluded_EveryCheckFiresOnTheBotPath_AndTheActionBehavesAsBefore()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsKit.EnterBotAsync(host, "PHBOTA", "Phbotaone");
        long clock = 1_000_000;
        try
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host, o => o.ExemptBots = false, () => Interlocked.Read(ref clock));
            using AnomalyCapture capture = ledger.Capture();
            Player player = bot.Player!;
            byte[] teleportAck = new byte[16];
            BitConverter.GetBytes(player.Guid.Value).CopyTo(teleportAck, 0);

            await OnWorld(host, () =>
            {
                // unknown-opcode: no handler; the action is refused as it always was.
                Assert.False(bot.TryManagedAction(WorldOpcode.CmsgBootme, []));

                // wrong-state: a session opcode sent as a world action.
                Assert.False(bot.TryManagedAction(WorldOpcode.CmsgPlayerLogin, new byte[8]));

                // shape: one byte too many; the handler still runs and the action succeeds.
                Assert.True(bot.TryManagedAction(WorldOpcode.CmsgStandstatechange, [0, 0, 0, 0, 9]));

                // malformed: the handler reads past the end and the exception reaches the caller exactly as before.
                Assert.IsType<ArgumentOutOfRangeException>(Record.Exception(() => bot.TryManagedAction(WorldOpcode.CmsgStandstatechange, [1, 0])));

                // orphan-ack: a teleport ack with no teleport pending (the handler ignores it).
                Assert.True(bot.TryManagedAction(WorldOpcode.MsgMoveTeleportAck, teleportAck));

                // incapacitated-action: a cast while stunned (Info for a bot).
                player.UnitFlags |= UnitFlags.Stunned;
                Assert.True(bot.TryManagedAction(WorldOpcode.CmsgCastSpell, [1, 0, 0, 0, 0, 0]));
                player.UnitFlags &= ~UnitFlags.Stunned;

                // replay: the same emote five times in a second.
                for (int i = 0; i < 5; i++)
                {
                    Assert.True(bot.TryManagedAction(WorldOpcode.CmsgEmote, PacketHeuristicsKit.U32(2)));
                    Interlocked.Add(ref clock, 10);
                }

                // cadence: eight different emotes inside one second.
                Interlocked.Add(ref clock, 5000);
                for (uint i = 1; i <= 8; i++)
                {
                    Assert.True(bot.TryManagedAction(WorldOpcode.CmsgEmote, PacketHeuristicsKit.U32(i)));
                    Interlocked.Add(ref clock, 10);
                }

                return true;
            });

            AnomalyNotice unknown = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.UnknownOpcode));
            Assert.Equal(AnomalySeverity.Info, unknown.Severity);
            Assert.Contains("CMSG_BOOTME", unknown.Detail, StringComparison.Ordinal);
            Assert.Equal("bot Phbotaone", unknown.Subject);

            AnomalyNotice wrongState = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.WrongState));
            Assert.Contains("session opcode sent as a world action", wrongState.Detail, StringComparison.Ordinal);

            Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Shape) && n.Detail.Contains("trailing 1 bytes", StringComparison.Ordinal));
            Assert.Equal(AnomalySeverity.Error, Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Malformed)).Severity);
            Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.OrphanAck));
            AnomalyNotice incapacitated = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.IncapacitatedAction));
            Assert.Equal(AnomalySeverity.Info, incapacitated.Severity);
            Assert.Contains("stunned", incapacitated.Detail, StringComparison.Ordinal);
            Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Replay));
            Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Cadence));

            Assert.Equal(0, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player));
            Assert.True(feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Bot) >= 1);
            Assert.NotNull(bot.HeuristicsState);
        }
        finally
        {
            await Finish(bot);
        }
    }

    [Fact]
    public async Task WrongState_ABotActionOutsideTheWorld_IsReported()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsKit.NewBotAtCharacterScreenAsync(host, "PHBOTC");
        try
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host, o => o.ExemptBots = false);
            using AnomalyCapture capture = ledger.Capture();

            bool sent = await OnWorld(host, () => bot.TryManagedAction(WorldOpcode.CmsgEmote, PacketHeuristicsKit.U32(1)));

            Assert.False(sent); // refused as before
            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.WrongState));
            Assert.Contains("CharacterSelect", notice.Detail, StringComparison.Ordinal);
        }
        finally
        {
            await Finish(bot);
        }
    }

    [Fact]
    public async Task AChaosProbe_IsNeverChecked()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsKit.EnterBotAsync(host, "PHBOTH", "Phbothone");
        try
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host, o => o.ExemptBots = false);
            using AnomalyCapture capture = ledger.Capture();
            await OnWorld(host, () =>
            {
                bot.IsInChaosProbe = true;
                bot.TryManagedAction(WorldOpcode.CmsgBootme, []);
                bot.TryManagedAction(WorldOpcode.CmsgStandstatechange, [0, 0, 0, 0, 9]);
                bot.IsInChaosProbe = false;
                return true;
            });

            Assert.Empty(capture.Notices);
            Assert.Equal(0, PacketHeuristicsKit.Total(feature, PacketCheck.UnknownOpcode) + PacketHeuristicsKit.Total(feature, PacketCheck.Shape));
        }
        finally
        {
            await Finish(bot);
        }
    }

    [Fact]
    public async Task TheThrottle_AHundredOccurrences_GiveOneLedgerReportAndACountOfAHundred()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsKit.EnterBotAsync(host, "PHBOTT", "Phbottone");
        long clock = 5_000_000;
        try
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host, o => o.ExemptBots = false, () => Interlocked.Read(ref clock));
            using AnomalyCapture capture = ledger.Capture();

            await OnWorld(host, () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    bot.TryManagedAction(WorldOpcode.CmsgBootme, []);
                    Interlocked.Add(ref clock, 50); // 5 s in all: inside the 10 s interval
                }

                return true;
            });

            Assert.Equal(1, capture.Count(Key(PacketCheck.UnknownOpcode)));
            Assert.Equal(100, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Bot));

            // Another opcode is its own throttle key and reports at once; the same opcode after the interval reports again, carrying the held-back count.
            await OnWorld(host, () =>
            {
                bot.TryManagedAction(WorldOpcode.CmsgDblookup, []);
                Interlocked.Add(ref clock, 10_000);
                bot.TryManagedAction(WorldOpcode.CmsgBootme, []);
                return true;
            });

            Assert.Equal(3, capture.Count(Key(PacketCheck.UnknownOpcode)));
            Assert.Contains("(+99 more since the last report)", capture.Notices.Last().Detail, StringComparison.Ordinal);
            Assert.Equal(102, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Bot));
        }
        finally
        {
            await Finish(bot);
        }
    }

    [Fact]
    public async Task Reset_ZeroesTheCounters_AndLetsASessionReportAgainAtOnce()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsKit.EnterBotAsync(host, "PHBOTR", "Phbotrone");
        long clock = 9_000_000;
        try
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host, o => o.ExemptBots = false, () => Interlocked.Read(ref clock));
            using AnomalyCapture capture = ledger.Capture();
            await OnWorld(host, () => bot.TryManagedAction(WorldOpcode.CmsgBootme, []));
            await OnWorld(host, () => bot.TryManagedAction(WorldOpcode.CmsgBootme, []));
            Assert.Equal(1, capture.Count(Key(PacketCheck.UnknownOpcode)));
            Assert.Equal(2, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Bot));

            feature.Reset();
            Assert.Equal(0, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Bot));
            await OnWorld(host, () => bot.TryManagedAction(WorldOpcode.CmsgBootme, []));

            Assert.Equal(2, capture.Count(Key(PacketCheck.UnknownOpcode))); // no clock advance: only the reset let it through
            Assert.Equal(1, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Bot));
        }
        finally
        {
            await Finish(bot);
        }
    }
}
