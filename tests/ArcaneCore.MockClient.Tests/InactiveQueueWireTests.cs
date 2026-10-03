using System.Net.Sockets;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Owned loopback, real SRP/session encryption and independent build5875 queue expectations.</summary>
public sealed class InactiveQueueWireTests
{
    private const ushort BattlefieldStatusRequest = 723;
    private const ushort MeetingStoneInfoRequest = 662;
    private const ushort MeetingStoneSetQueueReply = 661;
    private const int MaximumFramesPerStage = 128;

    // Independent opcode literals: battlefield list/win/lose/status/group join, followed
    // by meeting-stone setqueue/complete/in-progress/member-added/join-failed.
    private static readonly ushort[] QueueReplies = [573, 575, 576, 724, 744, 661, 663, 664, 665, 699];

    [Fact]
    public async Task BattlefieldStatus_EncryptedEmptyRequestStaysQuietAndKeepsWorldOperationsUsable()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await using QueueClient client = await LoginAsync(server, token);
        PlayerObservation before = await ObservePlayerAsync(server, client.Guid, token);
        CharacterQuestStatus journalBefore = await LoadJournalAsync(server, client.Guid, token);

        for (int iteration = 0; iteration < 3; iteration++)
        {
            await client.Client.SendAsync(BattlefieldStatusRequest, ReadOnlyMemory<byte>.Empty, token);
            await AssertQuietThroughMapStatusAsync(client.Connection, token);
            await AssertLiteralPingAsync(client.Connection, token);
        }

        AssertPlayerUnchanged(before, await ObservePlayerAsync(server, client.Guid, token));
        Assert.Equal(journalBefore, await LoadJournalAsync(server, client.Guid, token));
        Assert.Equal(1, server.World.OnlinePlayerCount);
    }

    [Fact]
    public async Task MeetingStoneInfo_EncryptedEmptyQueriesReturnExactInactiveLiteralWithoutMutatingPlayer()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await using QueueClient client = await LoginAsync(server, token);
        PlayerObservation before = await ObservePlayerAsync(server, client.Guid, token);
        CharacterQuestStatus journalBefore = await LoadJournalAsync(server, client.Guid, token);

        for (int iteration = 0; iteration < 3; iteration++)
        {
            await client.Client.SendAsync(MeetingStoneInfoRequest, ReadOnlyMemory<byte>.Empty, token);
            WorldFrame reply = await ReadInactiveReplyAsync(client.Connection, token);
            Assert.Equal((ushort)661, reply.Opcode);
            // Pinned vanilla SMSG_MEETINGSTONE_SETQUEUE: u32 area zero, u8 NONE five.
            // This literal is not produced by any server builder or shared response codec.
            Assert.Equal(Convert.FromHexString("0000000005"), reply.Payload);
            // The following real map response also rejects an extra queue notification.
            await AssertQuietThroughMapStatusAsync(client.Connection, token);
            await AssertLiteralPingAsync(client.Connection, token);
            AssertPlayerUnchanged(before, await ObservePlayerAsync(server, client.Guid, token));
        }

        Assert.Equal(journalBefore, await LoadJournalAsync(server, client.Guid, token));
        Assert.Equal(1, server.World.OnlinePlayerCount);
    }

    [Theory]
    [InlineData(723)]
    [InlineData(662)]
    public async Task QueueQuery_BeforeAuthenticationDisconnectsWithoutInactiveQueueReply(int opcode)
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await using WorldClient client = await WorldClient.ConnectAsync(server.WorldEndpoint, token);
        WorldFrame challenge = await client.ReadAsync(token);
        Assert.Equal((ushort)WorldOpcode.SmsgAuthChallenge, challenge.Opcode);
        Assert.Equal(4, challenge.Payload.Length);
        await client.SendAsync(checked((ushort)opcode), ReadOnlyMemory<byte>.Empty, token);
        await AssertDisconnectedWithoutQueueAsync(new ScenarioConnection(client), token);
        Assert.Equal(0, server.World.OnlinePlayerCount);
    }

    [Theory]
    [InlineData(723)]
    [InlineData(662)]
    public async Task QueueQuery_EncryptedSurplusByteFollowsStrictEmptyBodyDisconnectPolicy(int opcode)
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await using QueueClient client = await LoginAsync(server, token);
        // ArcaneCore requires exactly the documented empty request body. The vanilla
        // NullClientPacket reader may ignore surplus bytes; this is our validation policy.
        await client.Client.SendAsync(checked((ushort)opcode), new byte[] { 0 }, token);
        await AssertDisconnectedWithoutQueueAsync(client.Connection, token);
        Assert.Equal(0, await server.World.InvokeAsync(() => server.World.OnlinePlayerCount).WaitAsync(token));
    }

    private static async Task AssertQuietThroughMapStatusAsync(ScenarioConnection connection, CancellationToken token)
    {
        // Queue requests and this status request share the real map dispatch queue.
        // Ping is session-served and alone cannot prove an earlier map handler finished.
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token);
        for (int index = 0; index < MaximumFramesPerStage; index++)
        {
            WorldFrame frame = await connection.ReadAsync(token);
            Assert.DoesNotContain(frame.Opcode, QueueReplies);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgQuestgiverStatus)
            {
                MockQuestgiverStatus status = ScenarioWire.QuestgiverStatus(frame.Payload);
                Assert.Equal(SyntheticArcaneServer.NpcGuid, status.Guid);
                Assert.Equal(5u, status.Status);
                return;
            }
        }

        Assert.Fail("Inactive queue query did not retain its real map status response within 128 frames.");
    }

    private static async Task AssertLiteralPingAsync(ScenarioConnection connection, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgPing, Convert.FromHexString("0130726B11000000"), token);
        for (int index = 0; index < MaximumFramesPerStage; index++)
        {
            WorldFrame frame = await connection.ReadAsync(token);
            Assert.DoesNotContain(frame.Opcode, QueueReplies);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgPong)
            {
                Assert.Equal(Convert.FromHexString("0130726B"), frame.Payload);
                return;
            }
        }

        Assert.Fail("Inactive queue query did not retain its exact ping echo within 128 frames.");
    }

    private static async Task<WorldFrame> ReadInactiveReplyAsync(ScenarioConnection connection, CancellationToken token)
    {
        for (int index = 0; index < MaximumFramesPerStage; index++)
        {
            WorldFrame frame = await connection.ReadAsync(token);
            if (frame.Opcode == MeetingStoneSetQueueReply)
            {
                return frame;
            }

            Assert.DoesNotContain(frame.Opcode, QueueReplies);
        }

        throw new MockProtocolException("Expected literal meeting-stone inactive opcode661 within 128 frames.");
    }

    private static async Task AssertDisconnectedWithoutQueueAsync(ScenarioConnection connection, CancellationToken token)
    {
        for (int index = 0; index < MaximumFramesPerStage; index++)
        {
            try
            {
                Assert.DoesNotContain((await connection.ReadAsync(token)).Opcode, QueueReplies);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException exception) when (exception.InnerException is SocketException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }

        Assert.Fail("Rejected queue request did not disconnect its owned socket within 128 frames.");
    }

    private static async Task<PlayerObservation> ObservePlayerAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
        => await server.World.InvokeAsync(() =>
        {
            Player player = server.World.FindOnlinePlayer(new ObjectGuid(guid))
                ?? throw new InvalidOperationException("Inactive queue query removed its real player.");
            return new PlayerObservation(player, player.MapId, player.ZoneId, player.X, player.Y, player.Z, player.Orientation,
                player.Values.ToArray(), player.Inventory.CreateSnapshot().Items.ToArray());
        }).WaitAsync(token);

    private static void AssertPlayerUnchanged(PlayerObservation before, PlayerObservation after)
    {
        Assert.Same(before.Player, after.Player);
        Assert.Equal((before.Map, before.Zone, before.X, before.Y, before.Z, before.Orientation),
            (after.Map, after.Zone, after.X, after.Y, after.Z, after.Orientation));
        Assert.Equal(before.Fields, after.Fields);
        Assert.Equal(before.Inventory, after.Inventory);
    }

    private static async Task<CharacterQuestStatus> LoadJournalAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
    {
        await server.FlushCharacterAsync(checked((int)guid), token);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterQuestData quests = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>().LoadAsync(checked((int)guid), token);
        return Assert.Single(quests.Quests, quest => quest.Quest == SyntheticArcaneServer.JournalQuestId);
    }

    private static async Task<QueueClient> LoginAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        await server.AddAccountAsync("INACTIVEQUEUE", "PASSWORD", token);
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, "INACTIVEQUEUE", "PASSWORD", token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        try
        {
            Assert.Equal((byte)0x0C, await client.AuthenticateAsync("INACTIVEQUEUE", logon.SessionKey, token));
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync("Queuehero", token);
            ulong guid = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await MockScenarios.SeedJournalAsync(server, guid, token);
            await connection.LoginAsync(guid, token);
            return new QueueClient(client, connection, guid);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static CancellationTokenSource TestDeadline() => new(TimeSpan.FromSeconds(20));

    private sealed record PlayerObservation(Player Player, uint Map, uint Zone, float X, float Y, float Z, float Orientation,
        uint[] Fields, InventoryItemData[] Inventory);

    private sealed record QueueClient(WorldClient Client, ScenarioConnection Connection, ulong Guid) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    // Primary field layouts (only protocol facts, no upstream implementation copied):
    // gtker/wow_messages@70abb9deff0bb63440d8aeb4386b820653e8a176,
    // wow_message_parser/wowm/world/battleground/cmsg_battlefield_status.wowm (empty0x02D3),
    // wow_message_parser/wowm/world/meetingstone/cmsg_meetingstone_info.wowm (empty0x0296),
    // wow_message_parser/wowm/world/meetingstone/smsg_meetingstone_setqueue.wowm (area/u8statusNONE5).
}
