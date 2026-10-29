using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The role quotas of a dungeon or raid run (<see cref="PlayerbotGroupContent.RunRoleNeeds"/>) and the matcher that fills them
/// (<see cref="PlayerbotGroupContent.Match(IReadOnlyList{PlayerbotGroupCandidate}, int, PlayerbotGroupOptions, ValueTuple{int, int}, bool)"/>).
/// </summary>
public sealed class PlayerbotRunMatchingTests
{
    [Theory]
    [InlineData(2, 0, 0)]    // a pair needs no tank or healer
    [InlineData(5, 1, 1)]    // a party: the configured minimums
    [InlineData(10, 1, 3)]   // a raid: one tank per ten, a quarter healers, rounded up
    [InlineData(20, 2, 5)]
    [InlineData(40, 4, 10)]
    public void TheQuotas_AtTheDefaults(int size, int tanks, int healers)
        => Assert.Equal((tanks, healers), PlayerbotGroupContent.RunRoleNeeds(size, raid: size > 5, new PlayerbotGroupOptions()));

    [Fact]
    public void TheRaidQuotas_FollowTheLiveOptions()
    {
        var options = new PlayerbotGroupOptions();
        options.DungeonRuns.Raids.TanksPerTen = 2;
        options.DungeonRuns.Raids.HealerPercent = 30;

        Assert.Equal((2, 3), PlayerbotGroupContent.RunRoleNeeds(10, raid: true, options));
        Assert.Equal((4, 6), PlayerbotGroupContent.RunRoleNeeds(20, raid: true, options));
        Assert.Equal((8, 12), PlayerbotGroupContent.RunRoleNeeds(40, raid: true, options));

        // A dungeon party ignores the raid options and reads the party minimums.
        options.MinTank = 2;
        options.MinHealer = 2;
        Assert.Equal((2, 2), PlayerbotGroupContent.RunRoleNeeds(5, raid: false, options));
        Assert.Equal((0, 0), PlayerbotGroupContent.RunRoleNeeds(2, raid: false, options));
    }

    [Fact]
    public void ARaidOfTen_TakesOneTankAndThreeHealers_AndSpreadsThemOverTwoSubgroups()
    {
        var options = new PlayerbotGroupOptions();
        PlayerbotGroupCandidate[] bots = Raid(warriors: 1, priests: 3, mages: 6);

        PlayerbotGroupPlan plan = PlayerbotGroupContent.Match(bots, 10, options, PlayerbotGroupContent.RunRoleNeeds(10, raid: true, options))!;

        Assert.Equal(10, plan.Members.Count);
        Assert.Equal(1, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Tank));
        Assert.Equal(3, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Healer));
        Assert.Equal(6, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Damage));
        Assert.All(plan.Members.Where(m => m.Role == PlayerbotGroupRole.Tank), m => Assert.Equal(Class.Warrior, m.Bot.Class));
        Assert.All(plan.Members.Where(m => m.Role == PlayerbotGroupRole.Healer), m => Assert.Equal(Class.Priest, m.Bot.Class));
        Assert.Equal(plan.Leader.BotId, plan.Members.Single(m => m.Role == PlayerbotGroupRole.Tank).Bot.BotId); // the tank leads
        Assert.Equal([0, 1], plan.Members.Select(m => (int)m.SubGroup).Distinct().Order());
        Assert.All(plan.Members.GroupBy(m => m.SubGroup), g => Assert.Equal(5, g.Count()));
    }

    [Fact]
    public void ARaidOfForty_TakesFourTanksAndTenHealers_AcrossEightSubgroups()
    {
        var options = new PlayerbotGroupOptions();
        PlayerbotGroupCandidate[] bots = Raid(warriors: 4, priests: 10, mages: 26);

        PlayerbotGroupPlan plan = PlayerbotGroupContent.Match(bots, 40, options, PlayerbotGroupContent.RunRoleNeeds(40, raid: true, options))!;

        Assert.Equal(40, plan.Members.Count);
        Assert.Equal(4, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Tank));
        Assert.Equal(10, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Healer));
        Assert.Equal(8, plan.Members.Select(m => m.SubGroup).Distinct().Count());
        Assert.All(plan.Members.GroupBy(m => m.SubGroup), g => Assert.Equal(5, g.Count()));
    }

    [Fact]
    public void ARaidOfTwenty_TakesTwoTanksAndFiveHealers_AcrossFourSubgroups_AndNeedsAllOfThem()
    {
        var options = new PlayerbotGroupOptions();
        (int, int) needs = PlayerbotGroupContent.RunRoleNeeds(20, raid: true, options);

        PlayerbotGroupPlan plan = PlayerbotGroupContent.Match(Raid(warriors: 2, priests: 5, mages: 13), 20, options, needs)!;

        Assert.Equal(20, plan.Members.Count);
        Assert.Equal(2, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Tank));
        Assert.Equal(5, plan.Members.Count(m => m.Role == PlayerbotGroupRole.Healer));
        Assert.Equal(4, plan.Members.Select(m => m.SubGroup).Distinct().Count());
        Assert.All(plan.Members.GroupBy(m => m.SubGroup), g => Assert.Equal(5, g.Count()));
        Assert.Null(PlayerbotGroupContent.Match(Raid(warriors: 1, priests: 5, mages: 14), 20, options, needs));  // one tank of two
        Assert.Null(PlayerbotGroupContent.Match(Raid(warriors: 2, priests: 4, mages: 14), 20, options, needs));  // four healers of five
    }

    [Fact]
    public void ARaid_ShortOfItsHealers_OrItsTanks_OrItsBots_IsNotFormed()
    {
        var options = new PlayerbotGroupOptions();
        (int, int) needs = PlayerbotGroupContent.RunRoleNeeds(10, raid: true, options);

        Assert.Null(PlayerbotGroupContent.Match(Raid(warriors: 1, priests: 2, mages: 7), 10, options, needs));   // two healers of three
        Assert.Null(PlayerbotGroupContent.Match(Raid(warriors: 0, priests: 3, mages: 7), 10, options, needs));   // no class that tanks
        Assert.Null(PlayerbotGroupContent.Match(Raid(warriors: 1, priests: 3, mages: 5), 10, options, needs));   // nine bots
        Assert.NotNull(PlayerbotGroupContent.Match(Raid(warriors: 1, priests: 3, mages: 6), 10, options, needs));
    }

    [Fact]
    public void TheMembers_MustStayWithinTheLevelRange_OfOneAnother()
    {
        var options = new PlayerbotGroupOptions { LevelRange = 2 };
        (int, int) needs = PlayerbotGroupContent.RunRoleNeeds(5, raid: false, options);
        PlayerbotGroupCandidate[] spread =
        [
            Bot("Warrior", Class.Warrior, 20, 1), Bot("Priest", Class.Priest, 21, 2), Bot("Mage1", Class.Mage, 22, 3),
            Bot("Mage2", Class.Mage, 30, 4), Bot("Mage3", Class.Mage, 31, 5),
        ];

        Assert.Null(PlayerbotGroupContent.Match(spread, 5, options, needs));

        PlayerbotGroupCandidate[] close = [.. spread.Take(3), Bot("Mage2", Class.Mage, 21, 4), Bot("Mage3", Class.Mage, 20, 5)];
        PlayerbotGroupPlan plan = PlayerbotGroupContent.Match(close, 5, options, needs)!;
        Assert.Equal(5, plan.Members.Count);
        Assert.Single(plan.Members, m => m.Role == PlayerbotGroupRole.Tank);
        Assert.Single(plan.Members, m => m.Role == PlayerbotGroupRole.Healer);
    }

    [Fact]
    public void QuotasThatCannotFit_TheGroup_AreRefused()
    {
        var options = new PlayerbotGroupOptions();

        Assert.Null(PlayerbotGroupContent.Match(Raid(warriors: 3, priests: 3, mages: 4), 10, options, (6, 6)));
    }

    private static PlayerbotGroupCandidate[] Raid(int warriors, int priests, int mages)
    {
        var bots = new List<PlayerbotGroupCandidate>();
        uint since = 1;
        for (int i = 0; i < warriors; i++) bots.Add(Bot($"Warrior{i}", Class.Warrior, 60, since++, PlayerbotRole.Tank));
        for (int i = 0; i < priests; i++) bots.Add(Bot($"Priest{i}", Class.Priest, 60, since++, PlayerbotRole.Healer));
        for (int i = 0; i < mages; i++) bots.Add(Bot($"Mage{i}", Class.Mage, 60, since++, PlayerbotRole.RangeDps));
        return [.. bots];
    }

    private static PlayerbotGroupCandidate Bot(string name, Class @class, byte level, uint since, PlayerbotRole role = PlayerbotRole.MeleeDps)
        => new(Guid.NewGuid(), (ulong)(uint)name.GetHashCode(StringComparison.Ordinal), name, @class, level, 1, 0, Vector3.Zero, role, 0, since);
}
