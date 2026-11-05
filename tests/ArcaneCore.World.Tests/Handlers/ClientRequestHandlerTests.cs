using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.PacketHeuristics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// The seven client requests the 1.12.1 client has send code for and no reference server models (<see cref="ClientRequestHandlers"/>): each is
/// accepted in the world, decoded without throwing, counted, reported as an Info ledger entry, never answered, and never changes the character or the session.
/// Real session over a loopback socket. Written without a build or test run (lane rule); unproven until the integration gate.
/// </summary>
public sealed class ClientRequestHandlerTests
{
    private static readonly WorldOpcode[] All =
    [
        WorldOpcode.CmsgAutostoreGroundItem, WorldOpcode.CmsgStoreLootInSlot, WorldOpcode.CmsgSkillBuyStep, WorldOpcode.CmsgSkillBuyRank,
        WorldOpcode.CmsgSetLookingForGroup, WorldOpcode.CmsgChatFiltered, WorldOpcode.CmsgMinigameMove,
    ];

    private static byte[] LfgPayload()
    {
        var writer = new PacketWriter();
        writer.WriteUInt32(1);
        writer.WriteUInt32(2);
        writer.WriteUInt32(3);
        writer.WriteCString("note\nline");
        return writer.ToArray();
    }

    private static byte[] GuidPayload()
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(0x1122334455667788UL);
        writer.WriteByte(1);
        writer.WriteUInt32(9);
        return writer.ToArray();
    }

    [Fact]
    public async Task EveryRequest_IsRegisteredAsASessionHandlerForInWorldSessions_NotAStrippedOrMissingOne()
    {
        await using var host = WorldTestHost.Start();
        foreach (WorldOpcode opcode in All)
        {
            Assert.True(host.Opcodes.TryGet(opcode, out OpcodeHandler handler), WorldOpcodeNames.GetName(opcode));
            Assert.False(handler.Stripped, WorldOpcodeNames.GetName(opcode));
            Assert.NotNull(handler.Session);
            Assert.True(handler.AllowsState(SessionState.InWorld));
            Assert.False(handler.AllowsState(SessionState.CharacterSelect));
        }
    }

    [Fact]
    public async Task EveryRequest_FromAPlayerInWorld_IsDecodedCountedAndReported_WithoutAnAnswerOrAnEffect()
    {
        var tracker = new ClientRequestTracker();
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(tracker); c.AddSingleton(unhandled); });
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "Z30REQ", "Requestone");
        await using (client)
        {
            using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == AnomalyLedger.KeyOf("client", "unmodelled-request"));
            var player = await host.PlayerAsync("Requestone");
            (uint money, uint level) = await host.OnWorldAsync(() => (player.Money, player.Level));
            await client.CollectAsync();

            await client.SendAsync(WorldOpcode.CmsgAutostoreGroundItem, [1, 2, 3]);
            await client.SendAsync(WorldOpcode.CmsgStoreLootInSlot, []);
            await client.SendAsync(WorldOpcode.CmsgSkillBuyStep, []);
            await client.SendAsync(WorldOpcode.CmsgSkillBuyRank, [9]);
            await client.SendAsync(WorldOpcode.CmsgSetLookingForGroup, LfgPayload());
            await client.SendAsync(WorldOpcode.CmsgChatFiltered, GuidPayload());
            await client.SendAsync(WorldOpcode.CmsgMinigameMove, GuidPayload());
            await client.SendAsync(WorldOpcode.CmsgChatFiltered, [1, 2]); // a short packet: summarised, never a kick
            await client.SendAsync(WorldOpcode.CmsgSetLookingForGroup, [1, 2, 3]);
            await client.SendAsync(WorldOpcode.CmsgPing, PacketHeuristicsTestKit.PingPayload(1));
            List<(WorldOpcode Opcode, byte[] Payload)> received = await client.CollectFromAsync(WorldOpcode.SmsgPong, TimeSpan.FromMilliseconds(250));
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal([WorldOpcode.SmsgPong], received.Select(r => r.Opcode)); // no reply to any of them
            Assert.Equal(1, tracker.Count(WorldOpcode.CmsgAutostoreGroundItem));
            Assert.Equal("3 bytes", tracker.Last(WorldOpcode.CmsgAutostoreGroundItem));
            Assert.Equal("0 bytes", tracker.Last(WorldOpcode.CmsgStoreLootInSlot));
            Assert.Equal("0 bytes", tracker.Last(WorldOpcode.CmsgSkillBuyStep));
            Assert.Equal("1 bytes", tracker.Last(WorldOpcode.CmsgSkillBuyRank));
            Assert.Equal(2, tracker.Count(WorldOpcode.CmsgChatFiltered));
            Assert.Equal("2 bytes", tracker.Last(WorldOpcode.CmsgChatFiltered));
            Assert.Equal(1, tracker.Count(WorldOpcode.CmsgMinigameMove));
            Assert.Equal("13 bytes guid 0x1122334455667788", tracker.Last(WorldOpcode.CmsgMinigameMove));
            Assert.Equal(2, tracker.Count(WorldOpcode.CmsgSetLookingForGroup));
            Assert.Equal("3 bytes", tracker.Last(WorldOpcode.CmsgSetLookingForGroup)); // the short one came last

            Assert.Equal("22 bytes values 1/2/3 text 'note\nline'", ClientRequestHandlers.Summarise(WorldOpcode.CmsgSetLookingForGroup, LfgPayload()));
            Assert.True(capture.Notices.Count >= 9); // the ledger is process-wide: another test may add entries
            Assert.All(capture.Notices, n => Assert.Equal(AnomalySeverity.Info, n.Severity));
            Assert.Contains(capture.Notices, n => n.Subject == "CMSG_CHAT_FILTERED");
            Assert.Empty(unhandled.Snapshot()); // handled, so not a gap
            Assert.Equal(SessionState.InWorld, session.State);
            Assert.Equal((money, level), await host.OnWorldAsync(() => (player.Money, player.Level)));
        }
    }
}
