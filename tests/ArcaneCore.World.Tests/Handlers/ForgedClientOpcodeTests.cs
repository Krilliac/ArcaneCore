using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.PacketHeuristics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// The 12 client opcodes a real 1.12.1 client has no send code for (<see cref="ForgedClientOpcodes"/>, static client check): each is registered
/// as a tripwire, and a packet that arrives is dropped unread, never answered, counted, and flagged as a Warning <c>packet/dev-opcode</c> finding.
/// Real session over a loopback socket, every packet through <c>WorldSession.DispatchAsync</c>. Written without a build or test run (lane rule); unproven until the integration gate.
/// </summary>
public sealed class ForgedClientOpcodeTests
{
    private static readonly WorldOpcode[] Expected =
    [
        WorldOpcode.CmsgAutoequipGroundItem, WorldOpcode.CmsgDropItem, WorldOpcode.CmsgNewSpellSlot, WorldOpcode.MsgUpdateGroupMembers,
        WorldOpcode.MsgUpdateGuild, WorldOpcode.MsgDeleteGuildCharter, WorldOpcode.CmsgGmUpdateTicketStatus, WorldOpcode.CmsgBotDetected2,
        WorldOpcode.CmsgLuaUsage, WorldOpcode.MsgMoveSetRawPositionAck, WorldOpcode.MsgDelayGhostTeleport, WorldOpcode.MsgSetDungeonDifficulty,
    ];

    private static string Key => "packet/" + PacketChecks.Code(PacketCheck.DevOpcode);

    private static byte[] Junk => [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    [Fact]
    public async Task TheTripwireList_IsTheTwelveOpcodes_EachRegisteredAsAStrippedHandler_AndApartFromTheDevInventory()
    {
        await using var host = WorldTestHost.Start();
        Assert.Equal(Expected.Order(), ForgedClientOpcodes.All.Order());
        Assert.Equal(12, ForgedClientOpcodes.All.Count);
        Assert.Empty(ForgedClientOpcodes.All.Intersect(StrippedDevOpcodes.All));
        foreach (WorldOpcode opcode in Expected)
        {
            Assert.True(host.Opcodes.TryGet(opcode, out OpcodeHandler handler), WorldOpcodeNames.GetName(opcode));
            Assert.True(handler.Stripped, WorldOpcodeNames.GetName(opcode));
            Assert.Null(handler.World);
            Assert.Null(handler.Session);
        }
    }

    [Fact]
    public async Task EveryTripwire_FromAPlayerInWorld_IsDroppedFlaggedAsAWarning_AndNeverAnswered()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(dev); c.AddSingleton(unhandled); });
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "Z30FORGE", "Forgetripone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();
            Player player = await host.PlayerAsync("Forgetripone");
            (uint money, uint level) = await host.OnWorldAsync(() => (player.Money, player.Level));
            await client.CollectAsync();

            foreach (WorldOpcode opcode in Expected)
            {
                await client.SendAsync(opcode, Junk);
            }

            await client.SendAsync(WorldOpcode.CmsgPing, PacketHeuristicsTestKit.PingPayload(1));
            List<(WorldOpcode Opcode, byte[] Payload)> received = await client.CollectFromAsync(WorldOpcode.SmsgPong, TimeSpan.FromMilliseconds(250));
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal([WorldOpcode.SmsgPong], received.Select(r => r.Opcode));
            Assert.Equal(Expected.Length, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Player));
            IReadOnlyList<AnomalyNotice> notices = [.. capture.Notices.Where(n => n.Key == Key)];
            Assert.Equal(Expected.Length, notices.Count);
            Assert.All(notices, n => Assert.Equal(AnomalySeverity.Warning, n.Severity));
            Assert.Equal(Expected.Length, dev.Total);
            Assert.Equal(Expected.Select(WorldOpcodeNames.GetName).Order(), dev.Snapshot().Select(c => c.Opcode).Order());
            Assert.Empty(unhandled.Snapshot());
            Assert.Equal(SessionState.InWorld, session.State);
            Assert.Equal((money, level), await host.OnWorldAsync(() => (player.Money, player.Level)));
        }
    }
}
