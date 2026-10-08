using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>The pure rules of bot groups (<see cref="PlayerbotGroupContent"/>) and the risk estimate's group size.</summary>
public sealed class PlayerbotGroupContentTests
{
    [Theory]
    [InlineData(0u, (byte)0, 0)]   // an ordinary quest: alone
    [InlineData(0u, (byte)2, 2)]   // suggested for two
    [InlineData(1u, (byte)0, 3)]   // elite (QuestInfo 1, "Group"): three when the row suggests none
    [InlineData(1u, (byte)5, 5)]
    [InlineData(1u, (byte)8, 5)]   // an elite quest is a party's: a raid's kills credit raid quests only
    [InlineData(41u, (byte)0, 0)]  // PvP (QuestInfo 41): not group content for the bots
    [InlineData(81u, (byte)0, 5)]  // dungeon: five
    [InlineData(81u, (byte)3, 3)]
    [InlineData(62u, (byte)0, 10)] // raid: ten when the row suggests no raid
    [InlineData(62u, (byte)5, 10)]
    [InlineData(62u, (byte)6, 6)]
    [InlineData(62u, (byte)40, 40)]
    public void TheQuestFlags_GiveTheGroupSize(uint type, byte suggested, int size)
        => Assert.Equal(size, PlayerbotGroupContent.QuestGroupSize(new QuestTemplate { Entry = 1, Type = type, SuggestedPlayers = suggested }));

    [Fact]
    public void AnInstance_AsksForItsPlayerLimit()
    {
        Assert.Equal(5, PlayerbotGroupContent.InstanceGroupSize(new MapTemplate(36, 0, MapType.Instance, 0, 10, 0, 0, 0, 0, "dungeon", "")));
        Assert.Equal(5, PlayerbotGroupContent.InstanceGroupSize(new MapTemplate(36, 0, MapType.Instance, 0, 0, 0, 0, 0, 0, "dungeon", "")));
        Assert.Equal(40, PlayerbotGroupContent.InstanceGroupSize(new MapTemplate(409, 0, MapType.Raid, 0, 40, 0, 0, 0, 0, "raid", "")));
        Assert.Equal(10, PlayerbotGroupContent.InstanceGroupSize(new MapTemplate(409, 0, MapType.Raid, 0, 0, 0, 0, 0, 0, "raid", "")));
    }

    [Fact]
    public void RoleComposition_PicksATankAndAHealer_AndTheTankLeads()
    {
        var options = new PlayerbotGroupOptions();
        PlayerbotGroupCandidate[] bots =
        [
            Bot("Mage", Class.Mage, 10, since: 1),
            Bot("Rogue", Class.Rogue, 11, since: 2),
            Bot("Priest", Class.Priest, 9, since: 3, role: PlayerbotRole.Healer),
            Bot("Warrior", Class.Warrior, 10, since: 4),
        ];

        PlayerbotGroupPlan plan = PlayerbotGroupContent.Match(bots, 3, options)!;

        Assert.Equal("Warrior", plan.Leader.Name);
        Assert.Contains(plan.Members, m => m.Bot.Name == "Warrior" && m.Role == PlayerbotGroupRole.Tank);
        Assert.Contains(plan.Members, m => m.Bot.Name == "Priest" && m.Role == PlayerbotGroupRole.Healer);
        Assert.Contains(plan.Members, m => m.Bot.Name == "Mage" && m.Role == PlayerbotGroupRole.Damage); // waited longest of the rest
        Assert.Equal(3, plan.Members.Count);
    }

    [Fact]
    public void WithoutAHealer_NoGroupOfThree_ButADuoNeedsNoRoles()
    {
        var options = new PlayerbotGroupOptions();
        PlayerbotGroupCandidate[] bots = [Bot("Mage", Class.Mage, 10, 1), Bot("Rogue", Class.Rogue, 10, 2), Bot("Warrior", Class.Warrior, 10, 3)];

        Assert.Null(PlayerbotGroupContent.Match(bots, 3, options));
        PlayerbotGroupPlan duo = PlayerbotGroupContent.Match(bots, 2, options)!;
        Assert.Equal(2, duo.Members.Count);
        Assert.NotNull(PlayerbotGroupContent.Match(bots, 3, new PlayerbotGroupOptions { MinHealer = 0 }));
    }

    [Fact]
    public void LevelsFartherApartThanTheRange_DoNotGroup()
    {
        var options = new PlayerbotGroupOptions { LevelRange = 5 };
        PlayerbotGroupCandidate[] bots = [Bot("Low", Class.Mage, 4, 1), Bot("High", Class.Rogue, 12, 2)];

        Assert.Null(PlayerbotGroupContent.Match(bots, 2, options));
        Assert.NotNull(PlayerbotGroupContent.Match(bots, 2, new PlayerbotGroupOptions { LevelRange = 8 }));
    }

    [Fact]
    public void WithoutATank_TheBotThatWaitedLongestLeads()
    {
        PlayerbotGroupPlan plan = PlayerbotGroupContent.Match([Bot("Late", Class.Mage, 10, 9), Bot("Early", Class.Rogue, 10, 1)], 2,
            new PlayerbotGroupOptions())!;

        Assert.Equal("Early", plan.Leader.Name);
    }

    [Fact]
    public void ARaid_SpreadsItsTanksAndHealersOverTheSubgroups()
    {
        var options = new PlayerbotGroupOptions();
        PlayerbotGroupCandidate[] bots =
        [
            Bot("Warrior", Class.Warrior, 20, 1), Bot("Paladin", Class.Paladin, 20, 2, role: PlayerbotRole.Tank),
            Bot("Priest", Class.Priest, 20, 3, role: PlayerbotRole.Healer), Bot("Shaman", Class.Shaman, 20, 4, role: PlayerbotRole.Healer),
            Bot("Mage", Class.Mage, 20, 5), Bot("Rogue", Class.Rogue, 20, 6), Bot("Hunter", Class.Hunter, 20, 7), Bot("Warlock", Class.Warlock, 20, 8),
            Bot("Mage2", Class.Mage, 20, 9), Bot("Rogue2", Class.Rogue, 20, 10), Bot("Hunter2", Class.Hunter, 20, 11),
        ];

        PlayerbotGroupPlan plan = PlayerbotGroupContent.Match(bots, 10, options)!;

        Assert.Equal(10, plan.Members.Count);
        (int tanks, int healers) = PlayerbotGroupContent.RoleNeeds(10, options);
        Assert.Equal((2, 2), (tanks, healers));
        Assert.Equal(2, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Tank));
        Assert.Equal(2, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Healer));
        // One tank and one healer in each subgroup of five.
        foreach (byte subgroup in new byte[] { 0, 1 })
        {
            Assert.Equal(5, plan.Members.Count(m => m.SubGroup == subgroup));
            Assert.Single(plan.Members, m => m.SubGroup == subgroup && m.Role == PlayerbotGroupRole.Tank);
            Assert.Single(plan.Members, m => m.SubGroup == subgroup && m.Role == PlayerbotGroupRole.Healer);
        }
    }

    [Fact]
    public void TheRiskEstimate_FindsTheSmallestGroupForAFightTooStrongAlone()
    {
        var options = new PlayerbotRiskOptions();
        // A level 10 bot (prior damage 20 per second, 200 health) against one creature of 1000 health hitting for 8 per second.
        PlayerbotEngagementFacts facts = new()
        {
            BotLevel = 10, BotHealth = 200, BotMaxHealth = 200, BotDps = 20,
            Enemies = [new RiskEnemy(1, 10, 0, 1000, 8, RiskJoin.Target)], QuestObjective = true,
        };

        Assert.Equal(PlayerbotEngageDecision.Avoid, PlayerbotRiskModel.Assess(facts, options).Decision); // risk 2.0 alone
        Assert.Equal(2, PlayerbotRiskModel.NeededGroupSize(facts, options, 5)); // 0.5 for two
        Assert.True(PlayerbotRiskModel.Risk(facts with { GroupSize = 2 }, 200, null, facts.Enemies) < PlayerbotRiskModel.Risk(facts, 200, null, facts.Enemies) / 3.9f);

        // Twice as strong: three are needed. A creature that kills outright: no group size at all. One the bot can take: none needed.
        PlayerbotEngagementFacts stronger = facts with { Enemies = [new RiskEnemy(1, 10, 0, 2000, 8, RiskJoin.Target)] };
        Assert.Equal(3, PlayerbotRiskModel.NeededGroupSize(stronger, options, 5));
        Assert.Equal(0, PlayerbotRiskModel.NeededGroupSize(facts with { Enemies = [new RiskEnemy(1, 10, 0, 1000, 8, RiskJoin.Target, Lethal: true)] }, options, 5));
        Assert.Equal(0, PlayerbotRiskModel.NeededGroupSize(facts with { Enemies = [new RiskEnemy(1, 10, 0, 100, 8, RiskJoin.Target)] }, options, 5));
        Assert.Equal(0, PlayerbotRiskModel.NeededGroupSize(stronger, options, 2)); // more than the largest group allowed
    }

    [Fact]
    public void AGroup_MayTakeAnEliteAFewLevelsHigherThanABotAlone()
    {
        var options = new PlayerbotRiskOptions();
        PlayerbotEngagementFacts facts = new()
        {
            BotLevel = 10, BotHealth = 200, BotMaxHealth = 200, BotDps = 20,
            Enemies = [new RiskEnemy(1, 14, 1, 300, 4, RiskJoin.Target)],
        };

        Assert.Equal("elite-above", PlayerbotRiskModel.Assess(facts, options).Reason);
        Assert.NotEqual("elite-above", PlayerbotRiskModel.Assess(facts with { GroupSize = 3 }, options).Reason);
    }

    private static PlayerbotGroupCandidate Bot(string name, Class @class, byte level, uint since, PlayerbotRole role = PlayerbotRole.MeleeDps)
        => new(Guid.NewGuid(), (ulong)name.GetHashCode(StringComparison.Ordinal) & 0xFFFF, name, @class, level, 1, 0, Vector3.Zero, role, 0, since);
}
