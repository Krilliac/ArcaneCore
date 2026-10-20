using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>False-positive and cost fixes found in review: transfer grace, GM refresh, double Enter World, subject prefix, throttled allocation, toggles.</summary>
public sealed class PacketHeuristicsFollowUpTests
{
    private static string Key(PacketCheck check) => "packet/" + PacketChecks.Code(check);

    [Fact]
    public async Task AFarTeleport_WithAPacketInFlightBehindIt_ReportsNothing()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHFAR", "Phfarone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();
            Player player = await host.PlayerAsync("Phfarone");

            // A real far transfer: SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD go out, the player leaves its map, and a packet the client sent
            // before it saw them (a selection change) reaches a player without a map.
            await host.OnWorldAsync(() =>
            {
                TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
                Assert.True(teleports.TeleportTo(player, player.MapId, player.X, player.Y, player.Z, player.Orientation, TeleportOptions.ForceFar));
            });
            await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
            await client.SendAsync(WorldOpcode.CmsgSetSelection, new byte[8]);
            await PacketHeuristicsKit.SettleAsync(host, client, session);
            Assert.Equal(0, feature.Count(PacketCheck.WrongState, PacketTraffic.Player));
            Assert.DoesNotContain(capture.Notices, n => n.Key == Key(PacketCheck.WrongState));

            await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
            await host.WaitForWorldAsync(() => player.Map is not null, "back on a map");
        }
    }

    [Fact]
    public async Task AMaplessPlayerOutsideAnyTransfer_Reports_EvenWithTheDefaultGrace()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHNOMAP", "Phnomapone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();
            Player player = await host.PlayerAsync("Phnomapone");
            await host.OnWorldAsync(() =>
            {
                Map source = player.Map!;
                source.RemovePlayer(player); // in transit, but no transfer packet was sent
                source.BeginTransit(player);
            });

            await client.SendAsync(WorldOpcode.CmsgSetSelection, new byte[8]);
            await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.WrongState, PacketTraffic.Player) >= 1, "the map-less finding");
            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.WrongState));
            Assert.Contains("player.Map is null", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(SessionState.InWorld, session.State);
        }
    }

    [Fact]
    public async Task ADoubleClickedEnterWorld_IsInfo_ButAnotherSessionOpcodeInWorldStaysAWarning()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, _) = await PacketHeuristicsKit.EnterClientAsync(host, "PHDBL", "Phdblone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            await client.SendAsync(WorldOpcode.CmsgPlayerLogin, new byte[8]);
            await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.WrongState, PacketTraffic.Player) >= 1, "the second login");
            AnomalyNotice login = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.WrongState) && n.Detail.Contains("CMSG_PLAYER_LOGIN", StringComparison.Ordinal));
            Assert.Equal(AnomalySeverity.Info, login.Severity);

            await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
            await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.WrongState, PacketTraffic.Player) >= 2, "the char enum");
            Assert.Equal(AnomalySeverity.Warning, Assert.Single(capture.Notices, n => n.Detail.Contains("CMSG_CHAR_ENUM", StringComparison.Ordinal)).Severity);
        }
    }

    [Fact]
    public async Task TheGmClassification_FollowsTheGmFlagBothWays_EvenWhileStaffAreExempt()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHGM", "Phgmone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, _) = PacketHeuristicsKit.Enable(host);
            Player player = await host.PlayerAsync("Phgmone");

            await host.OnWorldAsync(() => player.Flags |= PlayerFlags.Gm);
            await client.SendAsync(WorldOpcode.CmsgSetSelection, new byte[8]); // a world packet refreshes the flag
            await PacketHeuristicsKit.SettleAsync(host, client, session);
            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await PacketHeuristicsKit.SettleAsync(host, client, session);
            Assert.Equal(0, PacketHeuristicsKit.Total(feature, PacketCheck.UnknownOpcode)); // GM mode: exempt staff

            await host.OnWorldAsync(() => player.Flags &= ~PlayerFlags.Gm);
            await client.SendAsync(WorldOpcode.CmsgSetSelection, new byte[8]);
            await PacketHeuristicsKit.SettleAsync(host, client, session);
            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player) >= 1, "checked as a player again");
        }
    }

    [Fact]
    public async Task ABotSubject_NeverStartsWithTheTelemetryPrefix()
    {
        await using var host = WorldTestHost.Start();
        (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host, o => o.ExemptBots = false);
        using AnomalyCapture capture = ledger.Capture();
        WorldSession bot = await PacketHeuristicsKit.NewBotAtCharacterScreenAsync(host, "PHSUBJ");

        feature.Unhandled(bot, WorldOpcode.CmsgBootme, 0);

        AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.UnknownOpcode));
        Assert.StartsWith("botname ", notice.Subject, StringComparison.Ordinal);
        Assert.False(notice.Subject!.StartsWith("bot ", StringComparison.Ordinal), "PlayerbotPolicyTelemetry counts every subject that starts with 'bot '");
    }

    [Fact]
    public async Task AThrottledFinding_AllocatesNothing_OnEveryReportingPath()
    {
        await using var host = WorldTestHost.Start();
        long now = 1_000_000;
        (PacketHeuristicsFeature feature, _) = PacketHeuristicsKit.Enable(host, o => o.ExemptBots = false, () => now);
        WorldSession bot = await PacketHeuristicsKit.NewBotAtCharacterScreenAsync(host, "PHALLOC");

        // The first of each is reported (and may allocate); everything after it inside the interval is only counted.
        feature.WrongState(bot, WorldOpcode.CmsgCastSpell, "bot action while the session is not in the world", SessionState.CharacterSelect);
        feature.Unhandled(bot, (WorldOpcode)5000, 3);
        feature.Unhandled(bot, WorldOpcode.SmsgPong, 3);
        feature.BadHeader(bot, default, 99, 0x1234);
        feature.Malformed(bot, WorldOpcode.CmsgPing, 2, "constant reason");
        feature.OrphanAck(bot, WorldOpcode.MsgMoveTeleportAck, "constant reason");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            feature.WrongState(bot, WorldOpcode.CmsgCastSpell, "bot action while the session is not in the world", SessionState.CharacterSelect);
            feature.Unhandled(bot, (WorldOpcode)5000, 3);
            feature.Unhandled(bot, WorldOpcode.SmsgPong, 3);
            feature.BadHeader(bot, default, 99, 0x1234);
            feature.Malformed(bot, WorldOpcode.CmsgPing, 2, "constant reason");
            feature.OrphanAck(bot, WorldOpcode.MsgMoveTeleportAck, "constant reason");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1001, feature.Count(PacketCheck.WrongState, PacketTraffic.Bot));
    }

    [Fact]
    public void TheOpcodeNameKinds_ComeFromATable_AndMatchTheNames()
    {
        Assert.Equal(OpcodeNameKind.Unnamed, PacketOpcodeClasses.NameKindOf((WorldOpcode)5000));
        Assert.Equal(OpcodeNameKind.Server, PacketOpcodeClasses.NameKindOf(WorldOpcode.SmsgPong));
        Assert.Equal(OpcodeNameKind.Client, PacketOpcodeClasses.NameKindOf(WorldOpcode.CmsgPing));
        Assert.Equal(OpcodeNameKind.Client, PacketOpcodeClasses.NameKindOf(WorldOpcode.MsgMoveHeartbeat));
    }

    [Fact]
    public async Task ConcurrentToggles_NeverLoseOneAnother()
    {
        await using var host = WorldTestHost.Start();
        PacketHeuristicsFeature feature = PacketHeuristicsKit.FeatureOf(host);
        for (int round = 0; round < 300; round++)
        {
            Assert.True(feature.TryApply(new PacketHeuristicsOptions(), out _));
            using var start = new ManualResetEventSlim(false);
            Task a = Task.Run(() => { start.Wait(); feature.SetExemptBots(false); });
            Task b = Task.Run(() => { start.Wait(); feature.SetExemptStaff(false); });
            Task c = Task.Run(() => { start.Wait(); feature.SetEnabled(true); });
            start.Set();
            await Task.WhenAll(a, b, c);
            PacketHeuristicsOptions current = feature.Options;
            Assert.True(!current.ExemptBots && !current.ExemptStaff && current.Enabled, $"round {round}: a toggle was lost ({current.ExemptBots}, {current.ExemptStaff}, {current.Enabled})");
        }
    }

    [Fact]
    public async Task IncapacitatedAction_WhileBeingTeleported_IsReported()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsKit.EnterClientAsync(host, "PHTELE", "Phteleone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();
            Player player = await host.PlayerAsync("Phteleone");
            await host.OnWorldAsync(() =>
            {
                TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
                Assert.True(teleports.TeleportTo(player, player.MapId, player.X + 5, player.Y, player.Z, player.Orientation)); // near: pending until the ack
            });

            await client.SendAsync(WorldOpcode.CmsgCastSpell, [1, 0, 0, 0, 0, 0]);
            await PacketHeuristicsKit.SettleAsync(host, client, session);

            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key(PacketCheck.IncapacitatedAction));
            Assert.Contains("being teleported", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(1, feature.Count(PacketCheck.IncapacitatedAction, PacketTraffic.Player));
        }
    }
}
