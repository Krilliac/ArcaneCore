using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// Independent vanilla wire assertions from gtker/wow_messages' 1.12
/// smsg_quest_query_response.wowm and vmangos QuestQueryResponse::AppendBodyTo.
/// </summary>
public sealed class QuestQueryPacketTests
{
    [Fact]
    public void EmptyTemplate_MatchesVanillaGoldenBody()
    {
        Quest quest = Load(new QuestTemplate { Entry = 1, Method = 2 });
        byte[] body = QuestPackets.QueryResponse(quest, 1).ToArray();

        // 15 header words, 20 fixed reward words, four point words, eight empty CStrings,
        // and 16 objective words total 228 bytes. No counts/display ids or later-expansion fields.
        byte[] expected = Convert.FromHexString("0100000002000000" + new string('0', 440));
        Assert.Equal(expected, body);
    }

    [Fact]
    public void PopulatedTemplate_PreservesVanillaOrder_FixedArraysAndSignedObjectives()
    {
        var template = new QuestTemplate
        {
            Entry = 10,
            Method = 2,
            QuestLevel = -1,
            ZoneOrSort = -24,
            Type = 3,
            RepObjectiveFaction = 76,
            RepObjectiveValue = -4000,
            RequiredMaxRepFaction = 999, // Taking requirements are not the opposite reputation objective.
            RequiredMaxRepValue = 888,
            NextQuestInChain = 11,
            RewOrReqMoney = 9,
            RewMoneyMaxLevel = 24,
            RewSpell = 13,
            SrcItemId = 14,
            QuestFlags = (uint)QuestFlags.Sharable,
            RewItemId1 = 101,
            RewItemCount1 = 2,
            RewItemId4 = 104,
            RewItemCount4 = 5,
            RewChoiceItemId2 = 202,
            RewChoiceItemCount2 = 6,
            RewChoiceItemId6 = 206,
            RewChoiceItemCount6 = 7,
            PointMapId = 1,
            PointX = 1.25f,
            PointY = -2.5f,
            PointOpt = 15,
            Title = "Café",
            Objectives = "Objectives",
            Details = "Details",
            EndText = "End",
            ReqCreatureOrGOId1 = -77,
            ReqCreatureOrGOCount1 = 8,
            ReqItemId1 = 12,
            ReqItemCount1 = 2,
            ReqCreatureOrGOId4 = 88,
            ReqCreatureOrGOCount4 = 9,
            ReqItemId4 = 16,
            ReqItemCount4 = 3,
            ReqSpellCast1 = 999, // Spell objectives are represented through objective text, not another wire field.
            ObjectiveText1 = "Activate",
            ObjectiveText4 = "Defeat",
        };
        Quest quest = Load(template, new QuestTemplate { Entry = 11, Method = 2 });
        var reader = new PacketReader(QuestPackets.QueryResponse(quest, 2).AsSpan());
        Assert.Equal(10u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(-24, reader.ReadInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(76u, reader.ReadUInt32());
        Assert.Equal(-4000, reader.ReadInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(11u, reader.ReadUInt32());
        Assert.Equal(18, reader.ReadInt32());
        Assert.Equal(24u, reader.ReadUInt32()); // Raw max-level money is not scaled in the query.
        Assert.Equal(13u, reader.ReadUInt32());
        Assert.Equal(14u, reader.ReadUInt32());
        Assert.Equal((uint)QuestFlags.Sharable, reader.ReadUInt32());

        uint[] rewards = [101, 2, 0, 0, 0, 0, 104, 5, 0, 0, 202, 6, 0, 0, 0, 0, 0, 0, 206, 7];
        foreach (uint value in rewards)
        {
            Assert.Equal(value, reader.ReadUInt32());
        }

        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(1.25f, reader.ReadSingle());
        Assert.Equal(-2.5f, reader.ReadSingle());
        Assert.Equal(15u, reader.ReadUInt32());
        Assert.Equal("Café", reader.ReadCString());
        Assert.Equal("Objectives", reader.ReadCString());
        Assert.Equal("Details", reader.ReadCString());
        Assert.Equal("End", reader.ReadCString());
        uint[] objectives = [0x8000004D, 8, 12, 2, 0, 0, 0, 0, 0, 0, 0, 0, 88, 9, 16, 3];
        foreach (uint value in objectives)
        {
            Assert.Equal(value, reader.ReadUInt32());
        }

        Assert.Equal("Activate", reader.ReadCString());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Equal("Defeat", reader.ReadCString());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void HiddenRewards_ZeroOnlyRewardMoneyAndItems_WithFixedBodySize()
    {
        Quest quest = Load(new QuestTemplate
        {
            Entry = 5,
            Method = 2,
            QuestFlags = (uint)QuestFlags.HiddenRewards,
            RewOrReqMoney = 17,
            RewMoneyMaxLevel = 19,
            RewSpell = 20,
            SrcItemId = 21,
            RewItemId1 = 22,
            RewItemCount1 = 23,
            RewChoiceItemId6 = 24,
            RewChoiceItemCount6 = 25,
        });
        byte[] body = QuestPackets.QueryResponse(quest, 3).ToArray();
        Assert.Equal(228, body.Length);
        var reader = new PacketReader(body);
        reader.Skip(40);
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(19u, reader.ReadUInt32());
        Assert.Equal(20u, reader.ReadUInt32());
        Assert.Equal(21u, reader.ReadUInt32());
        Assert.Equal((uint)QuestFlags.HiddenRewards, reader.ReadUInt32());
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(0u, reader.ReadUInt32());
        }

        Assert.Equal(88, reader.Remaining);
    }

    [Theory]
    [InlineData(12, 2f, 24)]
    [InlineData(-12, 2f, -12)]
    [InlineData(0, 5f, 0)]
    [InlineData(1, 0.5f, 0)]
    public void MoneyField_ScalesRewardsAndPreservesRequirements(int money, float rate, int expected)
    {
        Quest quest = Load(new QuestTemplate { Entry = 1, Method = 2, RewOrReqMoney = money });
        var reader = new PacketReader(QuestPackets.QueryResponse(quest, rate).AsSpan());
        reader.Skip(40);
        Assert.Equal(expected, reader.ReadInt32());
    }

    [Fact]
    public void MissingNextQuest_UsesValidatedZeroChainTarget()
    {
        Quest quest = Load(new QuestTemplate { Entry = 1, Method = 2, NextQuestInChain = 999 });
        var reader = new PacketReader(QuestPackets.QueryResponse(quest, 1).AsSpan());
        reader.Skip(36);
        Assert.Equal(0u, reader.ReadUInt32());
    }

    private static Quest Load(QuestTemplate template, params QuestTemplate[] additional)
        => new QuestStore(new QuestContent([template, .. additional], [], [])).Get(template.Entry)!;
}
