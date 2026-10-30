using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Live logs showed level 58-60 bots queueing ~1300 Wailing Caverns (map 43, band 17-24) groups for a level-21 quest. The fit rule: a bot
/// outside a dungeon's band goes only with a reason, and the choice among dungeons prefers the band, then the nearest band.
/// </summary>
public sealed class PlayerbotDungeonFitTests
{
    private const uint WailingCaverns = 43, DireMaul = 429, Stratholme = 329;

    private static PlayerbotDungeonBand Wc() => PlayerbotDungeonFit.BandOf(WailingCaverns, 15, []);

    private static PlayerbotDungeonBand Strat() => PlayerbotDungeonFit.BandOf(Stratholme, 55, []);

    private static (PlayerbotDungeonBand, PlayerbotDungeonReasons) Opt(byte min, byte max, PlayerbotDungeonReasons r) => (new PlayerbotDungeonBand(min, max), r);

    // --- BandOf ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void BandOf_WithoutCatalogue_WidensTheRecommendedBandByTheStaticSlack()
    {
        Assert.Equal(new PlayerbotDungeonBand(15, 26), PlayerbotDungeonFit.BandOf(WailingCaverns, 15, [])); // 17-24 +-2
        Assert.Equal(new PlayerbotDungeonBand(54, 62), PlayerbotDungeonFit.BandOf(DireMaul, 55, [])); // 56-60 +-2
        Assert.Equal(new PlayerbotDungeonBand(56, 62), PlayerbotDungeonFit.BandOf(Stratholme, 55, [])); // 58-60 +-2
    }

    [Fact]
    public void BandOf_UsesTheCatalogueBandsOfTheMap_AsTheirUnion()
    {
        PlayerbotDungeonBand[] bands = [new(20, 30), new(18, 25)];
        Assert.Equal(new PlayerbotDungeonBand(18, 30), PlayerbotDungeonFit.BandOf(WailingCaverns, 15, bands));
        Assert.Equal(new PlayerbotDungeonBand(20, 30), PlayerbotDungeonFit.BandOf(WailingCaverns, 15, [new PlayerbotDungeonBand(20, 30)]));
    }

    [Fact]
    public void BandOf_ForAMapWithNeitherCatalogueNorRecommendation_IsFromTheEntranceLevelUp()
    {
        Assert.Equal(new PlayerbotDungeonBand(40, 255), PlayerbotDungeonFit.BandOf(99_999, 40, []));
    }

    // --- QuestQualifies -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(60, 21, false)] // grey: the gray level of 60 is 47
    [InlineData(60, 47, false)] // the gray level itself is still grey
    [InlineData(60, 48, true)]
    [InlineData(60, 58, true)]
    [InlineData(20, 21, true)]
    [InlineData(12, 10, true)] // the harness quests: the gray level of 12 is 6
    public void QuestQualifies_WhenTheQuestIsNotGreyForTheBot(int level, int questLevel, bool expected)
        => Assert.Equal(expected, PlayerbotDungeonFit.QuestQualifies(level, questLevel));

    // --- Judge ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ALevel60WithNoReason_IsRefusedWailingCaverns()
    {
        PlayerbotDungeonVerdict v = PlayerbotDungeonFit.Judge(60, Wc(), PlayerbotDungeonReasons.None);
        Assert.False(v.Allowed);
        Assert.False(v.InBand);
        Assert.Contains("no reason", v.Trace, StringComparison.Ordinal);
    }

    [Fact]
    public void ALevel60WithAQuestReason_IsAllowedWailingCaverns_AndTheTraceNamesIt()
    {
        PlayerbotDungeonVerdict v = PlayerbotDungeonFit.Judge(60, Wc(), PlayerbotDungeonReasons.Quest);
        Assert.True(v.Allowed);
        Assert.False(v.InBand);
        Assert.Contains("out-of-band", v.Trace, StringComparison.Ordinal);
        Assert.Contains("quest", v.Trace, StringComparison.Ordinal);
    }

    [Fact]
    public void ABotInTheBand_IsAllowedWithoutAReason_AtTheBandEdgesToo()
    {
        foreach (int level in new[] { 15, 20, 26 })
        {
            PlayerbotDungeonVerdict v = PlayerbotDungeonFit.Judge(level, Wc(), PlayerbotDungeonReasons.None);
            Assert.True(v.Allowed, $"level {level}");
            Assert.True(v.InBand, $"level {level}");
            Assert.StartsWith("in-band", v.Trace, StringComparison.Ordinal);
        }

        Assert.False(PlayerbotDungeonFit.Judge(27, Wc(), PlayerbotDungeonReasons.None).Allowed);
        Assert.False(PlayerbotDungeonFit.Judge(14, Wc(), PlayerbotDungeonReasons.None).Allowed);
    }

    [Fact]
    public void WhenNoReasonIsRequired_EveryBotIsAllowed()
    {
        PlayerbotDungeonVerdict v = PlayerbotDungeonFit.Judge(60, Wc(), PlayerbotDungeonReasons.None, reasonRequired: false);
        Assert.True(v.Allowed);
        Assert.False(v.InBand);
    }

    [Fact]
    public void AnyOneReason_JustifiesAnOutOfBandRun()
    {
        foreach (PlayerbotDungeonReasons r in new[] { PlayerbotDungeonReasons.Attunement, PlayerbotDungeonReasons.GearUpgrade, PlayerbotDungeonReasons.Companion, PlayerbotDungeonReasons.FormingGroup, PlayerbotDungeonReasons.Farming })
        {
            PlayerbotDungeonVerdict v = PlayerbotDungeonFit.Judge(60, Wc(), r);
            Assert.True(v.Allowed, r.ToString());
            Assert.Contains(r.ToString().ToLowerInvariant(), v.Trace, StringComparison.Ordinal);
        }
    }

    // --- Choose ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ALevel60_NeverChoosesWailingCavernsWithoutAReason()
    {
        Assert.Null(PlayerbotDungeonFit.Choose(60, [Opt(15, 26, PlayerbotDungeonReasons.None)]));
        Assert.Null(PlayerbotDungeonFit.Choose(60, []));
    }

    [Fact]
    public void ALevel60WithAQuestReason_ChoosesWailingCaverns_WhenItIsTheOnlyOption()
    {
        Assert.Equal(0, PlayerbotDungeonFit.Choose(60, [(Wc(), PlayerbotDungeonReasons.Quest)]));
    }

    [Fact]
    public void ALevel60GivenWailingCavernsAndStratholmeBothForAQuest_ChoosesStratholme()
    {
        Assert.Equal(1, PlayerbotDungeonFit.Choose(60, [(Wc(), PlayerbotDungeonReasons.Quest), (Strat(), PlayerbotDungeonReasons.Quest)]));
        Assert.Equal(0, PlayerbotDungeonFit.Choose(60, [(Strat(), PlayerbotDungeonReasons.Quest), (Wc(), PlayerbotDungeonReasons.Quest)]));
    }

    [Fact]
    public void InBandOptions_BeatOutOfBandOnes_AndAmongThemTheHighestMinWins()
    {
        Assert.Equal(2, PlayerbotDungeonFit.Choose(58, [Opt(15, 26, PlayerbotDungeonReasons.Quest), Opt(50, 60, PlayerbotDungeonReasons.None), Opt(56, 62, PlayerbotDungeonReasons.None)]));
        // An in-band option needs no reason, and an out-of-band one with a reason still loses to it.
        Assert.Equal(1, PlayerbotDungeonFit.Choose(58, [Opt(40, 50, PlayerbotDungeonReasons.Quest), Opt(50, 60, PlayerbotDungeonReasons.None)]));
    }

    [Fact]
    public void OutOfBandOptionsWithAReason_AreChosenByTheSmallestDistance_TiesKeepTheLowerIndex()
    {
        Assert.Equal(1, PlayerbotDungeonFit.Choose(60, [Opt(15, 26, PlayerbotDungeonReasons.Quest), Opt(30, 40, PlayerbotDungeonReasons.Farming)]));
        Assert.Equal(0, PlayerbotDungeonFit.Choose(60, [Opt(30, 40, PlayerbotDungeonReasons.Quest), Opt(30, 40, PlayerbotDungeonReasons.Quest)]));
        // Below the band counts the same way: level 10 is 5 below 15-26 and 10 below 20-30.
        Assert.Equal(0, PlayerbotDungeonFit.Choose(10, [Opt(15, 26, PlayerbotDungeonReasons.Quest), Opt(20, 30, PlayerbotDungeonReasons.Quest)]));
    }

    [Fact]
    public void AnOutOfBandOptionWithoutAReason_IsNeverChosen_EvenWhenNearer()
    {
        Assert.Equal(1, PlayerbotDungeonFit.Choose(60, [Opt(50, 55, PlayerbotDungeonReasons.None), Opt(15, 26, PlayerbotDungeonReasons.Quest)]));
        Assert.Null(PlayerbotDungeonFit.Choose(60, [Opt(50, 55, PlayerbotDungeonReasons.None), Opt(15, 26, PlayerbotDungeonReasons.None)]));
    }

    [Fact]
    public void WhenNoReasonIsRequired_ChooseTakesTheNearestOption()
    {
        Assert.Equal(1, PlayerbotDungeonFit.Choose(60, [Opt(15, 26, PlayerbotDungeonReasons.None), Opt(50, 55, PlayerbotDungeonReasons.None)], reasonRequired: false));
    }
}
