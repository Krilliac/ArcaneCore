using System.Buffers.Binary;
using System.Net.Sockets;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Literal build5875 bodies assembled independently of the production packet builders.</summary>
public sealed class QuestGreetingWireTests
{
    [Theory]
    [InlineData(379, 7)]
    [InlineData(379, 9)]
    [InlineData(388, 7)]
    [InlineData(388, 9)]
    public async Task RealGreeting_MalformedGuidBodyDisconnectsWithoutOpeningQuestUi(int opcode, int length)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await using GreetingClient client = await LoginAsync(server, deadline.Token);
        await client.Connection.SendAsync((WorldOpcode)opcode, new byte[length], deadline.Token);
        await AssertDisconnectedAsync(client.Connection, deadline.Token);
        Assert.Equal(0, await server.World.InvokeAsync(() => server.World.OnlinePlayerCount).WaitAsync(deadline.Token));
    }

    [Theory]
    [InlineData(379, false)]
    [InlineData(379, true)]
    [InlineData(388, false)]
    [InlineData(388, true)]
    public async Task RealGreeting_UnknownCreatureOrPlayerGuidDoesNotOpenQuestUi(int opcode, bool playerGuid)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await using GreetingClient client = await LoginAsync(server, deadline.Token);
        ulong source = playerGuid ? client.Guid : SyntheticArcaneServer.NpcGuid + 1;
        await client.Connection.SendAsync((WorldOpcode)opcode, ScenarioWire.Guid(source), deadline.Token);
        // Both requests use the actual map dispatch queue. Its status response proves
        // the earlier invalid greeting handler has run; a session ping would not.
        await client.Connection.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), deadline.Token);
        for (int index = 0; index < 128; index++)
        {
            WorldFrame frame = await client.Connection.ReadAsync(deadline.Token);
            AssertNoQuestUi(frame);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgQuestgiverStatus)
            {
                MockQuestgiverStatus status = ScenarioWire.QuestgiverStatus(frame.Payload);
                Assert.Equal(SyntheticArcaneServer.NpcGuid, status.Guid);
                Assert.Equal(5u, status.Status);
                Assert.Equal(1, server.World.OnlinePlayerCount);
                return;
            }
        }

        Assert.Fail("Invalid greeting map barrier did not answer within 128 frames.");
    }

    [Theory]
    [InlineData(379)]
    [InlineData(388)]
    public async Task RealGreeting_NeutralCreatureWithoutQuestFlagReturnsEmptyClassicGossip(int opcode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await using GreetingClient client = await LoginAsync(server, deadline.Token);
        await client.Connection.SendAsync((WorldOpcode)opcode, ScenarioWire.Guid(SyntheticArcaneServer.FirstTargetGuid), deadline.Token);
        MockGossipMessage gossip = ScenarioWire.GossipMessage(await client.Connection.ReadUntilAsync(WorldOpcode.SmsgGossipMessage, deadline.Token));
        Assert.Equal(SyntheticArcaneServer.FirstTargetGuid, gossip.Guid);
        Assert.Equal(0xFFFFFFu, gossip.TextId);
        Assert.Empty(gossip.Options);
        Assert.Empty(gossip.Quests);
    }

    [Fact]
    public void QuestList_DecodesFullGuidGreetingEmotesAndEachQuest()
    {
        MockQuestList list = ScenarioWire.QuestList(QuestListVector());
        Assert.Equal(0x0102030405060708ul, list.Guid);
        Assert.Equal("Hello", list.Greeting);
        Assert.Equal(1000u, list.EmoteDelay);
        Assert.Equal(5u, list.Emote);
        Assert.Equal(new[] { new MockQuestMenuEntry(900002, 5, 1, "A"), new MockQuestMenuEntry(900003, 4, -1, "B") }, list.Quests);
    }

    [Fact]
    public void GossipMessage_DecodesClassicOptionsWithoutLaterExpansionFields()
    {
        MockGossipMessage gossip = ScenarioWire.GossipMessage(GossipVector());
        Assert.Equal(0x0102030405060708ul, gossip.Guid);
        Assert.Equal(0xFFFFFFu, gossip.TextId);
        Assert.Equal(new MockGossipOption(7, 2, true, "Ask"), Assert.Single(gossip.Options));
        Assert.Equal(new MockQuestMenuEntry(900003, 3, 1, "Active"), Assert.Single(gossip.Quests));
    }

    [Fact]
    public void QuestRequestItems_DecodesRequiredItemAndAllCompletionFlags()
    {
        MockQuestRequestItems request = ScenarioWire.QuestRequestItems(RequestItemsVector());
        Assert.Equal(0x0102030405060708ul, request.Guid);
        Assert.Equal(900003u, request.QuestId);
        Assert.Equal("Quest", request.Title);
        Assert.Equal("Bring", request.Text);
        Assert.Equal(500u, request.EmoteDelay);
        Assert.Equal(6u, request.Emote);
        Assert.Equal(1u, request.CloseOnCancel);
        Assert.Equal(100u, request.RequiredMoney);
        Assert.Equal(new MockQuestReward(900040, 2, 13), Assert.Single(request.RequiredItems));
        Assert.Equal(new uint[] { 2, 0, 4, 8 }, request.CompleteFlags);
    }

    [Fact]
    public void GreetingBodies_RejectEveryTruncationAndTrailingByte()
    {
        AssertStrict(QuestListVector(), body => ScenarioWire.QuestList(body));
        AssertStrict(GossipVector(), body => ScenarioWire.GossipMessage(body));
        AssertStrict(RequestItemsVector(), body => ScenarioWire.QuestRequestItems(body));
    }

    [Fact]
    public void QuestList_RejectsUnboundedCountAndGreetingText()
    {
        byte[] count = QuestListVector();
        count[22] = 33;
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestList(count));
        byte[] oversized = [.. Convert.FromHexString("0807060504030201"), .. Enumerable.Repeat((byte)'A', 8193), 0];
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestList(oversized));
    }

    [Fact]
    public void GossipMessage_RejectsUnboundedCountsAndInvalidBoolean()
    {
        byte[] options = GossipVector();
        BinaryPrimitives.WriteUInt32LittleEndian(options.AsSpan(12), 33);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.GossipMessage(options));
        byte[] quests = GossipVector();
        BinaryPrimitives.WriteUInt32LittleEndian(quests.AsSpan(26), 33);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.GossipMessage(quests));
        byte[] coded = GossipVector();
        coded[21] = 2;
        Assert.Throws<MockProtocolException>(() => ScenarioWire.GossipMessage(coded));
    }

    [Fact]
    public void GossipMessage_AcceptsSixteenIndependentLiteralOptions()
    {
        byte[] body = Convert.FromHexString("0807060504030201FFFFFF0010000000" + FirstSixteenOptions + "00000000");
        MockGossipMessage gossip = ScenarioWire.GossipMessage(body);
        Assert.Equal(16, gossip.Options.Length);
        Assert.Equal(Enumerable.Range(0, 16).Select(index => (uint)index), gossip.Options.Select(option => option.Id));
        Assert.All(gossip.Options, option => Assert.Equal(new MockGossipOption(option.Id, 0, false, ""), option));
        Assert.Empty(gossip.Quests);
    }

    [Fact]
    public void GossipMessage_AcceptsThirtyTwoIndependentLiteralOptions()
    {
        byte[] body = Convert.FromHexString("0807060504030201FFFFFF0020000000" + FirstSixteenOptions + SecondSixteenOptions + "00000000");
        MockGossipMessage gossip = ScenarioWire.GossipMessage(body);
        Assert.Equal(32, gossip.Options.Length);
        Assert.Equal(Enumerable.Range(0, 32).Select(index => (uint)index), gossip.Options.Select(option => option.Id));
        Assert.All(gossip.Options, option => Assert.Equal(new MockGossipOption(option.Id, 0, false, ""), option));
        Assert.Empty(gossip.Quests);
    }

    [Fact]
    public void GreetingBodies_RejectOversizedMenuTextAndRequiredItemCount()
    {
        byte[] title = [.. Convert.FromHexString("080706050403020100000000000000000001A2BB0D000500000001000000"),
            .. Enumerable.Repeat((byte)'A', 513), 0];
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestList(title));
        byte[] option = [.. Convert.FromHexString("0807060504030201FFFFFF0001000000070000000200"),
            .. Enumerable.Repeat((byte)'A', 2049), 0, 0, 0, 0, 0];
        Assert.Throws<MockProtocolException>(() => ScenarioWire.GossipMessage(option));
        byte[] items = RequestItemsVector();
        BinaryPrimitives.WriteUInt32LittleEndian(items.AsSpan(40), 5);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestRequestItems(items));
    }

    [Fact]
    public void EmptyQuestAndGossipMenus_AreStrictlyDecoded()
    {
        MockQuestList list = ScenarioWire.QuestList(Convert.FromHexString("080706050403020100000000000000000000"));
        Assert.Empty(list.Quests);
        MockGossipMessage gossip = ScenarioWire.GossipMessage(Convert.FromHexString("0807060504030201FFFFFF000000000000000000"));
        Assert.Empty(gossip.Options);
        Assert.Empty(gossip.Quests);
    }

    private static void AssertStrict(byte[] vector, Func<byte[], object> decode)
    {
        for (int length = 0; length < vector.Length; length++)
        {
            Assert.Throws<MockProtocolException>(() => decode(vector[..length]));
        }

        Assert.Throws<MockProtocolException>(() => decode([.. vector, 0]));
    }

    private static void AssertNoQuestUi(WorldFrame frame)
    {
        Assert.DoesNotContain(frame.Opcode, new ushort[]
        {
            (ushort)WorldOpcode.SmsgGossipMessage, (ushort)WorldOpcode.SmsgGossipComplete,
            (ushort)WorldOpcode.SmsgQuestgiverQuestList, (ushort)WorldOpcode.SmsgQuestgiverQuestDetails,
            (ushort)WorldOpcode.SmsgQuestgiverRequestItems, (ushort)WorldOpcode.SmsgQuestgiverOfferReward,
            (ushort)WorldOpcode.SmsgQuestgiverQuestComplete, (ushort)WorldOpcode.SmsgItemPushResult,
        });
    }

    private static async Task AssertDisconnectedAsync(ScenarioConnection connection, CancellationToken token)
    {
        for (int index = 0; index < 128; index++)
        {
            try
            {
                AssertNoQuestUi(await connection.ReadAsync(token));
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

        Assert.Fail("Malformed greeting did not disconnect within 128 frames.");
    }

    private static async Task<GreetingClient> LoginAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        await server.AddAccountAsync("GREETING", "PASSWORD", token);
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, "GREETING", "PASSWORD", token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        try
        {
            Assert.Equal((byte)0x0C, await client.AuthenticateAsync("GREETING", logon.SessionKey, token));
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync("Greeter", token);
            ulong guid = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await connection.LoginAsync(guid, token);
            return new GreetingClient(client, connection, guid);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private sealed record GreetingClient(WorldClient Client, ScenarioConnection Connection, ulong Guid) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    // Field-layout references (synthetic bytes, no source/captures copied):
    // vmangos/core@4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Server/Packets/Quest.cpp
    // and src/game/GossipDef.cpp (gossip quest maximum is hexadecimal0x20=32);
    // src/game/GossipDef.h defines the option maximum32. These indexed empty-text
    // literal options independently encode u32id/u8icon/u8coded/CString.
    private const string FirstSixteenOptions =
        "00000000000000" + "01000000000000" + "02000000000000" + "03000000000000" +
        "04000000000000" + "05000000000000" + "06000000000000" + "07000000000000" +
        "08000000000000" + "09000000000000" + "0A000000000000" + "0B000000000000" +
        "0C000000000000" + "0D000000000000" + "0E000000000000" + "0F000000000000";

    private const string SecondSixteenOptions =
        "10000000000000" + "11000000000000" + "12000000000000" + "13000000000000" +
        "14000000000000" + "15000000000000" + "16000000000000" + "17000000000000" +
        "18000000000000" + "19000000000000" + "1A000000000000" + "1B000000000000" +
        "1C000000000000" + "1D000000000000" + "1E000000000000" + "1F000000000000";

    // gtker/wow_messages@70abb9deff0bb63440d8aeb4386b820653e8a176/wow_message_parser/wowm/world/
    // quest/smsg_questgiver_quest_list.wowm and gossip/smsg_gossip_message.wowm.
    private static byte[] QuestListVector() => Convert.FromHexString(
        "0807060504030201" + "48656C6C6F00" + "E80300000500000002" +
        "A2BB0D0005000000010000004100" + "A3BB0D0004000000FFFFFFFF4200");

    private static byte[] GossipVector() => Convert.FromHexString(
        "0807060504030201FFFFFF0001000000" + "07000000020141736B00" +
        "01000000A3BB0D00030000000100000041637469766500");

    private static byte[] RequestItemsVector() => Convert.FromHexString(
        "0807060504030201A3BB0D005175657374004272696E6700" +
        "F401000006000000010000006400000001000000" +
        "C8BB0D00020000000D00000002000000000000000400000008000000");
}
