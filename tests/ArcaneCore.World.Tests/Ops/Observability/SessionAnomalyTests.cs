using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.Observability;

/// <summary>H1 outcome checks of the session and network layer: <c>session/*</c> keys, each proven to fire and to stay silent.</summary>
public sealed class SessionAnomalyTests
{
    private const string Storm = "session/rejection-storm";
    private const string NoReason = "session/kick-no-reason";
    private const string AfterClose = "session/send-after-close";
    private const string Overdue = "session/logout-overdue";

    private static async Task<WorldSession> NewBotAsync(WorldTestHost host, string username)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = username, Salt = new byte[32], Verifier = new byte[32] });
        return await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions(), NullLogger.Instance);
    }

    private static AnomalyCapture CaptureOf(WorldSession session)
    {
        string subject = session.AnomalySubject;
        return AnomalyLedger.Shared.Capture(n => n.Subject == subject);
    }

    [Fact]
    public void TheStormTracker_FiresOncePerStorm_AndReArmsAfterAQuietWindow()
    {
        var tracker = new RejectionStormTracker();
        long t = 1000;
        for (int i = 1; i < RejectionStormTracker.Threshold; i++)
        {
            Assert.False(tracker.Note(t + i, out _));
        }

        Assert.True(tracker.Note(t + 500, out int count));
        Assert.Equal(RejectionStormTracker.Threshold, count);
        Assert.False(tracker.Note(t + 600, out _)); // the same storm

        t += RejectionStormTracker.WindowMs * 3; // a quiet window
        Assert.False(tracker.Note(t, out _));
        t += RejectionStormTracker.WindowMs;
        for (int i = 0; i < RejectionStormTracker.Threshold - 1; i++)
        {
            tracker.Note(t + i, out _);
        }

        Assert.True(tracker.Note(t + 900, out _)); // a new storm
    }

    [Fact]
    public async Task ARepeatedlyRefusedAccount_IsReportedAsAStorm_AndAFewRefusalsAreNot()
    {
        await using var host = WorldTestHost.Start();
        WorldSession quiet = await NewBotAsync(host, "SANOSTORM");
        WorldSession loud = await NewBotAsync(host, "SASTORM");
        using AnomalyCapture capQuiet = CaptureOf(quiet);
        using AnomalyCapture capLoud = CaptureOf(loud);
        byte[] refusal = SpellPackets.BuildCastResult(6478, SpellCastResult.BadTargets);

        for (int i = 0; i < RejectionStormTracker.Threshold - 1; i++)
        {
            quiet.Send(WorldOpcode.SmsgCastResult, refusal);
        }

        for (int i = 0; i < RejectionStormTracker.Threshold + 20; i++)
        {
            loud.Send(WorldOpcode.SmsgCastResult, refusal);
        }

        Assert.False(capQuiet.Any(Storm));
        Assert.Equal(1, capLoud.Count(Storm));
        quiet.Kick("test over");
        loud.Kick("test over");
    }

    private static byte[] MailResultPacket(uint mailId, uint result)
    {
        var payload = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, mailId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), result);
        return payload;
    }

    [Fact]
    public async Task MailInternalErrorSent_ReportsServerErrorRejection()
    {
        const string key = "client/rejection-server-error";
        await using var host = WorldTestHost.Start();
        WorldSession faulty = await NewBotAsync(host, "SASRVERRA");
        WorldSession refused = await NewBotAsync(host, "SASRVERRB");
        using AnomalyCapture capFaulty = CaptureOf(faulty);
        using AnomalyCapture capRefused = CaptureOf(refused);

        faulty.Send(WorldOpcode.SmsgSendMailResult, MailResultPacket(77, (uint)ArcaneCore.Game.Economy.MailResult.InternalError));
        refused.Send(WorldOpcode.SmsgSendMailResult, MailResultPacket(77, (uint)ArcaneCore.Game.Economy.MailResult.NotEnoughMoney)); // the player's refusal, not the server's
        refused.Send(WorldOpcode.SmsgSendMailResult, MailResultPacket(77, 0)); // success: not a refusal at all

        Assert.Equal(1, capFaulty.Count(key));
        Assert.Contains("MailResult InternalError", capFaulty.Notices.Single().Detail, StringComparison.Ordinal);
        Assert.False(capRefused.Any(key));
        faulty.Kick("test over");
        refused.Kick("test over");
    }

    [Fact]
    public async Task AKickWithoutAReason_IsReported_AndOneWithAReasonIsNot()
    {
        await using var host = WorldTestHost.Start();
        WorldSession anonymous = await NewBotAsync(host, "SAKICKA");
        WorldSession explained = await NewBotAsync(host, "SAKICKB");
        using AnomalyCapture capAnon = CaptureOf(anonymous);
        using AnomalyCapture capExplained = CaptureOf(explained);

        anonymous.Kick();
        anonymous.Kick(); // idempotent: one report
        explained.Kick("test: explained kick");

        Assert.Equal(1, capAnon.Count(NoReason));
        Assert.Contains("AKickWithoutAReason", capAnon.Notices.Single().Detail, StringComparison.Ordinal);
        Assert.False(capExplained.Any(NoReason));
        Assert.Equal("test: explained kick", explained.KickReason);
    }

    [Fact]
    public async Task SendingToASessionClosedLongAgo_IsReportedOnce_ButNotRightAfterTheClose()
    {
        await using var host = WorldTestHost.Start();
        WorldSession fresh = await NewBotAsync(host, "SACLOSEA");
        WorldSession lingering = await NewBotAsync(host, "SACLOSEB");
        using AnomalyCapture capFresh = CaptureOf(fresh);
        using AnomalyCapture capLingering = CaptureOf(lingering);
        lingering.SendAfterCloseGraceMs = 0;
        fresh.Kick("test over");
        lingering.Kick("test over");

        fresh.Send(WorldOpcode.SmsgNotification, [(byte)'x', 0]);
        lingering.Send(WorldOpcode.SmsgNotification, [(byte)'x', 0]);
        lingering.Send(WorldOpcode.SmsgNotification, [(byte)'x', 0]);

        Assert.False(capFresh.Any(AfterClose)); // the world thread has not removed the player yet: normal
        Assert.Equal(1, capLingering.Count(AfterClose));
    }

    [Fact]
    public async Task ALogoutThatRunsPastItsCountdown_IsReported_AndOneWithinItIsNot()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("SALOGOUT", "Salogoutaa");
        Player player = await host.PlayerAsync("Salogoutaa");
        var session = (WorldSession)player.Session;
        using AnomalyCapture cap = CaptureOf(session);

        bool inTime = await host.World.InvokeAsync(() =>
        {
            player.BeginLogout(host.World.NowMs); // just started: well inside the countdown
            session.ProcessWorldPackets(player);
            bool silent = !cap.Any(Overdue);
            player.CancelLogout();
            return silent;
        });
        Assert.True(inTime);

        await host.World.InvokeAsync(() =>
        {
            uint late = host.World.Options.LogoutDelayMs + session.LogoutOverdueGraceMs + 5_000;
            player.BeginLogout(unchecked(host.World.NowMs - late));
            session.ProcessWorldPackets(player); // runs before the map's logout timer in a real tick
            session.ProcessWorldPackets(player);
            player.CancelLogout();
            return 0;
        });

        Assert.Equal(1, cap.Count(Overdue));
    }
}
