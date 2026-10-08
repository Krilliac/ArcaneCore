using ArcaneCore.Data.Talents;
using ArcaneCore.Game;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.World.Playerbots.Progression;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// The talent planner as a pure function: spending order and tier gates, refusals, build resolution against synthetic catalogs
/// and the vmangos LearnRandomTalents fallback (CombatBotBaseAI.cpp:2459). Every step is applied the way the server applies
/// it (<see cref="TalentRules.EvaluateLearn"/>), so a plan that the server would refuse fails here.
/// </summary>
public sealed class PlayerbotTalentPlannerTests
{
    private const uint Warrior = 1u << 0;

    [Fact]
    public void SpendingFollowsTheBuild_RespectsTheTierGate_AndStopsAtZeroPoints()
    {
        // Page 0: r0c0 (3 ranks), r0c1 (5 ranks), r1c0 (1 rank). The build wants r0c0 x2 and then r1c0, which needs 5 points.
        TalentCatalog catalog = new([new TalentTabRecord(10, Warrior, 0)],
        [
            Talent(1, 10, 0, 0, 3), Talent(2, 10, 0, 1, 5), Talent(3, 10, 1, 0, 1),
        ]);
        PlayerbotTalentBuild build = Build(P(0, 0, 0, 2), P(0, 1, 0, 1));
        var book = new HashSet<uint>();

        List<PlayerbotTalentStep> steps = Spend(catalog, book, build, points: 6);

        // r0c0 twice from the build; r1c0 is tier-locked at 2 points, so the fallback fills the tab in tier order
        // (r0c0's last rank, then r0c1) until the gate opens; the 6th point then takes r1c0.
        Assert.Equal(
        [
            (1u, 0u, true), (1u, 1u, true), (1u, 2u, false), (2u, 0u, false), (2u, 1u, false), (3u, 0u, true),
        ], steps.Select(step => (step.TalentId, step.RankIndex, step.FromBuild)));
        Assert.Null(PlayerbotTalentPlanner.Next(catalog, book.Contains, Warrior, freePoints: 0, build, botId: 1));
    }

    [Fact]
    public void ARefusedRank_IsNotRequestedAgain_AndTheNextLegalTalentIsTaken()
    {
        TalentCatalog catalog = new([new TalentTabRecord(10, Warrior, 0)], [Talent(1, 10, 0, 0, 3), Talent(2, 10, 0, 1, 5)]);
        PlayerbotTalentBuild build = Build(P(0, 0, 0, 3));
        var refused = new HashSet<(uint, uint)> { (1, 0) };

        PlayerbotTalentStep? step = PlayerbotTalentPlanner.Next(catalog, _ => false, Warrior, 3, build, 1, refused);

        Assert.Equal(new PlayerbotTalentStep(2, 0, FromBuild: false), step);
        refused.Add((2, 0));
        Assert.Null(PlayerbotTalentPlanner.Next(catalog, _ => false, Warrior, 3, build, 1, refused));
    }

    [Fact]
    public void ACatalogWithoutTheClass_OrWithoutLegalTalents_PlansNothing()
    {
        TalentCatalog catalog = new([new TalentTabRecord(10, 1u << 7, 0)], [Talent(1, 10, 0, 0, 3)]);
        Assert.Null(PlayerbotTalentPlanner.Next(catalog, _ => false, Warrior, 5, Build(P(0, 0, 0, 3)), 1));
        TalentCatalog gated = new([new TalentTabRecord(10, Warrior, 0)], [Talent(1, 10, 1, 0, 3)]);
        Assert.Null(PlayerbotTalentPlanner.Next(gated, _ => false, Warrior, 5, Build(P(0, 1, 0, 3)), 1));
    }

    /// <summary>
    /// Every authored build resolves against a synthetic catalog that has a talent at each of its positions and nothing else:
    /// all its points come from the build in its own order, and its tier gates hold (each step passes EvaluateLearn), whatever
    /// the real DBC ids are.
    /// </summary>
    [Fact]
    public void EveryBuild_SpendsItsPointsInOrderAgainstASyntheticCatalogOfItsPositions()
    {
        Assert.NotEmpty(PlayerbotTalentBuilds.All);
        foreach (PlayerbotTalentBuild build in PlayerbotTalentBuilds.All)
        {
            uint classMask = 1u << ((int)build.Class - 1);
            TalentCatalog catalog = CatalogOf(build, classMask);
            var book = new HashSet<uint>();
            List<PlayerbotTalentStep> steps = Spend(catalog, book, build, (uint)build.Points, classMask);
            Assert.True(steps.Count == build.Points, $"{build.Class}/{build.Name}: {steps.Count} of {build.Points} points");
            Assert.All(steps, step => Assert.True(step.FromBuild, $"{build.Class}/{build.Name}: talent {step.TalentId} came from the fallback"));
            Assert.Equal(51, build.Points);
        }
    }

    [Fact]
    public void EveryClassHasOneToThreeBuilds_AndTheChoiceIsAFunctionOfTheBotId()
    {
        foreach (Class playerClass in new[] { Class.Warrior, Class.Paladin, Class.Hunter, Class.Rogue, Class.Priest, Class.Shaman, Class.Mage, Class.Warlock, Class.Druid })
        {
            IReadOnlyList<PlayerbotTalentBuild> builds = PlayerbotTalentBuilds.For(playerClass);
            Assert.InRange(builds.Count, 1, 3);
            for (uint id = 0; id < 12; id++)
            {
                Assert.Same(PlayerbotTalentBuilds.Choose(playerClass, id), PlayerbotTalentBuilds.Choose(playerClass, id));
                Assert.Same(builds[(int)(id % (uint)builds.Count)], PlayerbotTalentBuilds.Choose(playerClass, id));
            }
        }

        Assert.Equal(PlayerbotTalentBuilds.ShieldSlam, PlayerbotTalentBuilds.For(Class.Warrior).Single(b => b.Name == "protection").RoleSpell);
    }

    [Fact]
    public void AMissingTemplateTalent_FallsBackToOneTabInTierOrder()
    {
        // The build starts on page 1, which this catalog does not have: the fallback takes the build's page if it exists,
        // otherwise the bot id picks a tab (here 7 % 2 = page 1, tab 20), and walks it by row, then column.
        TalentCatalog catalog = new([new TalentTabRecord(10, Warrior, 0), new TalentTabRecord(20, Warrior, 1)],
        [
            Talent(1, 10, 0, 0, 5), Talent(5, 20, 0, 2, 1), Talent(6, 20, 0, 1, 2), Talent(7, 20, 1, 0, 3),
        ]);
        PlayerbotTalentBuild build = Build(P(2, 0, 0, 5));
        var book = new HashSet<uint>();

        List<PlayerbotTalentStep> steps = Spend(catalog, book, build, points: 2, botId: 7);

        Assert.Equal([(6u, 0u), (6u, 1u)], steps.Select(step => (step.TalentId, step.RankIndex)));
        Assert.All(steps, step => Assert.False(step.FromBuild));
        // Once points sit in a tab, the fallback stays there whatever the id says: the next talent in tier order of tab 20.
        Assert.Equal(new PlayerbotTalentStep(5, 0, FromBuild: false), PlayerbotTalentPlanner.Next(catalog, book.Contains, Warrior, 1, build, botId: 0));
    }

    /// <summary>
    /// With the developer's build-5875 Talent.dbc and TalentTab.dbc (ARCANECORE_TEST_DBC_DIR), every build spends all 51 points
    /// from its own picks and reaches the talent vmangos AutoAssignRole checks; a healer build reaches none of them.
    /// </summary>
    [RealTalentDbcFact]
    public void EveryBuild_SpendsFiftyOnePointsAgainstTheClientTalentDbc_AndReachesItsRoleSpell()
    {
        string dir = Environment.GetEnvironmentVariable(RealTalentDbcFactAttribute.Variable)!;
        TalentCatalog catalog = TalentDbcReaders.Load(Path.Combine(dir, "Talent.dbc"), Path.Combine(dir, "TalentTab.dbc"));
        uint[] roleSpells =
        [
            PlayerbotTalentBuilds.ShieldSlam, PlayerbotTalentBuilds.HolyShield, PlayerbotTalentBuilds.SanctityAura, PlayerbotTalentBuilds.Shadowform,
            PlayerbotTalentBuilds.ElementalMastery, PlayerbotTalentBuilds.Stormstrike, PlayerbotTalentBuilds.MoonkinForm, PlayerbotTalentBuilds.LeaderOfThePack,
        ];
        foreach (PlayerbotTalentBuild build in PlayerbotTalentBuilds.All)
        {
            uint classMask = 1u << ((int)build.Class - 1);
            // Class spells some talents require (Talent.dbc DependsOnSpell: Nature's Grasp needs Entangling Roots, 339) are
            // trained long before level 10; the book starts with them.
            var book = new HashSet<uint>(catalog.Talents.Select(talent => talent.DependsOnSpell).Where(spell => spell != 0));
            List<PlayerbotTalentStep> steps = Spend(catalog, book, build, 51, classMask);
            Assert.True(steps.Count == 51 && steps.All(step => step.FromBuild),
                $"{build.Class}/{build.Name}: {steps.Count(step => step.FromBuild)} build points of {steps.Count}");
            uint[] reached = [.. roleSpells.Where(book.Contains)];
            Assert.Equal(build.RoleSpell == 0 ? [] : [build.RoleSpell], reached);
        }
    }

    internal static PlayerbotTalentPick P(byte page, byte row, byte column, byte ranks) => new(page, row, column, ranks);

    internal static PlayerbotTalentBuild Build(params PlayerbotTalentPick[] picks)
        => new(Class.Warrior, "test", PlayerbotStatWeights.TwoHandStrength, 0, picks);

    /// <summary>A talent whose rank spells are id * 100 + rank (1-based).</summary>
    internal static TalentRecord Talent(uint id, uint tab, uint row, uint column, int ranks, uint dependsOn = 0, uint dependsOnRank = 0)
        => new(id, tab, row, column, [.. Enumerable.Range(1, 5).Select(rank => rank <= ranks ? (id * 100) + (uint)rank : 0u)],
            dependsOn, dependsOnRank, 0);

    /// <summary>Apply planned steps to <paramref name="book"/> as the server would, until the points run out or nothing is planned.</summary>
    internal static List<PlayerbotTalentStep> Spend(TalentCatalog catalog, HashSet<uint> book, PlayerbotTalentBuild build, uint points,
        uint classMask = Warrior, uint botId = 1)
    {
        var steps = new List<PlayerbotTalentStep>();
        while (points > 0 && PlayerbotTalentPlanner.Next(catalog, book.Contains, classMask, points, build, botId) is { } step)
        {
            TalentLearnResult verdict = TalentRules.EvaluateLearn(catalog, book.Contains, step.TalentId, step.RankIndex, points, classMask);
            Assert.Equal(TalentLearnOutcome.Ok, verdict.Outcome);
            foreach (uint other in catalog.ById(step.TalentId)!.RankSpells)
                book.Remove(other);
            book.Add(verdict.RankSpell);
            points -= verdict.PointsNeeded;
            steps.Add(step);
        }

        return steps;
    }

    /// <summary>Three tabs for the class; a talent at every position the build names, with the most ranks it asks for there.</summary>
    private static TalentCatalog CatalogOf(PlayerbotTalentBuild build, uint classMask)
    {
        uint id = 1;
        var talents = new List<TalentRecord>();
        foreach (var position in build.Picks.GroupBy(pick => (pick.Page, pick.Row, pick.Column)))
            talents.Add(Talent(id++, 100u + position.Key.Page, position.Key.Row, position.Key.Column, position.Max(pick => pick.Ranks)));
        return new TalentCatalog(
            [new TalentTabRecord(100, classMask, 0), new TalentTabRecord(101, classMask, 1), new TalentTabRecord(102, classMask, 2)], talents);
    }
}

/// <summary>Skipped (with the variable name) when ARCANECORE_TEST_DBC_DIR does not name a DBC directory; never a silent pass.</summary>
public sealed class RealTalentDbcFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_DBC_DIR";

    public RealTalentDbcFactAttribute()
    {
        string? dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "Talent.dbc")) || !File.Exists(Path.Combine(dir, "TalentTab.dbc")))
        {
            Skip = $"{Variable} is not set to a directory holding the build-5875 Talent.dbc and TalentTab.dbc.";
        }
    }
}
