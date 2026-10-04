using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>The quest sharing opcodes of the world host (vmangos QuestHandler.cpp:332-381, 403-474).</summary>
public sealed class QuestShareWorldTests
{
    [Fact]
    public void TheThreeShareOpcodesAreRegisteredAsInWorldHandlers()
    {
        var table = new OpcodeTable();
        new QuestShareHandlers().Register(table);
        foreach (WorldOpcode opcode in new[] { WorldOpcode.CmsgPushquesttoparty, WorldOpcode.CmsgQuestConfirmAccept, WorldOpcode.MsgQuestPushResult })
        {
            Assert.True(table.TryGet(opcode, out OpcodeHandler handler), opcode.ToString());
            Assert.NotNull(handler.World);
            Assert.Equal(SessionStates.InWorld, handler.AllowedStates);
        }
    }

    [Fact]
    public void TheHostsOpcodeTable_ContainsThem_SoNoOtherGroupRegistersTheSameOpcode()
    {
        OpcodeTable table = WorldServiceCollectionExtensions.BuildOpcodeTable();
        Assert.True(table.TryGet(WorldOpcode.CmsgPushquesttoparty, out _));
        Assert.True(table.TryGet(WorldOpcode.CmsgQuestConfirmAccept, out _));
        Assert.True(table.TryGet(WorldOpcode.MsgQuestPushResult, out _));
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgPushquesttoparty, 0)]
    [InlineData(WorldOpcode.CmsgPushquesttoparty, 3)]
    [InlineData(WorldOpcode.CmsgPushquesttoparty, 5)]
    [InlineData(WorldOpcode.CmsgQuestConfirmAccept, 0)]
    [InlineData(WorldOpcode.CmsgQuestConfirmAccept, 8)]
    [InlineData(WorldOpcode.MsgQuestPushResult, 8)]
    [InlineData(WorldOpcode.MsgQuestPushResult, 10)]
    public async Task MalformedShareOpcode_Disconnects(WorldOpcode opcode, int length)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("BADSHARE", "Badshare");
        await client.CollectAsync();
        await client.SendAsync(opcode, new byte[length]);
        Assert.True(await client.IsClosedByServerAsync());
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgPushquesttoparty, 4)]
    [InlineData(WorldOpcode.CmsgQuestConfirmAccept, 4)]
    [InlineData(WorldOpcode.MsgQuestPushResult, 9)]
    public async Task WellFormedShareOpcodeOfAPlayerWithoutAGroupOrOffer_IsIgnored(WorldOpcode opcode, int length)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("OKSHARE", "Okshare");
        await client.CollectAsync();
        await client.SendAsync(opcode, new byte[length]);
        // The connection stays up and answers the next request.
        await client.SendAsync(WorldOpcode.CmsgPing, [1, 0, 0, 0, 0, 0, 0, 0]);
        await client.ReadUntilAsync(WorldOpcode.SmsgPong);
    }
}
