using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>The pure rules of recruiting (<see cref="PlayerbotRecruitment"/>) and the options that steer them.</summary>
public sealed class PlayerbotRecruitmentTests
{
    private static readonly PlayerbotCooperationOptions Defaults = new();

    [Fact]
    public void TheDefaults_AreTheDocumentedOnes_AndEveryOptionIsOn()
    {
        var groups = new PlayerbotGroupOptions();
        Assert.Equal(16, groups.MaxGroups);
        Assert.True(groups.InvitePlayers);
        Assert.Equal(20, groups.InvitePlayerAffinity);
        Assert.Equal(30, groups.InvitePlayerCooldownMinutes);
        PlayerbotCooperationOptions coop = groups.Cooperation;
        Assert.True(coop.Recruit && coop.AnswerBotCalls && coop.GuildCalls && coop.Mentoring && coop.RaidEscalation);
        Assert.Equal(20, coop.RecruitWaitSeconds);
        Assert.Equal(300, coop.RecruitRadiusYards);
        Assert.Equal(0.35f, coop.RecruitBaseChance);
        Assert.Equal(0.2f, coop.GuildBonus);
        Assert.Equal(0.15f, coop.LfgAnswerBonus);
        Assert.Equal(-40, coop.RefuseAffinity);
        Assert.Equal(40, coop.MaxRaidSize);
        Assert.Equal(PlayerbotInvitePolicy.Affinity, new PlayerbotPartyOptions().InvitePolicy);
        groups.Validate();
    }

    [Theory]
    [InlineData(0.5f, 10, true, false, false, 0.78f)]  // 0.35 + 0.2 + 0.03 + 0.2
    [InlineData(0.0f, 0, false, false, false, 0.35f)]
    [InlineData(0.5f, 0, false, true, false, 0.75f)]   // 0.35 + 0.2 + guild 0.2
    [InlineData(0.5f, 0, false, false, true, 0.70f)]   // 0.35 + 0.2 + answered 0.15
    [InlineData(1.0f, 100, true, true, true, 0.95f)]   // clamped
    public void TheChance_IsTheDocumentedSum_AndNeverAbove95Percent(float sociability, int affinity, bool role, bool guildmate, bool answered, float expected)
        => Assert.Equal(expected, PlayerbotRecruitment.Chance(Defaults, new PlayerbotRecruitFacts(sociability, affinity, affinity, false, role, guildmate, answered)), 0.0001f);

    [Fact]
    public void ARivalOrAnAffinityAtOrBelowTheRefusalLine_NeverJoins()
    {
        Assert.Equal(0f, PlayerbotRecruitment.Chance(Defaults, new PlayerbotRecruitFacts(1f, 90, 90, Rival: true, true, true, true)));
        Assert.Equal(0f, PlayerbotRecruitment.Chance(Defaults, new PlayerbotRecruitFacts(1f, 0, -40, false, true, true, true)));
        Assert.Equal(0f, PlayerbotRecruitment.Chance(Defaults, new PlayerbotRecruitFacts(1f, -40, 0, false, true, true, true)));
        Assert.True(PlayerbotRecruitment.Chance(Defaults, new PlayerbotRecruitFacts(1f, -39, -39, false, true, true, true)) > 0f);
        for (ulong guid = 1; guid < 200; guid++)
            Assert.False(PlayerbotRecruitment.Accepts(Defaults, guid, 1, new PlayerbotRecruitFacts(1f, 0, -50, false, true, true, true)));
    }

    /// <summary>Refusal windows are phase-shifted per (candidate, group): the pairs' windows turn over at different instants, and each one every period exactly.</summary>
    [Fact]
    public void TheRefusalWindows_AreShiftedPerPair_AndTurnOverExactlyEveryPeriod()
    {
        const uint period = 60_000;
        const ulong first = 3, second = 4; // character guids: the phase is a function of the guid and the group alone, the same on every run
        Assert.Equal(21461u, PlayerbotRecruitment.RefusalPhase(period, 3, 1)); // fixed by the guid and the group: nothing random in it
        Assert.NotEqual(PlayerbotRecruitment.RefusalPhase(period, first, 7), PlayerbotRecruitment.RefusalPhase(period, second, 7));
        Assert.NotEqual(PlayerbotRecruitment.RefusalPhase(period, first, 7), PlayerbotRecruitment.RefusalPhase(period, first, 8)); // the group is part of the pair

        List<uint> Boundaries(ulong bot)
        {
            var found = new List<uint>();
            for (uint t = 1; t < 4 * period; t++)
                if (PlayerbotRecruitment.RefusalWindow(t, period, bot, 7) != PlayerbotRecruitment.RefusalWindow(t - 1, period, bot, 7)) found.Add(t);
            return found;
        }

        List<uint> b1 = Boundaries(first), b2 = Boundaries(second);
        Assert.InRange(b1.Count, 3, 4);
        Assert.InRange(b2.Count, 3, 4);
        for (int i = 1; i < b1.Count; i++) Assert.Equal(period, b1[i] - b1[i - 1]);
        for (int i = 1; i < b2.Count; i++) Assert.Equal(period, b2[i] - b2[i - 1]);
        Assert.NotEqual(b1[0] % period, b2[0] % period);
    }

    [Fact]
    public void TheRoll_IsAHashOfTheCandidateAndTheGroup_SoTheAnswerNeverChanges()
    {
        var facts = new PlayerbotRecruitFacts(0.5f, 0, 0, false, false, false, false);
        for (ulong guid = 1; guid < 50; guid++)
            for (int group = 1; group < 20; group++)
            {
                double roll = PlayerbotRecruitment.Roll(guid, group);
                Assert.InRange(roll, 0d, 1d);
                Assert.Equal(roll, PlayerbotRecruitment.Roll(guid, group));
                Assert.Equal(PlayerbotRecruitment.Accepts(Defaults, guid, group, facts), PlayerbotRecruitment.Accepts(Defaults, guid, group, facts));
            }

        // It varies with both: not one constant answer for a candidate, nor for a group.
        Assert.True(Enumerable.Range(1, 40).Select(group => PlayerbotRecruitment.Roll(7, group)).Distinct().Count() > 30);
        Assert.True(Enumerable.Range(1, 40).Select(guid => PlayerbotRecruitment.Roll((ulong)guid, 3)).Distinct().Count() > 30);
        int accepted = 0;
        for (ulong guid = 1; guid <= 2000; guid++) if (PlayerbotRecruitment.Accepts(Defaults, guid, 9, facts)) accepted++;
        Assert.InRange(accepted, 1000, 1200); // the chance here is 0.55
    }

    [Theory]
    [InlineData(PlayerbotGoalKind.Explore, 0, true)]
    [InlineData(PlayerbotGoalKind.Grind, 0, true)]
    [InlineData(PlayerbotGoalKind.Vendor, 0, true)]
    [InlineData(PlayerbotGoalKind.Train, 0, true)]
    [InlineData(PlayerbotGoalKind.Combat, 0, false)]
    [InlineData(PlayerbotGoalKind.Recover, 0, false)]
    [InlineData(PlayerbotGoalKind.Group, 0, false)]
    [InlineData(PlayerbotGoalKind.Battleground, 0, false)]
    [InlineData(PlayerbotGoalKind.Follow, 0, false)]
    [InlineData(PlayerbotGoalKind.Quest, 5, true)]
    [InlineData(PlayerbotGoalKind.Quest, 3, true)]
    [InlineData(PlayerbotGoalKind.Quest, 2, false)] // two kills from done: not interrupted
    [InlineData(PlayerbotGoalKind.Quest, 0, false)]
    public void OnlyABotOnALowPriorityGoal_MayBePulledAway(PlayerbotGoalKind goal, int remaining, bool yields)
        => Assert.Equal(yields, PlayerbotRecruitment.GoalYields(goal, remaining));

    [Fact]
    public void TheMissingRoles_AreWhatTheWaitingCannotCover()
    {
        var options = new PlayerbotGroupOptions();
        Assert.Equal((1, 1), PlayerbotRecruitment.MissingRoles([Bot("Mage", Class.Mage, 1), Bot("Rogue", Class.Rogue, 2)], 3, options));
        Assert.Equal((0, 1), PlayerbotRecruitment.MissingRoles([Bot("Mage", Class.Mage, 1), Bot("Warrior", Class.Warrior, 2)], 3, options));
        Assert.Equal((0, 0), PlayerbotRecruitment.MissingRoles([Bot("Priest", Class.Priest, 1), Bot("Warrior", Class.Warrior, 2)], 3, options));
        Assert.Equal((0, 0), PlayerbotRecruitment.MissingRoles([Bot("Mage", Class.Mage, 1), Bot("Rogue", Class.Rogue, 2)], 2, options)); // a pair needs no roles
    }

    [Fact]
    public void TheOrder_TakesTheRoleFillersFirst_ThenThePreferred_ThenTheNearer()
    {
        PlayerbotRecruitment.Ranked Rank(string name, Class cls, int preference, float distance)
            => new(Bot(name, cls, 5), preference, distance, PlayerbotGroupContent.CanTank(cls), PlayerbotGroupContent.CanHeal(cls));
        PlayerbotRecruitment.Ranked[] accepted =
        [
            Rank("Friendlymage", Class.Mage, 90, 10),
            Rank("Nearmage", Class.Mage, 0, 5),
            Rank("Farmage", Class.Mage, 0, 50),
            Rank("Healer", Class.Priest, -5, 200),
            Rank("Tank", Class.Warrior, -5, 200),
        ];

        // Short one tank, one healer, and one more member besides: the fillers, then the best of the rest.
        List<PlayerbotGroupCandidate> chosen = PlayerbotRecruitment.Order(accepted, (1, 1), slots: 3);
        Assert.Equal(["Tank", "Healer", "Friendlymage"], chosen.Select(c => c.Name));

        // Two free slots, no role missing: preference, then distance.
        Assert.Equal(["Friendlymage", "Nearmage"], PlayerbotRecruitment.Order(accepted, (0, 0), slots: 2).Select(c => c.Name));
        Assert.Equal(["Friendlymage", "Nearmage", "Farmage"], PlayerbotRecruitment.Order(accepted, (0, 0), slots: 3).Select(c => c.Name));

        // Never fewer than the role fillers, even with no free slot.
        Assert.Equal(["Healer"], PlayerbotRecruitment.Order(accepted, (0, 1), slots: 0).Select(c => c.Name));
    }

    [Fact]
    public void TheGuildBonus_ChangesThePick_BetweenOtherwiseEqualCandidates()
    {
        PlayerbotGroupCandidate Equal(string name, uint since, int bonus) => Bot(name, Class.Mage, 10, since) with { Bonus = bonus };
        var options = new PlayerbotGroupOptions();

        // Three mages that waited equally long, a pair wanted: the bot that waited longest and a guildmate of it, not the lowest id.
        PlayerbotGroupCandidate[] withGuild = [Equal("Anchor", 1, 1), Equal("Aaa", 5, 0), Equal("Mate", 5, 1)];
        PlayerbotGroupCandidate[] without = [Equal("Anchor", 1, 0), Equal("Aaa", 5, 0), Equal("Mate", 5, 0)];
        PlayerbotGroupPlan guilded = PlayerbotGroupContent.Match(withGuild, 2, options)!;
        Assert.Equal(["Anchor", "Mate"], guilded.Members.Select(m => m.Bot.Name).OrderBy(n => n));

        // Without the bonus the tie falls to the id order, which (BotIds being random here) is whatever it is: the two plans must differ only by that.
        PlayerbotGroupPlan plain = PlayerbotGroupContent.Match(without, 2, options)!;
        Assert.Contains(plain.Members, m => m.Bot.Name == "Anchor");
        PlayerbotGroupCandidate[] swapped = [Equal("Anchor", 1, 1), Equal("Aaa", 5, 1), Equal("Mate", 5, 0)];
        Assert.Equal(["Aaa", "Anchor"], PlayerbotGroupContent.Match(swapped, 2, options)!.Members.Select(m => m.Bot.Name).OrderBy(n => n));
    }

    [Fact]
    public void ThePreference_CountsAffinity_Trust_AndTheGuild()
    {
        Assert.Equal(0, PlayerbotRecruitment.Preference(0, 0, false, Defaults));
        Assert.Equal(40, PlayerbotRecruitment.Preference(30, 100, false, Defaults));
        Assert.Equal(20, PlayerbotRecruitment.Preference(0, 0, true, Defaults));
        Assert.True(PlayerbotRecruitment.Preference(10, 0, true, Defaults) > PlayerbotRecruitment.Preference(25, 0, false, Defaults));
    }

    [Fact]
    public void TheCooperationOptions_AreValidated()
    {
        static void Throws(Action<PlayerbotCooperationOptions> bad)
        {
            var options = new PlayerbotCooperationOptions();
            bad(options);
            Assert.Throws<InvalidOperationException>(options.Validate);
        }

        Throws(o => o.RecruitWaitSeconds = 0);
        Throws(o => o.RecruitRadiusYards = 5);
        Throws(o => o.RecruitBaseChance = 1.5f);
        Throws(o => o.GuildBonus = -0.1f);
        Throws(o => o.RefuseAffinity = 1);
        Throws(o => o.MaxRaidSize = 5);
        Throws(o => o.MaxRaidSize = 41);
        Throws(o => o.MentorMaxMinutes = 0);
        Throws(o => o.MentorLevelGap = 0);
        new PlayerbotCooperationOptions().Validate();
    }

    private static PlayerbotGroupCandidate Bot(string name, Class @class, byte level, uint since = 1)
        => new(Guid.NewGuid(), (ulong)name.GetHashCode(StringComparison.Ordinal) & 0xFFFF, name, @class, level, 1, 0, Vector3.Zero, PlayerbotRole.MeleeDps, 0, since);
}
