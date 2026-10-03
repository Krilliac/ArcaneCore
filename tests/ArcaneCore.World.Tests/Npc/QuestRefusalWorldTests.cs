using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>The refusal, cancel and swap opcodes of the quest interaction handlers, over the socket.</summary>
public sealed class QuestRefusalWorldTests
{
    [Fact]
    public void CancelAndSwapAreRegisteredAsInWorldHandlers()
    {
        var table = new OpcodeTable();
        new QuestNpcInteractionHandlers().Register(table);
        foreach (WorldOpcode opcode in new[] { WorldOpcode.CmsgQuestgiverCancel, WorldOpcode.CmsgQuestlogSwapQuest })
        {
            Assert.True(table.TryGet(opcode, out OpcodeHandler handler), opcode.ToString());
            Assert.NotNull(handler.World);
            Assert.Equal(SessionStates.InWorld, handler.AllowedStates);
        }
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgQuestgiverCancel, 1)]
    [InlineData(WorldOpcode.CmsgQuestlogSwapQuest, 0)]
    [InlineData(WorldOpcode.CmsgQuestlogSwapQuest, 1)]
    [InlineData(WorldOpcode.CmsgQuestlogSwapQuest, 3)]
    public async Task MalformedCancelOrSwap_Disconnects(WorldOpcode opcode, int length)
    {
        await using WorldTestHost host = Start(new QuestInteractionFixture());
        await using WorldTestClient client = await host.EnterWorldAsync("BADCANCEL", "Badcancel");
        await client.CollectAsync();
        await client.SendAsync(opcode, new byte[length]);
        Assert.True(await client.IsClosedByServerAsync());
    }

    [Fact]
    public async Task AcceptingTwice_AnswersAlreadyOn_AndCancelClosesTheWindow()
    {
        var fixture = new QuestInteractionFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await host.EnterWorldAsync("REFUSAL", "Refusal");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Refusal")!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "questgiver becomes visible");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);

        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
        byte[] invalid = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestInvalid);
        Assert.Equal((uint)ArcaneCore.Game.Quests.QuestInvalidReason.AlreadyOn, BinaryPrimitives.ReadUInt32LittleEndian(invalid));
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);

        await client.SendAsync(WorldOpcode.CmsgQuestgiverCancel, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);

        // Swapping the one used slot with an empty one moves the quest; out-of-range slots do nothing.
        await client.SendAsync(WorldOpcode.CmsgQuestlogSwapQuest, [0, 5]);
        await client.SendAsync(WorldOpcode.CmsgQuestlogSwapQuest, [0, 200]);
        await client.SendAsync(WorldOpcode.CmsgQuestgiverCancel, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        Assert.Equal(0u, await host.PlayerStateAsync("Refusal", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(QuestInteractionFixture.QuestId,
            await host.PlayerStateAsync("Refusal", p => p.GetUInt32(UpdateFields.PlayerQuestLog11 + (5 * ArcaneCore.Game.Quests.QuestConstants.FieldsPerSlot))));
    }

    private static WorldTestHost Start(QuestInteractionFixture fixture)
    {
        QuestInteractionTestServices.Current.Value = fixture;
        try { return WorldTestHost.Start(); }
        finally { QuestInteractionTestServices.Current.Value = null; }
    }

    private static byte[] QuestBody()
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(QuestInteractionFixture.Guid.Value);
        writer.WriteUInt32(QuestInteractionFixture.QuestId);
        return writer.ToArray();
    }
}
