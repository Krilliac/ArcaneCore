using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;
using Emotes = ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialEmotes.TextEmotes;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The pure rules of P6a (docs/design p6-rivalry, 4, 4.1, 4.2, 7.1): the stance of a bot toward another character in a chosen memory layer, the
/// greeting and rude picks, the stance-aware reply table, the hash rolls and the quiet places. No world, no clock.
/// <para>
/// Contract the implementation must meet (every name below is used by these tests and by the world tests):
/// <c>PlayerbotStance { Neutral, Friendly, Disliked, Rival }</c>, <c>PlayerbotRivalryLayer { Normal, War }</c>, and in
/// <c>PlayerbotRivalryRules</c>: <c>StanceOf(PlayerbotRelation?, layer, friendlyAffinity)</c>, <c>PickGreeting(stance, roll)</c>,
/// <c>PickRude(roll)</c>, <c>ReplyTo(stance, emote, toBot, roll, rudeAtBots, optedOut)</c>, <c>GreetChance</c>, <c>GreetRoll</c>, <c>PickRoll</c>,
/// <c>ReplyRoll</c>, <c>BucketMs</c> and <c>IsQuietPlace</c>. A roll is a double in [0, 1); a pick takes <c>(int)(roll * choices)</c> of its list.
/// </para>
/// </summary>
public sealed class PlayerbotRivalryRulesTests
{
    private const int Grid = 240;

    private static PlayerbotRelation Rel(short affinity, PlayerbotRelationFlags flags = PlayerbotRelationFlags.None, short war = 0,
        PlayerbotRelationFlags warFlags = PlayerbotRelationFlags.None)
        => new(Guid.Empty, 1, 77, false, affinity, 5, 500, flags, 0, string.Empty, string.Empty, war, warFlags);

    /// <summary>Rolls spread evenly over [0, 1): every choice of a pick of up to a dozen entries is reached by one of them.</summary>
    private static IEnumerable<double> Rolls() => Enumerable.Range(0, Grid).Select(i => (i + 0.5) / Grid);

    private static HashSet<uint?> Outcomes(PlayerbotStance stance, uint emote, bool toBot, bool rudeAtBots = false)
        => [.. Rolls().Select(roll => PlayerbotRivalryRules.ReplyTo(stance, emote, toBot, roll, rudeAtBots))];

    /// <summary>What today's <see cref="PlayerbotSocialEmotes.ReplyTo"/> can answer <paramref name="emote"/> with (ground truth: the existing function).</summary>
    private static HashSet<uint> TodaysAnswers(uint emote)
    {
        var answers = new HashSet<uint>();
        for (int seed = 0; seed < 500; seed++)
            if (PlayerbotSocialEmotes.ReplyTo(emote, new Random(seed)) is { } answer) answers.Add(answer);
        return answers;
    }

    private static bool IsRude(uint? emote) => emote is { } e && PlayerbotEmoteMoods.Classify(e) == PlayerbotEmoteMood.Rude;

    // --- stance ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AStranger_IsNeutral_InEveryLayer()
    {
        Assert.Equal(PlayerbotStance.Neutral, PlayerbotRivalryRules.StanceOf(null, PlayerbotRivalryLayer.Normal, 20));
        Assert.Equal(PlayerbotStance.Neutral, PlayerbotRivalryRules.StanceOf(null, PlayerbotRivalryLayer.War, 20));
    }

    [Theory]
    [InlineData(0, PlayerbotStance.Neutral)]
    [InlineData(19, PlayerbotStance.Neutral)]
    [InlineData(20, PlayerbotStance.Friendly)]
    [InlineData(100, PlayerbotStance.Friendly)]
    [InlineData(-1, PlayerbotStance.Disliked)]
    [InlineData(-49, PlayerbotStance.Disliked)]
    public void TheNormalLayersAffinity_DecidesTheStance_AtItsBoundaries(int affinity, PlayerbotStance expected)
        => Assert.Equal(expected, PlayerbotRivalryRules.StanceOf(Rel((short)affinity), PlayerbotRivalryLayer.Normal, 20));

    [Fact]
    public void TheRivalFlag_MakesARival_AndWinsOverTheAffinity()
    {
        Assert.Equal(PlayerbotStance.Rival, PlayerbotRivalryRules.StanceOf(Rel(-60, PlayerbotRelationFlags.Rival), PlayerbotRivalryLayer.Normal, 20));
        Assert.Equal(PlayerbotStance.Rival, PlayerbotRivalryRules.StanceOf(Rel(-10, PlayerbotRelationFlags.Rival), PlayerbotRivalryLayer.Normal, 20));
        // Without the flag the same low affinity is only a dislike.
        Assert.Equal(PlayerbotStance.Disliked, PlayerbotRivalryRules.StanceOf(Rel(-60), PlayerbotRivalryLayer.Normal, 20));
    }

    [Fact]
    public void FriendlyAffinity_IsTheCallersThreshold()
    {
        PlayerbotRelation liked = Rel(30);
        Assert.Equal(PlayerbotStance.Friendly, PlayerbotRivalryRules.StanceOf(liked, PlayerbotRivalryLayer.Normal, 20));
        Assert.Equal(PlayerbotStance.Neutral, PlayerbotRivalryRules.StanceOf(liked, PlayerbotRivalryLayer.Normal, 40));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void TheBlankRowOfAWarOnlyRelation_IsAStrangerToTheNormalLayer_EvenWithFriendlyAffinityZero(int friendlyAffinity)
    {
        // What TryGet returns under War Mode for a relation the normal rules evicted: affinity 0, familiarity 0, the war layer holding the rest.
        PlayerbotRelation blank = Rel(0, PlayerbotRelationFlags.None, -60, PlayerbotRelationFlags.Rival | PlayerbotRelationFlags.WarSeeded) with { Familiarity = 0 };
        Assert.Equal(PlayerbotStance.Neutral, PlayerbotRivalryRules.StanceOf(blank, PlayerbotRivalryLayer.Normal, friendlyAffinity));
        Assert.False(PlayerbotRivalryRules.IsKnown(blank, PlayerbotRivalryLayer.Normal));
        Assert.Equal(PlayerbotStance.Rival, PlayerbotRivalryRules.StanceOf(blank, PlayerbotRivalryLayer.War, friendlyAffinity));

        // The control: a row the bot really knows, at affinity 0, is Friendly when the threshold is 0.
        Assert.Equal(PlayerbotStance.Friendly, PlayerbotRivalryRules.StanceOf(Rel(0), PlayerbotRivalryLayer.Normal, 0));
    }

    [Fact]
    public void EachLayer_ReadsItsOwnAffinityAndFlags()
    {
        // The state a War Mode kill leaves behind: the normal layer holds the plain -20, the war layer a rival (x3 and the flag).
        PlayerbotRelation warOnlyRival = Rel(-20, PlayerbotRelationFlags.None, -60, PlayerbotRelationFlags.Rival | PlayerbotRelationFlags.WarSeeded);
        Assert.Equal(PlayerbotStance.Disliked, PlayerbotRivalryRules.StanceOf(warOnlyRival, PlayerbotRivalryLayer.Normal, 20));
        Assert.Equal(PlayerbotStance.Rival, PlayerbotRivalryRules.StanceOf(warOnlyRival, PlayerbotRivalryLayer.War, 20));

        // And the other way round: a normal rival the war layer has not seen.
        PlayerbotRelation normalRival = Rel(-60, PlayerbotRelationFlags.Rival, 0, PlayerbotRelationFlags.WarSeeded);
        Assert.Equal(PlayerbotStance.Rival, PlayerbotRivalryRules.StanceOf(normalRival, PlayerbotRivalryLayer.Normal, 20));
        Assert.Equal(PlayerbotStance.Neutral, PlayerbotRivalryRules.StanceOf(normalRival, PlayerbotRivalryLayer.War, 20));

        PlayerbotRelation warFriend = Rel(0, PlayerbotRelationFlags.None, 40, PlayerbotRelationFlags.WarSeeded);
        Assert.Equal(PlayerbotStance.Neutral, PlayerbotRivalryRules.StanceOf(warFriend, PlayerbotRivalryLayer.Normal, 20));
        Assert.Equal(PlayerbotStance.Friendly, PlayerbotRivalryRules.StanceOf(warFriend, PlayerbotRivalryLayer.War, 20));
    }

    // --- constants and picks ----------------------------------------------------------------------------------------------------

    [Fact]
    public void MockAndViolin_AreTheVmangosTextEmoteIds_AndBothClassifyRude()
    {
        // The ids PlayerbotEmoteMoods.Classify already lists as rude ("mock", "violin"); P6a gives them names.
        Assert.Equal(119u, Emotes.Mock);
        Assert.Equal(143u, Emotes.Violin);
        Assert.Equal(PlayerbotEmoteMood.Rude, PlayerbotEmoteMoods.Classify(Emotes.Mock));
        Assert.Equal(PlayerbotEmoteMood.Rude, PlayerbotEmoteMoods.Classify(Emotes.Violin));
        // Revision 1's "laugh" is a neutral emote: it must not be a rude pick.
        Assert.Equal(PlayerbotEmoteMood.Neutral, PlayerbotEmoteMoods.Classify(Emotes.Laugh));
    }

    [Fact]
    public void AFriendlyGreeting_IsOneOfWaveBowSaluteCheerHail_AndReachesEachOfThem()
    {
        HashSet<uint> picks = [.. Rolls().Select(roll => PlayerbotRivalryRules.PickGreeting(PlayerbotStance.Friendly, roll))];
        Assert.Equal(new HashSet<uint> { Emotes.Wave, Emotes.Bow, Emotes.Salute, Emotes.Cheer, Emotes.Hail }, picks);
        Assert.All(picks, p => Assert.Equal(PlayerbotEmoteMood.Friendly, PlayerbotEmoteMoods.Classify(p)));
    }

    [Fact]
    public void ANeutralGreeting_IsOneOfWaveNodBow_AndReachesEachOfThem_AndNeverClassifiesRude()
    {
        HashSet<uint> picks = [.. Rolls().Select(roll => PlayerbotRivalryRules.PickGreeting(PlayerbotStance.Neutral, roll))];
        Assert.Equal(new HashSet<uint> { Emotes.Wave, Emotes.Nod, Emotes.Bow }, picks);
        Assert.All(picks, p => Assert.NotEqual(PlayerbotEmoteMood.Rude, PlayerbotEmoteMoods.Classify(p)));
    }

    [Fact]
    public void ARudePick_IsOneOfRudeChickenThreatenMockViolin_ReachesEachOfThem_AndAlwaysClassifiesRude()
    {
        HashSet<uint> picks = [.. Rolls().Select(PlayerbotRivalryRules.PickRude)];
        Assert.Equal(new HashSet<uint> { Emotes.Rude, Emotes.Chicken, Emotes.Threaten, Emotes.Mock, Emotes.Violin }, picks);
        Assert.All(picks, p => Assert.Equal(PlayerbotEmoteMood.Rude, PlayerbotEmoteMoods.Classify(p)));
    }

    [Fact]
    public void APickIsAPureFunctionOfItsRoll()
    {
        foreach (double roll in Rolls())
        {
            Assert.Equal(PlayerbotRivalryRules.PickRude(roll), PlayerbotRivalryRules.PickRude(roll));
            Assert.Equal(PlayerbotRivalryRules.PickGreeting(PlayerbotStance.Friendly, roll), PlayerbotRivalryRules.PickGreeting(PlayerbotStance.Friendly, roll));
        }
    }

    // --- the reply table ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARivalsFriendlyEmote_ToAPlayer_GetsRudeOrNothing_NeverAFriendlyAnswer()
    {
        HashSet<uint?> outcomes = Outcomes(PlayerbotStance.Rival, Emotes.Wave, toBot: false);
        Assert.Contains((uint?)null, outcomes);
        Assert.Contains(outcomes, IsRude);
        Assert.All(outcomes, o => Assert.True(o is null || IsRude(o), $"answer {o} is neither nothing nor rude"));
    }

    [Fact]
    public void ARivalsFriendlyEmote_ToABot_GetsAShrugOrNothing()
    {
        HashSet<uint?> outcomes = Outcomes(PlayerbotStance.Rival, Emotes.Wave, toBot: true);
        Assert.Equal(new HashSet<uint?> { null, Emotes.Shrug }, outcomes);
    }

    [Fact]
    public void ARivalsRudeEmote_ToAPlayer_IsAnsweredRudely_Always()
    {
        HashSet<uint?> outcomes = Outcomes(PlayerbotStance.Rival, Emotes.Rude, toBot: false);
        Assert.DoesNotContain((uint?)null, outcomes);
        Assert.All(outcomes, o => Assert.True(IsRude(o), $"answer {o} is not rude"));
    }

    [Fact]
    public void ARivalsRudeEmote_ToABot_IsAShrugOrASigh()
        => Assert.Equal(new HashSet<uint?> { Emotes.Shrug, Emotes.Sigh }, Outcomes(PlayerbotStance.Rival, Emotes.Rude, toBot: true));

    [Fact]
    public void ADislikedBots_FriendlyEmote_GetsAShrugOrNothing_ToPlayersAndBots()
    {
        Assert.Equal(new HashSet<uint?> { null, Emotes.Shrug }, Outcomes(PlayerbotStance.Disliked, Emotes.Wave, toBot: false));
        Assert.Equal(new HashSet<uint?> { null, Emotes.Shrug }, Outcomes(PlayerbotStance.Disliked, Emotes.Wave, toBot: true));
    }

    [Fact]
    public void ADislikedBots_RudeEmote_IsAnsweredRudelyByAPlayer_AndWithAShrugOrASighByABot()
    {
        HashSet<uint?> toPlayer = Outcomes(PlayerbotStance.Disliked, Emotes.Chicken, toBot: false);
        Assert.DoesNotContain((uint?)null, toPlayer);
        Assert.All(toPlayer, o => Assert.True(IsRude(o)));
        Assert.Equal(new HashSet<uint?> { Emotes.Shrug, Emotes.Sigh }, Outcomes(PlayerbotStance.Disliked, Emotes.Chicken, toBot: true));
    }

    [Fact]
    public void AFriendlyBot_AnswersAFriendlyEmoteAsToday_AndARudeOneWithAShrugOrASigh()
    {
        Assert.Subset(TodaysAnswers(Emotes.Wave), Outcomes(PlayerbotStance.Friendly, Emotes.Wave, toBot: false).Select(o => o!.Value).ToHashSet());
        Assert.Equal(TodaysAnswers(Emotes.Wave), Outcomes(PlayerbotStance.Friendly, Emotes.Wave, toBot: false).Select(o => o!.Value).ToHashSet());
        Assert.Equal(new HashSet<uint?> { Emotes.Shrug, Emotes.Sigh }, Outcomes(PlayerbotStance.Friendly, Emotes.Rude, toBot: false));
        Assert.Equal(new HashSet<uint?> { Emotes.Shrug, Emotes.Sigh }, Outcomes(PlayerbotStance.Friendly, Emotes.Rude, toBot: true));
    }

    [Fact]
    public void ANeutralBot_AnswersAFriendlyEmoteAsToday_ToPlayersAndBots()
    {
        HashSet<uint> today = TodaysAnswers(Emotes.Wave);
        Assert.Equal(today, Outcomes(PlayerbotStance.Neutral, Emotes.Wave, toBot: false).Select(o => o!.Value).ToHashSet());
        Assert.Equal(today, Outcomes(PlayerbotStance.Neutral, Emotes.Wave, toBot: true).Select(o => o!.Value).ToHashSet());
    }

    [Fact]
    public void ANeutralBot_AnswersARudeEmoteAsToday_ToAPlayer_AndWithAShrugOrASighToABot()
    {
        // Today a rude gesture gets rude / shrug / sigh back (PlayerbotSocialEmotes.ReplyTo).
        Assert.Equal(TodaysAnswers(Emotes.Rude), Outcomes(PlayerbotStance.Neutral, Emotes.Rude, toBot: false).Select(o => o!.Value).ToHashSet());
        Assert.Equal(new HashSet<uint?> { Emotes.Shrug, Emotes.Sigh }, Outcomes(PlayerbotStance.Neutral, Emotes.Rude, toBot: true));
    }

    [Fact]
    public void ANeutralBot_AnswersABotsRudeEmoteWithAShrugOrASigh_AboutHalfEach()
    {
        // Design 4.2: shrug or sigh. The table's rude pick must not fold into the shrug and leave the sigh a third of the answers.
        List<uint?> answers = [.. Rolls().Select(roll => PlayerbotRivalryRules.ReplyTo(PlayerbotStance.Neutral, Emotes.Rude, toBot: true, roll))];
        Assert.Equal(answers.Count / 2, answers.Count(o => o == Emotes.Shrug));
        Assert.Equal(answers.Count / 2, answers.Count(o => o == Emotes.Sigh));
    }

    [Theory]
    [InlineData(PlayerbotStance.Neutral)]
    [InlineData(PlayerbotStance.Friendly)]
    [InlineData(PlayerbotStance.Disliked)]
    [InlineData(PlayerbotStance.Rival)]
    public void AnEmoteThatIsNeitherFriendlyNorRude_IsAnsweredAsToday_InEveryStance(PlayerbotStance stance)
    {
        // Flex and poke are neutral emotes that get an answer today (applaud or laugh; shrug, sigh or poke); agree gets nothing.
        foreach (uint emote in new[] { Emotes.Flex, Emotes.Poke })
        {
            Assert.Equal(PlayerbotEmoteMood.Neutral, PlayerbotEmoteMoods.Classify(emote));
            Assert.Equal(TodaysAnswers(emote), Outcomes(stance, emote, toBot: false).Select(o => o!.Value).ToHashSet());
        }

        Assert.Equal(PlayerbotEmoteMood.Neutral, PlayerbotEmoteMoods.Classify(Emotes.Agree));
        Assert.Equal(new HashSet<uint?> { null }, Outcomes(stance, Emotes.Agree, toBot: false));
        Assert.Equal(new HashSet<uint?> { null }, Outcomes(stance, Emotes.Agree, toBot: true));
    }

    [Theory]
    [InlineData(PlayerbotStance.Neutral)]
    [InlineData(PlayerbotStance.Friendly)]
    [InlineData(PlayerbotStance.Disliked)]
    [InlineData(PlayerbotStance.Rival)]
    public void AnOptedOutPlayer_GetsNoAnswer_WhateverTheStanceAndTheEmote(PlayerbotStance stance)
    {
        foreach (uint emote in new[] { Emotes.Wave, Emotes.Rude, Emotes.Bow, Emotes.Dance, Emotes.Threaten })
            foreach (double roll in Rolls())
                Assert.Null(PlayerbotRivalryRules.ReplyTo(stance, emote, toBot: false, roll, rudeAtBots: false, optedOut: true));
    }

    [Theory]
    [InlineData(PlayerbotStance.Neutral)]
    [InlineData(PlayerbotStance.Friendly)]
    [InlineData(PlayerbotStance.Disliked)]
    [InlineData(PlayerbotStance.Rival)]
    public void WithRudeAtBotsOff_NothingIsEverRudeToABot_ForEveryEmoteTodaysReplyAnswers(PlayerbotStance stance)
    {
        int answered = 0, shrugged = 0;
        for (uint emote = 0; emote < 400; emote++)
        {
            foreach (double roll in Rolls())
            {
                uint? answer = PlayerbotRivalryRules.ReplyTo(stance, emote, toBot: true, roll, rudeAtBots: false);
                Assert.False(IsRude(answer), $"stance {stance}, emote {emote}, roll {roll}: rude answer {answer} to a bot");
                if (answer is null) continue;
                answered++;
                if (answer == Emotes.Shrug && PlayerbotEmoteMoods.Classify(emote) == PlayerbotEmoteMood.Rude) shrugged++;
            }
        }

        // Non-vacuity: the loop did see answers, and a rude emote at a bot was met with a shrug where today it would be rude (threaten -> chicken).
        Assert.True(answered > 0);
        Assert.True(shrugged > 0);
    }

    [Fact]
    public void TodaysChickenAnswerToAThreat_BecomesAShrug_ToABot()
    {
        // Today threaten is answered with chicken, laugh or flex; chicken is rude, so toward a bot it must be a shrug.
        HashSet<uint?> outcomes = Outcomes(PlayerbotStance.Neutral, Emotes.Threaten, toBot: true);
        Assert.DoesNotContain(outcomes, IsRude);
        Assert.Contains((uint?)Emotes.Shrug, outcomes);
        Assert.Subset(new HashSet<uint?> { Emotes.Shrug, Emotes.Laugh, Emotes.Flex }, outcomes);
    }

    [Fact]
    public void WithRudeAtBotsOn_ARivalMayAnswerABotsRudeEmoteRudely()
    {
        HashSet<uint?> outcomes = Outcomes(PlayerbotStance.Rival, Emotes.Rude, toBot: true, rudeAtBots: true);
        Assert.Contains(outcomes, IsRude);
        // And a neutral bot is back to today's answers, rude ones included.
        Assert.Contains(Outcomes(PlayerbotStance.Neutral, Emotes.Rude, toBot: true, rudeAtBots: true), IsRude);
    }

    // --- rolls ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARoll_IsTheSameInsideAHalfHourBucket_AndInUnitRange()
    {
        const ulong bot = 0x1000_0042UL, other = 0x77;
        long bucketStart = 12 * PlayerbotRivalryRules.BucketMs;
        double first = PlayerbotRivalryRules.GreetRoll(bot, other, bucketStart + 1);
        Assert.Equal(first, PlayerbotRivalryRules.GreetRoll(bot, other, bucketStart + PlayerbotRivalryRules.BucketMs - 1));
        Assert.Equal(first, PlayerbotRivalryRules.GreetRoll(bot, other, bucketStart + 1));
        Assert.True(first >= 0.0 && first < 1.0, $"roll {first} is outside [0, 1)");
        Assert.Equal(30 * 60_000L, PlayerbotRivalryRules.BucketMs);
    }

    [Fact]
    public void ARoll_ChangesFromBucketToBucket_AndWithTheBotAndTheTarget()
    {
        const ulong bot = 0x1000_0042UL, other = 0x77;
        var buckets = new HashSet<double>();
        for (int b = 0; b < 40; b++) buckets.Add(PlayerbotRivalryRules.GreetRoll(bot, other, b * PlayerbotRivalryRules.BucketMs));
        Assert.True(buckets.Count > 30, "rolls barely change between buckets");

        var targets = new HashSet<double>();
        for (ulong o = 0; o < 40; o++) targets.Add(PlayerbotRivalryRules.GreetRoll(bot, o, 0));
        Assert.True(targets.Count > 30, "rolls barely change between targets");

        var bots = new HashSet<double>();
        for (ulong b = 0; b < 40; b++) bots.Add(PlayerbotRivalryRules.GreetRoll(b + 1, other, 0));
        Assert.True(bots.Count > 30, "rolls barely change between bots");
    }

    [Fact]
    public void TheGreetRoll_IsUniform_SoAChanceIsHonoured()
    {
        const int samples = 8000;
        int below = 0;
        for (int i = 0; i < samples; i++)
            if (PlayerbotRivalryRules.GreetRoll(0x1000_0000UL + (ulong)i, 0x2000UL, 0) < 0.25) below++;
        Assert.InRange(below / (double)samples, 0.22, 0.28);
    }

    [Fact]
    public void ThePickRollAndTheGreetRoll_AreIndependentStreams()
    {
        // A target that failed the greet roll must not always get the same emote pick: the two rolls use their own salts.
        int same = 0;
        for (int i = 0; i < 200; i++)
            if (PlayerbotRivalryRules.GreetRoll(0x1000_0000UL + (ulong)i, 5, 0) == PlayerbotRivalryRules.PickRoll(0x1000_0000UL + (ulong)i, 5, 0)) same++;
        Assert.Equal(0, same);
    }

    [Fact]
    public void TheReplyRoll_DependsOnTheEmote_AndIsDeterministic()
    {
        const ulong bot = 0x1000_0042UL, emoter = 0x99;
        Assert.Equal(PlayerbotRivalryRules.ReplyRoll(bot, emoter, Emotes.Wave, 5_000), PlayerbotRivalryRules.ReplyRoll(bot, emoter, Emotes.Wave, 5_000));
        var rolls = new HashSet<double>();
        for (uint emote = 0; emote < 40; emote++) rolls.Add(PlayerbotRivalryRules.ReplyRoll(bot, emoter, emote, 5_000));
        Assert.True(rolls.Count > 30, "the reply roll ignores the emote");
    }

    // --- chances ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheGreetChance_IsTheStrangerChanceHalfAndFullByStance_TimesSociability()
    {
        // Strangers: GreetStrangerChance x sociability; a known neutral 0.5 x sociability; a friend always (4.1).
        Assert.Equal(0.25 * 0.8, PlayerbotRivalryRules.GreetChance(PlayerbotStance.Neutral, known: false, strangerChance: 0.25, sociability: 0.8), 6);
        Assert.Equal(0.5 * 0.8, PlayerbotRivalryRules.GreetChance(PlayerbotStance.Neutral, known: true, strangerChance: 0.25, sociability: 0.8), 6);
        Assert.Equal(1.0, PlayerbotRivalryRules.GreetChance(PlayerbotStance.Friendly, known: true, strangerChance: 0.25, sociability: 0.1), 6);
        Assert.Equal(0.0, PlayerbotRivalryRules.GreetChance(PlayerbotStance.Neutral, known: false, strangerChance: 0.0, sociability: 0.9), 6);
        Assert.Equal(0.9, PlayerbotRivalryRules.GreetChance(PlayerbotStance.Neutral, known: false, strangerChance: 1.0, sociability: 0.9), 6);
    }

    // --- quiet places (capitals, safe areas, fail closed) ----------------------------------------------------------------------------------

    private static AreaTable Areas(params AreaTemplate[] areas) => new(areas, MapRegistry.Default);

    private static AreaTemplate Zone(uint id, uint flags = 0) => new(id, 0, 0, id, flags, 1, "zone " + id, 0, 0);

    private static readonly IReadOnlySet<uint> NoSafeAreas = new HashSet<uint>();

    [Fact]
    public void ACapitalZone_IsQuiet_AnOrdinaryZone_IsNot()
    {
        AreaTable table = Areas(Zone(12), Zone(1519, (uint)AreaFlags.Capital));
        Assert.True(PlayerbotRivalryRules.IsQuietPlace(table, zoneId: 1519, areaId: 1519, NoSafeAreas));
        Assert.False(PlayerbotRivalryRules.IsQuietPlace(table, zoneId: 12, areaId: 12, NoSafeAreas));
    }

    [Fact]
    public void AZoneOfZero_OrAZoneMissingFromTheTable_OrNoTable_FailsClosed()
    {
        AreaTable table = Areas(Zone(12));
        Assert.True(PlayerbotRivalryRules.IsQuietPlace(table, zoneId: 0, areaId: 0, NoSafeAreas));
        Assert.True(PlayerbotRivalryRules.IsQuietPlace(table, zoneId: 4242, areaId: 4242, NoSafeAreas));
        Assert.True(PlayerbotRivalryRules.IsQuietPlace(null, zoneId: 12, areaId: 12, NoSafeAreas));
        Assert.True(PlayerbotRivalryRules.IsQuietPlace(AreaTable.Empty, zoneId: 12, areaId: 12, NoSafeAreas));
    }

    [Fact]
    public void ASafeArea_IsQuiet_EvenInAnOrdinaryZone_AndTheSafeAreaIsMatchedByTheAreaId()
    {
        AreaTable table = Areas(Zone(12));
        var safe = new HashSet<uint> { 35, 392 };
        Assert.True(PlayerbotRivalryRules.IsQuietPlace(table, zoneId: 12, areaId: 35, safe));   // Booty Bay (an area inside the zone)
        Assert.False(PlayerbotRivalryRules.IsQuietPlace(table, zoneId: 12, areaId: 36, safe));  // the next area over
        Assert.False(PlayerbotRivalryRules.IsQuietPlace(table, zoneId: 12, areaId: 12, safe));  // the zone id is not an area id of the list
    }
}
