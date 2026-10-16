using ArcaneCore.Game;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Commands;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>The pure rules <c>.bot party fill</c> and <c>.bot raid</c> share (<see cref="PlayerbotGroupFill"/>), and the raid reply channel.</summary>
public sealed class PlayerbotGroupFillTests
{
    [Theory]
    [InlineData(false, 1, 4)] // no group or a party of one: four bots
    [InlineData(false, 3, 2)]
    [InlineData(false, 5, 0)] // a full party
    [InlineData(true, 5, 5)]  // a raid of five takes one more subgroup's worth
    [InlineData(true, 9, 5)]
    [InlineData(true, 38, 2)] // up to forty
    [InlineData(true, 40, 0)]
    public void PartyFillCount_FillsAPartyToFive_AndARaidBySubgroupUpToForty(bool raid, int members, int expected)
        => Assert.Equal(expected, PlayerbotGroupFill.PartyFillCount(raid, members));

    [Theory]
    [InlineData(10, 1, 10, false)]
    [InlineData(39, 1, 39, false)]
    [InlineData(50, 1, 39, true)]  // the caller counts as one
    [InlineData(50, 0, 39, true)]  // not grouped yet: still the caller
    [InlineData(3, 38, 2, true)]
    [InlineData(5, 40, 0, true)]   // a full raid
    public void RaidFillCount_ClampsToTheFreeRaidSlots(int requested, int members, int count, bool clamped)
        => Assert.Equal((count, clamped), PlayerbotGroupFill.RaidFillCount(requested, members));

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(4, 1, 1)]
    [InlineData(5, 1, 1)]
    [InlineData(10, 1, 3)]
    [InlineData(20, 2, 5)]
    [InlineData(39, 4, 10)]
    public void RoleTargets_ScaleWithTheCount_AsTheRaidRunMakeUp(int count, int tanks, int healers)
        => Assert.Equal((tanks, healers), PlayerbotGroupFill.RoleTargets(count, new PlayerbotGroupOptions()));

    [Fact]
    public void RoleTargets_FollowTheConfiguredRaidRunMakeUp()
    {
        var options = new PlayerbotGroupOptions();
        options.DungeonRuns.Raids.TanksPerTen = 2;
        options.DungeonRuns.Raids.HealerPercent = 50;
        Assert.Equal((4, 10), PlayerbotGroupFill.RoleTargets(20, options));
    }

    [Fact]
    public void Select_TakesTanksThenHealers_ThenTheNearestLevels()
    {
        PlayerbotFillCandidate[] candidates =
        [
            Bot("Mage1", Class.Mage, 0), Bot("Mage2", Class.Mage, 0), Bot("Rogue1", Class.Rogue, 1), Bot("Hunter1", Class.Hunter, 1),
            Bot("Pala1", Class.Paladin, 0), Bot("Warr1", Class.Warrior, 3), Bot("Priest1", Class.Priest, 2), Bot("Shaman1", Class.Shaman, 4),
            Bot("Druid1", Class.Druid, 0), Bot("Lock1", Class.Warlock, 2),
        ];

        List<PlayerbotFillCandidate> picked = PlayerbotGroupFill.Select(candidates, 6, tanks: 1, healers: 2);

        // A warrior is the preferred tank, priests and shamans the preferred healers; then the nearest levels by name.
        Assert.Equal(["Warr1", "Priest1", "Shaman1", "Druid1", "Mage1", "Mage2"], picked.Select(c => c.Name));
    }

    [Fact]
    public void Select_FallsBackToHybrids_WhenNoDedicatedTankOrHealerIsThere()
    {
        PlayerbotFillCandidate[] candidates = [Bot("Mage1", Class.Mage, 0), Bot("Pala1", Class.Paladin, 1), Bot("Druid1", Class.Druid, 2)];
        Assert.Equal(["Pala1", "Druid1", "Mage1"], PlayerbotGroupFill.Select(candidates, 3, 1, 1).Select(c => c.Name));
    }

    [Fact]
    public void Select_PrefersBotsNearTheCallersLevel_WhileThereAreEnough()
    {
        PlayerbotFillCandidate[] candidates =
            [Bot("FarWarr", Class.Warrior, 20), Bot("Mage1", Class.Mage, 1), Bot("Rogue1", Class.Rogue, 2), Bot("Far2", Class.Mage, 30)];

        Assert.Equal(["Mage1", "Rogue1"], PlayerbotGroupFill.Select(candidates, 2, 1, 1).Select(c => c.Name));
        // Not enough near ones: everyone is a candidate, the far warrior tanks.
        Assert.Equal(["FarWarr", "Mage1", "Rogue1"], PlayerbotGroupFill.Select(candidates, 3, 1, 1).Select(c => c.Name));
    }

    [Fact]
    public void Select_NeverPicksMoreThanAsked_OrMoreThanThereAre()
    {
        PlayerbotFillCandidate[] candidates = [Bot("A", Class.Warrior, 0), Bot("B", Class.Priest, 0)];
        Assert.Empty(PlayerbotGroupFill.Select(candidates, 0, 1, 1));
        Assert.Single(PlayerbotGroupFill.Select(candidates, 1, 1, 1));
        Assert.Equal(2, PlayerbotGroupFill.Select(candidates, 39, 4, 10).Count);
    }

    [Fact]
    public void Select_ForAFullRaid_KeepsTheRoleMix()
    {
        Class[] classes = [Class.Warrior, Class.Priest, Class.Mage, Class.Rogue, Class.Hunter, Class.Warlock, Class.Shaman, Class.Druid];
        PlayerbotFillCandidate[] candidates =
            [.. Enumerable.Range(0, 80).Select(i => Bot("Bot" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), classes[i % classes.Length], i % 4))];
        (int tanks, int healers) = PlayerbotGroupFill.RoleTargets(39, new PlayerbotGroupOptions());

        List<PlayerbotFillCandidate> picked = PlayerbotGroupFill.Select(candidates, 39, tanks, healers);

        Assert.Equal(39, picked.Count);
        Assert.Equal(39, picked.Select(c => c.BotId).Distinct().Count());
        Assert.True(picked.Count(c => c.Class == Class.Warrior) >= 4);
        Assert.True(picked.Count(c => c.Class is Class.Priest or Class.Shaman) >= 10);
    }

    [Fact]
    public void Names_ListsTenThenCountsTheRest()
    {
        string[] names = [.. Enumerable.Range(1, 12).Select(i => "B" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal("B1, B2, B3, B4, B5, B6, B7, B8, B9, B10 and 2 more", PlayerbotGroupFill.Names(names));
        Assert.Equal("B1, B2", PlayerbotGroupFill.Names(["B1", "B2"]));
    }

    [Fact]
    public void GroupChannel_IsRaidChatInARaid_SoEverySubgroupHearsTheBot()
    {
        Assert.Equal(ChatType.Raid, PlayerbotChatCommands.GroupChannel(raid: true));
        Assert.Equal(ChatType.Party, PlayerbotChatCommands.GroupChannel(raid: false));
    }

    private static PlayerbotFillCandidate Bot(string name, Class @class, int levelDiff)
        => new(Guid.NewGuid(), name, @class, levelDiff);
}
