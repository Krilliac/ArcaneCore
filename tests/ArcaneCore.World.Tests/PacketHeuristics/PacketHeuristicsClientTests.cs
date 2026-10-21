using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>
/// The packet heuristics on the client path: real sessions over loopback sockets, every packet through <c>WorldSession.DispatchAsync</c> and,
/// for world opcodes, the world queue and <c>ProcessWorldPackets</c>. Each check is proven to fire (RED when its hook is removed) and the
/// switches (off by default, exempt staff) are proven to keep everything silent.
/// </summary>
public sealed class PacketHeuristicsClientTests
{
    private const string Subsystem = "packet";

    private static string Key(PacketCheck check) => Subsystem + "/" + PacketChecks.Code(check);

    [Fact]
    public async Task OffByDefault_ABurstOfBadPackets_RecordsNothingAndAllocatesNoState()
    {
        await using var host = WorldTestHost.Start();
        PacketHeuristicsFeature feature = PacketHeuristicsKit.FeatureOf(host);
        Assert.False(feature.Active);
        var ledger = new AnomalyLedger();
        feature.Ledger = ledger; // the options were never switched on: this is the shipped default
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHOFF", "Phoffone");
        await using (client)
        {
            using AnomalyCapture capture = ledger.Capture();
            for (int i = 0; i < 20; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgBootme, []);
                await client.SendAsync((WorldOpcode)826, [1, 2, 3]);
                await client.SendAsync(WorldOpcode.CmsgStandstatechange, [0, 0, 0, 0, 9]);
                await client.SendAsync(WorldOpcode.CmsgEmote, PacketHeuristicsKit.U32(0));
                await client.SendAsync(WorldOpcode.CmsgPlayerLogin, new byte[8]);
            }

            await PacketHeuristicsKit.SettleAsync(host, client, session);

            Assert.Empty(capture.Notices);
            Assert.Equal(0, ledger.Total);
            foreach (PacketCheck check in Enum.GetValues<PacketCheck>())
            {
                Assert.Equal(0, PacketHeuristicsKit.Total(feature, check));
            }

            Assert.Null(session.HeuristicsState); // no per-session state exists while the feature is off
        }
    }

    [Fact]
    public async Task WrongState_AWorldOpcodeAtTheCharacterScreen_IsReported_AndStillDropped()
    {
        await using var host = WorldTestHost.Start();
        (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
        byte[] key = await host.AddAccountAsync("PHSTATE");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("PHSTATE", key);
        await client.CreateCharacterAsync("Phstateone");
        using AnomalyCapture capture = ledger.Capture();

        await client.SendAsync(WorldOpcode.CmsgAttackswing, new byte[8]);
        await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.WrongState, PacketTraffic.Player) >= 1, "the wrong-state finding");

        AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.WrongState));
        Assert.Contains("CMSG_ATTACKSWING", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("CharacterSelect", notice.Detail, StringComparison.Ordinal);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.StartsWith("account ", notice.Subject, StringComparison.Ordinal);
        Account account = (await host.Accounts.FindByUsernameAsync("PHSTATE"))!;
        Assert.Equal(SessionState.CharacterSelect, host.Registry.Find(account.Id)!.State); // dropped as before: the session is where it was
    }

    [Fact]
    public async Task WrongState_ASessionOpcodeInWorld_IsReported()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHSESS", "Phsessone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            await client.SendAsync(WorldOpcode.CmsgCharEnum, []); // allowed at the character screen only
            await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.WrongState, PacketTraffic.Player) >= 1, "the wrong-state finding");

            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.WrongState));
            Assert.Contains("CMSG_CHAR_ENUM", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            Assert.Contains("InWorld", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(SessionState.InWorld, session.State);
        }
    }

    [Fact]
    public async Task UnknownOpcode_IsInfoWhenNamed_AndWarningWhenOutsideTheTableOrAServerOpcode()
    {
        await using var host = WorldTestHost.Start();
        Assert.False(host.Opcodes.TryGet(WorldOpcode.CmsgBootme, out _));
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHUNK", "Phunkone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await client.SendAsync((WorldOpcode)826, [1, 2, 3]);
            await client.SendAsync(WorldOpcode.SmsgPong, [0, 0, 0, 0]);
            await PacketHeuristicsKit.SettleAsync(host, client, session);

            Assert.Equal(3, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player));
            IReadOnlyList<AnomalyNotice> notices = capture.Notices;
            Assert.Equal(AnomalySeverity.Info, Assert.Single(notices, n => n.Detail.StartsWith("CMSG_BOOTME", StringComparison.Ordinal)).Severity);
            AnomalyNotice outside = Assert.Single(notices, n => n.Detail.Contains("outside the opcode table", StringComparison.Ordinal));
            Assert.Equal(AnomalySeverity.Warning, outside.Severity);
            AnomalyNotice server = Assert.Single(notices, n => n.Detail.StartsWith("SMSG_PONG", StringComparison.Ordinal));
            Assert.Equal(AnomalySeverity.Warning, server.Severity);
            Assert.All(notices, n => Assert.Equal(Key(PacketCheck.UnknownOpcode), n.Key));
        }
    }

    [Fact]
    public async Task Malformed_APingWithoutItsSequence_IsReportedBeforeTheDisconnect()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHMALP", "Phmalpone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            await client.SendAsync(WorldOpcode.CmsgPing, [1, 2, 3]);

            Assert.True(await client.IsClosedByServerAsync()); // behaviour unchanged: a malformed ping disconnects
            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Malformed));
            Assert.Contains("CMSG_PING", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(AnomalySeverity.Error, notice.Severity);
            Assert.Equal(1, feature.Count(PacketCheck.Malformed, PacketTraffic.Player));
        }
    }

    [Fact]
    public async Task Malformed_AWorldHandlerThatReadsPastTheEnd_IsReportedBeforeTheKick()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHMALW", "Phmalwone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            await client.SendAsync(WorldOpcode.CmsgStandstatechange, [1, 0]); // the handler reads a u32

            Assert.True(await client.IsClosedByServerAsync());
            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Malformed));
            Assert.Contains("CMSG_STANDSTATECHANGE", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("2 bytes", notice.Detail, StringComparison.Ordinal);
            Assert.Equal("malformed packet", session.KickReason);
        }
    }

    [Fact]
    public async Task Shape_ALengthTheMessageNeverHas_IsReported_AndTheRightLengthIsNot()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHSHAPE", "Phshapeone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            await client.SendAsync(WorldOpcode.CmsgStandstatechange, [0, 0, 0, 0]); // exactly 4 bytes: fine
            await client.SendAsync(WorldOpcode.CmsgStandstatechange, [0, 0, 0, 0, 9]); // one byte too many
            await client.SendAsync(WorldOpcode.CmsgPing, [1, 0, 0, 0, 0]); // the message is 8 bytes; the server tolerates 4+
            await PacketHeuristicsKit.SettleAsync(host, client, session);

            Assert.Equal(2, feature.Count(PacketCheck.Shape, PacketTraffic.Player));
            Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Shape) && n.Detail.Contains("CMSG_STANDSTATECHANGE", StringComparison.Ordinal)
                && n.Detail.Contains("trailing 1 bytes", StringComparison.Ordinal));
            Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Shape) && n.Detail.Contains("CMSG_PING", StringComparison.Ordinal)
                && n.Detail.Contains("short by 3 bytes", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Cadence_MoreThanTheCeilingInASecond_IsReported_AtTheCeilingItIsNot()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient quiet, WorldSession quietSession) = await PacketHeuristicsKit.EnterClientAsync(host, "PHCADQ", "Phcadqone");
        (WorldTestClient loud, WorldSession loudSession) = await PacketHeuristicsKit.EnterClientAsync(host, "PHCADL", "Phcadlone");
        await using (quiet)
        await using (loud)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host, o => o.Replay = false);
            using AnomalyCapture capture = ledger.Capture();
            string quietSubject = $"account {quietSession.AccountId}";
            string loudSubject = $"account {loudSession.AccountId}";

            for (uint i = 1; i <= 5; i++)
            {
                await quiet.SendAsync(WorldOpcode.CmsgEmote, PacketHeuristicsKit.U32(i)); // five emotes: the ceiling
            }

            for (uint i = 1; i <= 8; i++)
            {
                await loud.SendAsync(WorldOpcode.CmsgEmote, PacketHeuristicsKit.U32(i));
            }

            await PacketHeuristicsKit.SettleAsync(host, quiet, quietSession);
            await PacketHeuristicsKit.SettleAsync(host, loud, loudSession);

            Assert.DoesNotContain(capture.Notices, n => n.Subject == quietSubject);
            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Subject == loudSubject && n.Key == Key(PacketCheck.Cadence));
            Assert.Contains("CMSG_EMOTE", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("Emote", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("> 5/s", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(3, feature.Count(PacketCheck.Cadence, PacketTraffic.Player)); // packets 6, 7 and 8 exceeded the ceiling
        }
    }

    [Fact]
    public async Task Cadence_APingTwiceInAHurry_IsOverspeed()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHPING", "Phpingone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            await client.SendAsync(WorldOpcode.CmsgPing, PacketHeuristicsKit.PingPayload(1));
            await client.ReadUntilAsync(WorldOpcode.SmsgPong);
            Assert.Equal(0, feature.Count(PacketCheck.Cadence, PacketTraffic.Player)); // the first ping is never early
            await client.SendAsync(WorldOpcode.CmsgPing, PacketHeuristicsKit.PingPayload(2));
            await client.ReadUntilAsync(WorldOpcode.SmsgPong); // still answered as before

            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Cadence));
            Assert.Contains("overspeed-ping", notice.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Replay_TheSamePayloadFiveTimesInASecond_IsReported_ButNotAnExcludedOpcode()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHREP", "Phrepone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            for (int i = 0; i < 5; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgEmote, PacketHeuristicsKit.U32(2)); // five identical emotes (the cadence ceiling is 5, not exceeded)
            }

            for (int i = 0; i < 20; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgSetSelection, new byte[8]); // a client re-selects the same unit all the time
            }

            await PacketHeuristicsKit.SettleAsync(host, client, session);

            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.Replay));
            Assert.Contains("CMSG_EMOTE", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("5 times", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(0, feature.Count(PacketCheck.Cadence, PacketTraffic.Player));
            Assert.DoesNotContain(capture.Notices, n => n.Detail.Contains("CMSG_SET_SELECTION", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task OrphanAck_ATeleportAckWithNoTeleportPending_IsReported()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHACK", "Phackone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();
            Player player = await host.PlayerAsync("Phackone");
            byte[] ack = new byte[16];
            BitConverter.GetBytes(player.Guid.Value).CopyTo(ack, 0);

            await client.SendAsync(WorldOpcode.MsgMoveTeleportAck, ack);
            await PacketHeuristicsKit.SettleAsync(host, client, session);

            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.OrphanAck));
            Assert.Contains("MSG_MOVE_TELEPORT_ACK", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("no near teleport is pending", notice.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task IncapacitatedAction_ACastWhileStunned_IsNotReported_BecauseTheServerOnlyRefusesSomeStunnedCasts()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHINC", "Phincone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();
            Player player = await host.PlayerAsync("Phincone");
            byte[] cast = [1, 0, 0, 0, 0, 0];

            await client.SendAsync(WorldOpcode.CmsgCastSpell, cast);
            await PacketHeuristicsKit.SettleAsync(host, client, session);
            Assert.Equal(0, feature.Count(PacketCheck.IncapacitatedAction, PacketTraffic.Player)); // able to act

            // Stunned (and in the logout countdown, which sets the same flag): CasterAuraGate refuses only an instant spell or one with the stun
            // interrupt flag, and not at all for a caster immune to the stun mechanic, so the packet alone proves nothing and is never reported.
            await host.OnWorldAsync(() => { player.UnitFlags |= UnitFlags.Stunned; });
            await client.SendAsync(WorldOpcode.CmsgCastSpell, cast);
            await PacketHeuristicsKit.SettleAsync(host, client, session);
            await host.OnWorldAsync(() => { player.UnitFlags &= ~UnitFlags.Stunned; });

            Assert.Equal(0, feature.Count(PacketCheck.IncapacitatedAction, PacketTraffic.Player));
            Assert.DoesNotContain(capture.Notices, n => n.Key == Key(PacketCheck.IncapacitatedAction));
        }
    }

    [Fact]
    public async Task IncapacitatedAction_OnATaxiFlight_IsReported()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHTAXI", "Phtaxione");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();
            Player player = await host.PlayerAsync("Phtaxione");
            await host.OnWorldAsync(() => { player.UnitFlags |= UnitFlags.TaxiFlight; });

            await client.SendAsync(WorldOpcode.CmsgGossipHello, new byte[8]);
            await PacketHeuristicsKit.SettleAsync(host, client, session);

            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.IncapacitatedAction));
            Assert.Contains("CMSG_GOSSIP_HELLO", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("taxi", notice.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Staff_AreExemptByDefault_AndCountedAsStaffWhenIncluded()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHSTAFF", "Phstaffone", AccountSecurity.GameMaster);
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await PacketHeuristicsKit.SettleAsync(host, client, session);
            Assert.Empty(capture.Notices);
            Assert.Equal(0, PacketHeuristicsKit.Total(feature, PacketCheck.UnknownOpcode));
            Assert.Null(session.HeuristicsState);

            feature.SetExemptStaff(false);
            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Staff) >= 1, "the staff finding");
            Assert.Equal(0, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player));
            Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.UnknownOpcode));
        }
    }

    [Fact]
    public async Task ARuntimeSwitch_TurnsTheChecksOnAndOffWithoutARestart()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHSWITCH", "Phswitchone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player) == 1, "one finding while on");

            feature.SetEnabled(false);
            Assert.False(feature.Active);
            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await PacketHeuristicsKit.SettleAsync(host, client, session);
            Assert.Equal(1, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player));

            feature.SetEnabled(true);
            Assert.True(feature.Active);
            Assert.Equal(1, ledger.CountOf(Key(PacketCheck.UnknownOpcode)));
        }
    }
}
