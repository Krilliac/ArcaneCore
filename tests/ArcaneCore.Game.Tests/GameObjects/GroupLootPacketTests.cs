using ArcaneCore.Game.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// The group loot packet layouts, as literal little-endian bytes derived by hand from vmangos
/// Server/Packets/Loot.cpp and Group/Group.cpp:957-972, and wow_messages world/loot/*.wowm.
/// The corpse is guid 0x0102030405060708, the roller/winner guid 9, item 0x11223344.
/// </summary>
public sealed class GroupLootPacketTests
{
    private static readonly ObjectGuid Corpse = new(0x0102030405060708UL);
    private static readonly ObjectGuid Roller = new(9);
    private const uint Item = 0x11223344;

    private static readonly byte[] CorpseBytes = [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01];
    private static readonly byte[] RollerBytes = [9, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] ItemBytes = [0x44, 0x33, 0x22, 0x11];
    private static readonly byte[] Zero4 = [0, 0, 0, 0];
    private static readonly byte[] Slot2 = [2, 0, 0, 0];

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    [Fact]
    public void StartRoll_Is28Bytes_WithTheCountdownLast()
    {
        byte[] expected = Cat(CorpseBytes, Slot2, ItemBytes, Zero4, Zero4, [0x60, 0xEA, 0, 0]); // 60000 ms
        Assert.Equal(expected, GroupLootPackets.StartRoll(Corpse, 2, Item, 60000));
        Assert.Equal(28, expected.Length);
    }

    [Theory]
    [InlineData(RollVote.Pass, 128, 128)]
    [InlineData(RollVote.Need, 0, 0)]
    [InlineData(RollVote.Greed, 128, 2)]
    public void VoteAnnouncement_UsesTheVmangosNumberAndTypePairs(RollVote vote, byte number, byte type)
    {
        byte[] expected = Cat(CorpseBytes, Slot2, RollerBytes, ItemBytes, Zero4, Zero4, [number, type]);
        Assert.Equal(34, expected.Length);
        Assert.Equal(expected, GroupLootPackets.VoteAnnouncement(Corpse, 2, Roller, Item, vote));
    }

    [Fact]
    public void ResolvedRoll_CarriesTheNumberAndTheNeedOrGreedVote()
    {
        Assert.Equal(Cat(CorpseBytes, Slot2, RollerBytes, ItemBytes, Zero4, Zero4, [57, 1]),
            GroupLootPackets.ResolvedRoll(Corpse, 2, Roller, Item, 57, RollVote.Need));
        Assert.Equal(Cat(CorpseBytes, Slot2, RollerBytes, ItemBytes, Zero4, Zero4, [100, 2]),
            GroupLootPackets.ResolvedRoll(Corpse, 2, Roller, Item, 100, RollVote.Greed));
    }

    [Fact]
    public void RollWon_PutsTheItemBeforeTheWinner()
    {
        byte[] expected = Cat(CorpseBytes, Slot2, ItemBytes, Zero4, Zero4, RollerBytes, [100, 1]);
        Assert.Equal(34, expected.Length);
        Assert.Equal(expected, GroupLootPackets.RollWon(Corpse, 2, Item, Roller, 100, RollVote.Need));
    }

    [Fact]
    public void AllPassed_Is24Bytes_AndTheOrderDiffersFromStartRoll()
    {
        byte[] expected = Cat(CorpseBytes, Slot2, ItemBytes, Zero4, Zero4);
        Assert.Equal(24, expected.Length);
        Assert.Equal(expected, GroupLootPackets.AllPassed(Corpse, 2, Item));
    }

    [Fact]
    public void MasterList_IsACountedGuidArray()
    {
        Assert.Equal([0], GroupLootPackets.MasterList([]));
        Assert.Equal(Cat([2], RollerBytes, CorpseBytes), GroupLootPackets.MasterList([Roller, Corpse]));
    }

    [Theory]
    [InlineData(LootError.DidntKill, 0)]
    [InlineData(LootError.TooFar, 4)]
    [InlineData(LootError.NotStanding, 8)]
    [InlineData(LootError.Stunned, 9)]
    [InlineData(LootError.PlayerNotFound, 10)]
    [InlineData(LootError.MasterInventoryFull, 12)]
    [InlineData(LootError.MasterUniqueItem, 13)]
    [InlineData(LootError.MasterOther, 14)]
    public void LootErrorResponse_Is10Bytes_TypeZeroThenTheCode(LootError error, byte code)
    {
        byte[] expected = Cat(CorpseBytes, [0, code]);
        Assert.Equal(10, expected.Length);
        Assert.Equal(expected, GroupLootPackets.LootErrorResponse(Corpse, error));
    }

    [Fact]
    public void ErrorCodes_MatchLootMgrH()
    {
        Assert.Equal([0, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16], Enum.GetValues<LootError>().Select(e => (int)e).Order());
        Assert.Equal([0, 1, 2], Enum.GetValues<RollVote>().Select(v => (int)v).Order());
    }

    [Theory]
    [InlineData(0, RollVote.Pass)]
    [InlineData(1, RollVote.Need)]
    [InlineData(2, RollVote.Greed)]
    public void ParseLootRoll_AcceptsThe13BytePayload(byte raw, RollVote expected)
    {
        byte[] payload = Cat(CorpseBytes, Slot2, [raw]);
        Assert.True(GroupLootPackets.TryParseLootRoll(payload, out ObjectGuid corpse, out uint slot, out RollVote vote));
        Assert.Equal((Corpse, 2u, expected), (corpse, slot, vote));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(255)]
    public void ParseLootRoll_RejectsVotesTheClientCannotSend(byte raw)
        => Assert.False(GroupLootPackets.TryParseLootRoll(Cat(CorpseBytes, Slot2, [raw]), out _, out _, out _));

    [Fact]
    public void ParseLootRoll_RejectsShortPayloads()
    {
        Assert.False(GroupLootPackets.TryParseLootRoll([], out _, out _, out _));
        Assert.False(GroupLootPackets.TryParseLootRoll(Cat(CorpseBytes, Slot2), out _, out _, out _)); // 12 bytes
    }

    [Fact]
    public void ParseMasterGive_Needs17Bytes()
    {
        byte[] payload = Cat(CorpseBytes, [7], RollerBytes);
        Assert.True(GroupLootPackets.TryParseMasterGive(payload, out ObjectGuid loot, out byte slot, out ObjectGuid target));
        Assert.Equal((Corpse, (byte)7, Roller), (loot, slot, target));
        Assert.False(GroupLootPackets.TryParseMasterGive(payload[..16], out _, out _, out _));
    }
}
