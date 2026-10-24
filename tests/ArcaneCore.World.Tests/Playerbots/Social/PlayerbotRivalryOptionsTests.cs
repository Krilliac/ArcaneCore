using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// <c>World:Playerbots:Social:Rivalry</c> (P6a rows of the design's section 5): the defaults the owner approved (new behaviour on, rude emotes at
/// bots off), the bounds <see cref="PlayerbotSocialRivalryOptions.Validate"/> enforces, the safe-area list and the chain from
/// <see cref="PlayerbotSocialOptions.Validate"/>. The generated files (world.conf.dist, conf-catalog.json, configuration.md) and the live-key
/// classification are covered by <c>ConfDistTests</c>, <c>DocKeyAuditTests</c> and <c>ConfigReloadTests</c>.
/// </summary>
public sealed class PlayerbotRivalryOptionsTests
{
    [Fact]
    public void TheDefaults_AreTheApprovedOnes()
    {
        var options = new PlayerbotSocialRivalryOptions();
        Assert.True(options.Enabled);
        Assert.True(options.AllowOptOut);
        Assert.True(options.Emotes.Enabled);
        Assert.True(options.Emotes.RivalEmotes);
        Assert.False(options.Emotes.RudeAtBots);
        Assert.False(options.Emotes.RudeRequiresOptIn);
        Assert.True(options.Emotes.BotToBot);
        Assert.Equal(20, options.Emotes.FriendlyAffinity);
        Assert.Equal(20, options.Emotes.RangeYards);
        Assert.Equal(0.25, options.Emotes.GreetStrangerChance);
        Assert.Equal(30, options.Emotes.RudePairGapMinutes);
        Assert.Equal(30, options.Emotes.RudePerTargetMinutes);
        Assert.Equal(12, options.Emotes.EmotesPerMinute);
        options.Validate();
    }

    [Fact]
    public void TheMemoryOption_ForOrganisedKills_IsOffByDefault_SoABattlegroundKillIsNotAGrudge()
    {
        Assert.False(new PlayerbotSocialMemoryOptions().OrganisedPvpKills);
        new PlayerbotSocialMemoryOptions().Validate();
        new PlayerbotSocialMemoryOptions { OrganisedPvpKills = true }.Validate();
    }

    [Fact]
    public void TheSocialOptions_HoldTheRivalryOptions_AndRefuseAMissingOrBadOne()
    {
        var social = new PlayerbotSocialOptions();
        Assert.NotNull(social.Rivalry);
        social.Validate();

        social.Rivalry = null!;
        Assert.Contains("Rivalry", Assert.Throws<InvalidOperationException>(social.Validate).Message);

        social = new PlayerbotSocialOptions();
        social.Rivalry.Emotes.RangeYards = 26;
        Assert.Contains("RangeYards", Assert.Throws<InvalidOperationException>(social.Validate).Message);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(25, true)]
    [InlineData(4, false)]
    [InlineData(26, false)]
    public void RangeYards_IsFiveToTwentyFive(int yards, bool valid) => Check(o => o.Emotes.RangeYards = yards, valid, "RangeYards");

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    public void FriendlyAffinity_IsZeroToOneHundred(int affinity, bool valid) => Check(o => o.Emotes.FriendlyAffinity = affinity, valid, "FriendlyAffinity");

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, true)]
    [InlineData(-0.01, false)]
    [InlineData(1.01, false)]
    public void GreetStrangerChance_IsZeroToOne(double chance, bool valid) => Check(o => o.Emotes.GreetStrangerChance = chance, valid, "GreetStrangerChance");

    [Theory]
    [InlineData(1, true)]
    [InlineData(1440, true)]
    [InlineData(0, false)]
    [InlineData(1441, false)]
    public void RudePairGapMinutes_IsOneToADay(int minutes, bool valid) => Check(o => o.Emotes.RudePairGapMinutes = minutes, valid, "RudePairGapMinutes");

    [Theory]
    [InlineData(1, true)]
    [InlineData(1440, true)]
    [InlineData(0, false)]
    [InlineData(1441, false)]
    public void RudePerTargetMinutes_IsOneToADay(int minutes, bool valid) => Check(o => o.Emotes.RudePerTargetMinutes = minutes, valid, "RudePerTargetMinutes");

    [Theory]
    [InlineData(0, true)]
    [InlineData(120, true)]
    [InlineData(-1, false)]
    [InlineData(121, false)]
    public void EmotesPerMinute_IsZeroToOneTwenty_AndZeroSilencesTheLane(int perMinute, bool valid) => Check(o => o.Emotes.EmotesPerMinute = perMinute, valid, "EmotesPerMinute");

    private static void Check(Action<PlayerbotSocialRivalryOptions> change, bool valid, string key)
    {
        var options = new PlayerbotSocialRivalryOptions();
        change(options);
        if (valid) options.Validate();
        else Assert.Contains(key, Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    // --- Safeguards:SafeAreas -------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultSafeAreas_AreTheSixTownsWithAGuardPostForBothFactions()
    {
        // GuardPostTable: Booty Bay 35, Ratchet 392, Gadgetzan 976, Everlook 2255, Nighthaven 2361, Light's Hope Chapel 2268 (area ids, not zones).
        var options = new PlayerbotSocialRivalryOptions();
        Assert.Equal(new HashSet<uint> { 35, 392, 976, 2255, 2361, 2268 }, options.Safeguards.SafeAreaIds().ToHashSet());
        Assert.Equal(6, options.Safeguards.SafeAreaIds().Count);
    }

    [Theory]
    [InlineData("35, 392 ,976", new uint[] { 35, 392, 976 })]
    [InlineData("12", new uint[] { 12 })]
    [InlineData("35,35,35", new uint[] { 35 })]
    [InlineData("", new uint[0])]
    public void SafeAreas_IsACommaSeparatedListOfAreaIds_SpacesAndRepeatsAndAnEmptyListAreFine(string text, uint[] expected)
    {
        var options = new PlayerbotSocialRivalryOptions();
        options.Safeguards.SafeAreas = text;
        options.Validate();
        Assert.Equal(expected.ToHashSet(), options.Safeguards.SafeAreaIds().ToHashSet());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("35,x")]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("35;392")]
    public void SafeAreas_WithAnEntryThatIsNotAnAreaId_IsRefusedOnValidate(string text)
    {
        var options = new PlayerbotSocialRivalryOptions();
        options.Safeguards.SafeAreas = text;
        Assert.Contains("SafeAreas", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}
