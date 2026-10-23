using ArcaneCore.Game;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>Section 12 of the social memory: drift, apology, the daily cap, bad group behaviour, trust and roles, and War Mode.</summary>
public sealed class PlayerbotSocialForgivenessTests
{
    private const long Day = 86_400_000L;
    private static readonly Guid Bot = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private const ulong Other = 42;

    private sealed class Clock
    {
        // Midnight UTC of some day, so a "day" in the tests is the same UTC day the memory counts.
        public long Now = 20_000 * Day;
    }

    private static (PlayerbotSocialMemory Memory, PlayerbotSocialMemoryOptions Options, PlayerbotWarModeOptions War, Clock Clock) Make(bool uncapped = false)
    {
        var options = new PlayerbotSocialMemoryOptions();
        if (uncapped) options.MaxDailyAffinityGainPerPair = 0;
        var war = new PlayerbotWarModeOptions();
        var clock = new Clock();
        var memory = new PlayerbotSocialMemory(options, () => clock.Now, war);
        memory.SetSelf(Bot, 1);
        return (memory, options, war, clock);
    }

    private static PlayerbotRelation Get(PlayerbotSocialMemory memory, ulong other = Other)
    {
        Assert.True(memory.TryGet(Bot, other, out PlayerbotRelation relation));
        return relation;
    }

    private static void Repeat(PlayerbotSocialMemory memory, PlayerbotSocialEvent e, int times, ulong other = Other)
    {
        for (int i = 0; i < times; i++) memory.Note(Bot, 1, other, false, e);
    }

    // ---- 12.1 forgiveness ----

    [Fact]
    public void Affinity_DriftsTowardZero_ByTheHalfLife()
    {
        (PlayerbotSocialMemory memory, _, _, Clock clock) = Make(uncapped: true);
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 5); // +40
        Assert.Equal(40, Get(memory).Affinity);
        clock.Now += 13 * Day;
        Assert.Equal(21, Get(memory).Affinity); // 40 * 2^(-13/14) = 21.0, truncated
        clock.Now += 1 * Day;
        Assert.Equal(20, Get(memory).Affinity); // 14 days: half
        clock.Now += 14 * Day;
        Assert.Equal(10, Get(memory).Affinity);
    }

    [Fact]
    public void Drift_IsFoldedIntoTheStoredValue_OnTheNextWrite_AndInsideADayNothingMoves()
    {
        (PlayerbotSocialMemory memory, _, _, Clock clock) = Make(uncapped: true);
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 5);
        clock.Now += Day - 1;
        Assert.Equal(40, Get(memory).Affinity);
        clock.Now += 14 * Day;
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Grouped); // +3 after the drift to 20
        Assert.Equal(23, Memory(memory).Affinity);

        static PlayerbotRelation Memory(PlayerbotSocialMemory m) => m.Snapshot(Bot).Single();
    }

    [Fact]
    public void ARival_FadesOnItsOwnLongerHalfLife_AndStopsBeingOne_WhenItFadesPastTheThreshold()
    {
        (PlayerbotSocialMemory memory, _, _, Clock clock) = Make(uncapped: true);
        Repeat(memory, PlayerbotSocialEvent.KilledBy, 4); // -80, Rival
        Assert.True(Get(memory).Flags.HasFlag(PlayerbotRelationFlags.Rival));
        clock.Now += 14 * Day;
        PlayerbotRelation r = Get(memory);
        Assert.Equal(-64, r.Affinity); // 80 * 2^(-14/45) = 64.5, not the normal 40
        Assert.True(r.Flags.HasFlag(PlayerbotRelationFlags.Rival));
        clock.Now += 31 * Day; // 45 days in all: half of -80
        r = Get(memory);
        Assert.Equal(-40, r.Affinity);
        Assert.False(r.Flags.HasFlag(PlayerbotRelationFlags.Rival));
    }

    [Fact]
    public void ANonRival_FadesOnTheNormalHalfLife()
    {
        (PlayerbotSocialMemory memory, _, _, Clock clock) = Make(uncapped: true);
        Repeat(memory, PlayerbotSocialEvent.Kicked, 3); // -30
        clock.Now += 14 * Day;
        Assert.Equal(-15, Get(memory).Affinity);
    }

    [Fact]
    public void AnApology_ToABotThatHoldsAGrudge_RaisesAffinity_OncePerPairPerDay()
    {
        (PlayerbotSocialMemory memory, _, _, Clock clock) = Make();
        Repeat(memory, PlayerbotSocialEvent.Kicked, 3); // -30
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Apologized);
        Assert.Equal(-20, Get(memory).Affinity);
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Apologized);
        Assert.Equal(-20, Get(memory).Affinity); // the same day: nothing more
        clock.Now += Day;
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Apologized);
        Assert.Equal(-9, Get(memory).Affinity); // -20 drifted to -19, then +10
    }

    [Fact]
    public void AnApology_FromAStrangerOrToAFriend_ChangesNothing()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Apologized);
        Assert.False(memory.TryGet(Bot, Other, out _));
        memory.Note(Bot, 1, 43, false, PlayerbotSocialEvent.Grouped);
        memory.Note(Bot, 1, 43, false, PlayerbotSocialEvent.Apologized);
        PlayerbotRelation friend = Get(memory, 43);
        Assert.Equal(3, friend.Affinity);
        Assert.Equal(1, friend.Familiarity);
    }

    // ---- 12.2 daily cap ----

    [Fact]
    public void TheDailyCap_DropsGainsBeyondIt_AndResetsWithTheUtcDay()
    {
        (PlayerbotSocialMemory memory, _, _, Clock clock) = Make();
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 4); // 8, 16, 20 (a partial 4), 20
        Assert.Equal(20, Get(memory).Affinity);
        clock.Now += Day; // the next UTC day; the drift costs a point of 20
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.HelpedBy);
        Assert.Equal(27, Get(memory).Affinity);
    }

    [Fact]
    public void TheDailyCap_IsPerPair_AndZeroMeansUnlimited_AndNegativesAreNeverCapped()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, _, _) = Make();
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 4);
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 1, other: 44);
        Assert.Equal(8, Get(memory, 44).Affinity);
        Repeat(memory, PlayerbotSocialEvent.Kicked, 1);
        Assert.Equal(10, Get(memory).Affinity);
        options.MaxDailyAffinityGainPerPair = 0;
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 3);
        Assert.Equal(34, Get(memory).Affinity);
    }


    // ---- 12.3 bad group behaviour ----

    [Theory]
    [InlineData("NinjaLoot", -8)]
    [InlineData("KillSteal", -6)]
    [InlineData("AbandonedRun", -10)]
    public void TheBadBehaviourEvents_ApplyTheirDelta(string eventName, int delta)
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 1, Other, false, Enum.Parse<PlayerbotSocialEvent>(eventName));
        PlayerbotRelation r = Get(memory);
        Assert.Equal(delta, r.Affinity);
        Assert.Equal(1, r.Familiarity);
    }

    [Fact]
    public void TheBadBehaviourOptions_AreRangeChecked()
    {
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { AffinityPerNinjaLoot = 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { AffinityPerKillSteal = -101 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { AffinityPerAbandonedRun = 5 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { AffinityHalfLifeDays = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { RivalHalfLifeDays = 3651 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { AffinityPerApology = 51 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { MaxDailyAffinityGainPerPair = 201 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { TrustPerGroupSuccess = 501 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { TrustPerFirstDeathInWipe = -501 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotWarModeOptions { NegativeMultiplier = 0.5f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotWarModeOptions { NegativeMultiplier = 11f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotWarModeOptions { NegativeMultiplier = float.NaN }.Validate());
        new PlayerbotSocialMemoryOptions().Validate();
        new PlayerbotWarModeOptions().Validate();
        Assert.False(new PlayerbotWarModeOptions().Enabled);
    }

    [Theory]
    [InlineData("sorry about that", true)]
    [InlineData("SORRY!", true)]
    [InlineData("my bad, I pulled too much", true)]
    [InlineData("I apologize", true)]
    [InlineData("many apologies", true)]
    [InlineData("hello there", false)]
    [InlineData("", false)]
    [InlineData("I'm so sorry.", true)]
    [InlineData("sorry! I did not mean it", true)]
    [InlineData("not sorry", false)]
    [InlineData("I'm not even sorry", false)]
    [InlineData("never sorry", false)]
    [InlineData("sorry not sorry", false)]
    [InlineData("Apologies, never apologies", false)]
    [InlineData("sorry, I did not mean it", true)]
    [InlineData("I won't say sorry", false)]
    [InlineData("I don’t apologize", false)]
    [InlineData("sorrycake", false)]
    [InlineData("my badass is back", false)]
    public void TheApologyKeywords_AreMatchedCaseInsensitively(string text, bool expected)
        => Assert.Equal(expected, PlayerbotApology.IsApology(text));

    [Fact]
    public void TheSorryEmote_IsAnApology_NotAMood()
        => Assert.Equal(PlayerbotEmoteMood.Apology, PlayerbotEmoteMoods.Classify(PlayerbotApology.SorryEmote));

    [Theory]
    [InlineData(RollVote.Need, RollVote.Need, false, true)]
    [InlineData(RollVote.Need, RollVote.Need, true, false)] // the winner could use it
    [InlineData(RollVote.Need, RollVote.Greed, false, false)] // won on Greed: not a ninja
    [InlineData(RollVote.Greed, RollVote.Need, false, false)] // the bot did not need it
    [InlineData(RollVote.Pass, RollVote.Need, false, false)]
    public void ANinjaRoll_IsANeedWinOfAnUnusableItem_ThatTheBotAlsoNeeded(RollVote bot, RollVote winner, bool couldUse, bool expected)
        => Assert.Equal(expected, PlayerbotLootRolls.IsNinja(bot, winner, couldUse));

    [Fact]
    public void TheRollWonPacket_IsReadBack()
    {
        byte[] payload = GroupLootPackets.RollWon(ObjectGuid.Player(7), 3, 1234, ObjectGuid.Player(9), 77, RollVote.Need);
        Assert.True(PlayerbotLootRolls.TryReadWon(payload, out PlayerbotLootRolls.RollResult won));
        Assert.Equal((ObjectGuid.Player(7), 3u, 1234u, ObjectGuid.Player(9), RollVote.Need), (won.Source, won.Slot, won.ItemId, won.Winner, won.Vote));
        Assert.False(PlayerbotLootRolls.TryReadWon(payload[..20], out _));
    }

    // ---- 12.4 trust and roles ----

    [Fact]
    public void Trust_RisesOnASuccessfulRun_AndFallsForTheFirstToDieInAWipe_WithinZeroToAThousand()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, _, _) = Make();
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.GroupSucceeded);
        Assert.Equal(530, Get(memory).TrustPermille);
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.DiedFirstInWipe);
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.DiedFirstInWipe);
        Assert.Equal(470, Get(memory).TrustPermille);

        options.TrustPerFirstDeathInWipe = -500;
        Repeat(memory, PlayerbotSocialEvent.DiedFirstInWipe, 2);
        Assert.Equal(0, Get(memory).TrustPermille);
        options.TrustPerGroupSuccess = 500;
        Repeat(memory, PlayerbotSocialEvent.GroupSucceeded, 4);
        Assert.Equal(1000, Get(memory).TrustPermille);
    }

    [Fact]
    public void ThePlayedRole_ReplacesTheOneBefore_AndKeepsTheOtherFlags()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.SetRole(Bot, Other, PlayerbotRelationFlags.PrefersTank); // no relation yet: nothing
        Assert.False(memory.TryGet(Bot, Other, out _));

        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Grouped);
        memory.SetFlags(Bot, Other, PlayerbotRelationFlags.WantsInvites);
        memory.SetRole(Bot, Other, PlayerbotRelationFlags.PrefersTank);
        Assert.Equal(PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.WantsInvites | PlayerbotRelationFlags.PrefersTank, Get(memory).Flags);
        long version = memory.Version(Bot);
        memory.SetRole(Bot, Other, PlayerbotRelationFlags.PrefersTank);
        Assert.Equal(version, memory.Version(Bot));
        memory.SetRole(Bot, Other, PlayerbotRelationFlags.PrefersHealer);
        Assert.Equal(PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.WantsInvites | PlayerbotRelationFlags.PrefersHealer, Get(memory).Flags);
        memory.SetRole(Bot, Other, PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.PrefersDamage); // only the role bits count
        Assert.Equal(PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.WantsInvites | PlayerbotRelationFlags.PrefersDamage, Get(memory).Flags);
    }

    // ---- 12.5 / 13 War Mode: a separate layer ----

    private static PlayerbotRelation Normal(PlayerbotSocialMemory memory, ulong other = Other)
        => memory.Snapshot(Bot).Single(r => r.OtherGuid == other) with { WarAffinity = 0, WarFlags = PlayerbotRelationFlags.None, WarAffinityExact = 0, WarDecayAnchorUnixMs = 0 };

    private static void Script(PlayerbotSocialMemory memory, Clock clock)
    {
        Repeat(memory, PlayerbotSocialEvent.Kicked, 1);
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 4);
        Repeat(memory, PlayerbotSocialEvent.KilledBy, 3);
        Repeat(memory, PlayerbotSocialEvent.Apologized, 2);
        clock.Now += 10 * Day;
        Repeat(memory, PlayerbotSocialEvent.Grouped, 2);
        Repeat(memory, PlayerbotSocialEvent.GroupSucceeded, 1);
        Repeat(memory, PlayerbotSocialEvent.ChatStrike, 1);
    }

    [Fact]
    public void WarMode_NeverTouchesTheNormalLayer_WhateverTheEvents()
    {
        (PlayerbotSocialMemory plain, _, _, Clock plainClock) = Make();
        Script(plain, plainClock);

        (PlayerbotSocialMemory warred, _, PlayerbotWarModeOptions war, Clock warClock) = Make();
        war.Enabled = true;
        Script(warred, warClock);

        Assert.Equal(Normal(plain), Normal(warred)); // affinity, familiarity, trust, flags and times: identical
        PlayerbotRelation layered = warred.Snapshot(Bot).Single();
        Assert.True(layered.WarFlags.HasFlag(PlayerbotRelationFlags.WarSeeded));
        Assert.True(layered.WarAffinity < layered.Affinity); // the war layer holds the grudge harder

        // A run that turns War Mode on part-way and off again also leaves the normal layer alone.
        (PlayerbotSocialMemory partial, _, PlayerbotWarModeOptions partialWar, _) = Make();
        Repeat(partial, PlayerbotSocialEvent.Kicked, 1);
        partialWar.Enabled = true;
        Repeat(partial, PlayerbotSocialEvent.HelpedBy, 4);
        Repeat(partial, PlayerbotSocialEvent.KilledBy, 3);
        partialWar.Enabled = false;
        Repeat(partial, PlayerbotSocialEvent.Apologized, 2);
        (PlayerbotSocialMemory control, _, _, _) = Make();
        Repeat(control, PlayerbotSocialEvent.Kicked, 1);
        Repeat(control, PlayerbotSocialEvent.HelpedBy, 4);
        Repeat(control, PlayerbotSocialEvent.KilledBy, 3);
        Repeat(control, PlayerbotSocialEvent.Apologized, 2);
        Assert.Equal(Normal(control), Normal(partial));
    }

    [Fact]
    public void AWarOnlyApology_LeavesTheNormalLayerAlone_IncludingFamiliarityDecayAndLastSeen()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, Clock clock) = Make();
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 2); // normal 16
        war.Enabled = true;
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Kicked); // normal 6, war -14
        Assert.Equal(6, Get(memory).Affinity);
        Assert.Equal(-14, Get(memory).WarAffinity);
        PlayerbotRelation before = Normal(memory);
        clock.Now += 3 * Day;
        long versionBefore = memory.Version(Bot);

        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Apologized);

        Assert.Equal(before, Normal(memory)); // affinity, familiarity, LastSeen: not a field moved
        Assert.True(memory.Snapshot(Bot).Single().WarAffinity > -14); // the war layer took the apology
        Assert.NotEqual(versionBefore, memory.Version(Bot));
    }

    private static readonly PlayerbotSocialEvent[] AllEvents = Enum.GetValues<PlayerbotSocialEvent>();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ARandomEventSequence_LeavesTheNormalLayerIdentical_WithOrWithoutWarMode(int seed)
    {
        (PlayerbotSocialMemory plain, PlayerbotSocialMemoryOptions plainOptions, _, Clock plainClock) = Make();
        (PlayerbotSocialMemory warred, PlayerbotSocialMemoryOptions warOptions, PlayerbotWarModeOptions war, Clock warClock) = Make();
        plainOptions.MaxRelationsPerBot = warOptions.MaxRelationsPerBot = 16; // far fewer places than characters: the eviction runs all the time
        var random = new Random(seed);
        for (int step = 0; step < 400; step++)
        {
            // Both runs see the same events at the same times; only the war run has War Mode, switched on and off at random.
            war.Enabled = random.Next(3) != 0;
            long advance = random.Next(4) switch { 0 => 0, 1 => 60_000L * random.Next(1, 120), 2 => Day / 2 + random.Next(1, 1000), _ => Day * random.Next(1, 6) };
            plainClock.Now += advance;
            warClock.Now += advance;
            ulong other = (ulong)random.Next(2, 2 + warOptions.MaxRelationsPerBot + 40);
            PlayerbotSocialEvent e = AllEvents[random.Next(AllEvents.Length)];
            plain.Note(Bot, 1, other, false, e);
            warred.Note(Bot, 1, other, false, e);
            Assert.Equal(NormalView(plain), NormalView(warred));
        }

        war.Enabled = false;
        Assert.Equal(16, plain.Snapshot(Bot).Count); // the eviction really ran
        foreach (PlayerbotRelation r in plain.Snapshot(Bot))
        {
            Assert.True(warred.TryGet(Bot, r.OtherGuid, out PlayerbotRelation w));
            Assert.True(plain.TryGet(Bot, r.OtherGuid, out PlayerbotRelation p));
            Assert.Equal(p with { WarAffinity = 0, WarFlags = PlayerbotRelationFlags.None, WarAffinityExact = 0, WarDecayAnchorUnixMs = 0 }, w with { WarAffinity = 0, WarFlags = PlayerbotRelationFlags.None, WarAffinityExact = 0, WarDecayAnchorUnixMs = 0 });
        }

        Assert.Equal(plain.Snapshot(Bot).Select(r => r.OtherGuid), warred.Snapshot(Bot).Where(r => !r.Flags.HasFlag(PlayerbotRelationFlags.WarOnly)).Select(r => r.OtherGuid));
    }

    // The normal layer as the stored rows show it, field by field: the war-only rows are not part of it and the war fields are not.
    private static IEnumerable<PlayerbotRelation> NormalView(PlayerbotSocialMemory memory)
        => memory.Snapshot(Bot).Where(r => !r.Flags.HasFlag(PlayerbotRelationFlags.WarOnly))
            .Select(r => r with { WarAffinity = 0, WarFlags = PlayerbotRelationFlags.None, WarAffinityExact = 0, WarDecayAnchorUnixMs = 0 });

    [Fact]
    public void AnApologisingLine_IsRecordedBeforeItsChatExchange_SoTheExchangesAffinityCannotCancelTheGrudge()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, _, _) = Make();
        options.AffinityPerChat = 2;
        options.AffinityPerKick = -2;
        var recorder = new PlayerbotSocialRecorder(memory, NullLogger.Instance);
        ObjectGuid sender = ObjectGuid.Player(77);
        memory.Note(Bot, 1, sender.Value, false, PlayerbotSocialEvent.Kicked); // a grudge of exactly what one chat exchange is worth
        Assert.Equal(-2, Get(memory, sender.Value).Affinity);

        recorder.NoteChatLine(Bot, ObjectGuid.Player(1), sender, "sorry about that");

        Assert.Equal(options.AffinityPerApology, Get(memory, sender.Value).Affinity); // -2, the apology, the exchange's +2
        recorder.NoteChatLine(Bot, ObjectGuid.Player(1), ObjectGuid.Player(78), "sorry not sorry"); // no apology, and no relation from nothing
        Assert.True(memory.TryGet(Bot, ObjectGuid.Player(78).Value, out PlayerbotRelation mocking));
        Assert.Equal(options.AffinityPerChat, mocking.Affinity);
    }

    [Fact]
    public void TheDormantWarLayer_KeepsDriftingByTheClock_PositiveAffinityAlways_NegativeOnlyWithNeverForgetOff()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, Clock clock) = Make(uncapped: true);
        war.Enabled = true;
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 5); // war +40
        Repeat(memory, PlayerbotSocialEvent.Kicked, 1, other: 60); // war -30
        Assert.Equal(40, Get(memory).WarAffinity);
        Assert.Equal(-30, Get(memory, 60).WarAffinity);

        war.Enabled = false; // dormant for a half-life (14 days)
        clock.Now += 14 * Day;
        war.Enabled = true;
        Assert.Equal(20, Get(memory).WarAffinity); // not frozen at 40
        Assert.Equal(-30, Get(memory, 60).WarAffinity); // NeverForget keeps the grudge
        war.NeverForget = false;
        Assert.Equal(-15, Get(memory, 60).WarAffinity); // off: it drifted like any other
    }

    // ---- decay keeps its time ----

    [Fact]
    public void ADailyContact_KeepsItsFamiliarity_InsteadOfFreezingAtOne()
    {
        (PlayerbotSocialMemory memory, _, _, Clock clock) = Make();
        for (int i = 0; i < 10; i++)
        {
            memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Grouped);
            clock.Now += Day;
        }

        Assert.True(Get(memory).Familiarity >= 3, $"familiarity {Get(memory).Familiarity}");
    }

    [Theory]
    [InlineData(1, 14, 40)]
    [InlineData(1, 28, 20)]
    [InlineData(6, 14, 40)]
    [InlineData(24, 28, 20)]
    public void Affinity_HalvesOnItsConfiguredHalfLife_HoweverOftenTheEntryIsWritten(int writesPerDay, int days, int expected)
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, _, Clock clock) = Make(uncapped: true);
        options.AffinityPerChat = 0;
        options.ChatFamiliarityCooldownMinutes = 0;
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 10); // 80
        for (int i = 0; i < days * writesPerDay; i++)
        {
            clock.Now += Day / writesPerDay;
            memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.ChatExchange);
        }

        int value = Get(memory).Affinity;
        Assert.InRange(value, (int)Math.Floor(expected * 0.95), (int)Math.Ceiling(expected * 1.05));
        Assert.InRange(memory.Snapshot(Bot).Single().Affinity, (int)Math.Floor(expected * 0.95), (int)Math.Ceiling(expected * 1.05));
    }

    [Fact]
    public void Top_OrdersByTheAffinityDecisionsRead_AndTheMemoryHasItsOwnClock()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, Clock clock) = Make(uncapped: true);
        Assert.Equal(clock.Now, memory.NowMs);
        // Both end with familiarity 2 and normal affinity -7; the war layer holds 10 at -27 and 11 at -7.
        memory.Note(Bot, 1, 10, false, PlayerbotSocialEvent.TradeCompleted); // +3
        memory.Note(Bot, 1, 11, false, PlayerbotSocialEvent.Kicked); // -10
        war.Enabled = true;
        memory.Note(Bot, 1, 10, false, PlayerbotSocialEvent.Kicked); // normal -7, war seeded at 3 then -30
        memory.Note(Bot, 1, 11, false, PlayerbotSocialEvent.TradeCompleted); // normal -7, war -10 + 3
        Assert.Equal(new ulong[] { 11, 10 }, memory.Top(Bot, 2).Select(r => r.OtherGuid)); // war on: the war layer decides
        war.Enabled = false;
        Assert.Equal(new ulong[] { 10, 11 }, memory.Top(Bot, 2).Select(r => r.OtherGuid)); // a tie in the normal layer: the lower guid first
    }

    [Fact]
    public void TheWarLayer_IsSeededFromTheNormalAffinity_TheFirstTime()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, _) = Make();
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 2); // 16 in the normal layer
        war.Enabled = true;
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Kicked);
        PlayerbotRelation r = Get(memory);
        Assert.Equal(6, r.Affinity);
        Assert.Equal(-14, r.WarAffinity); // 16, then -10 x 3
    }

    [Fact]
    public void Decisions_ReadTheWarLayerOnlyWhileWarModeIsOn_AndTheDormantLayerComesBack()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, _) = Make();
        war.Enabled = true;
        Repeat(memory, PlayerbotSocialEvent.KilledBy, 1); // normal -20, war -60: a war Rival only
        PlayerbotRelation r = Get(memory);
        Assert.Equal(-60, memory.EffectiveAffinity(r));
        Assert.True(memory.IsRival(r));
        Assert.False(r.Flags.HasFlag(PlayerbotRelationFlags.Rival));

        war.Enabled = false;
        Assert.Equal(-20, memory.EffectiveAffinity(r));
        Assert.False(memory.IsRival(r));
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 1); // normal moves, the dormant layer does not
        PlayerbotRelation off = Get(memory);
        Assert.Equal(-12, memory.EffectiveAffinity(off));
        Assert.Equal(-60, off.WarAffinity);

        war.Enabled = true;
        Assert.Equal(-60, memory.EffectiveAffinity(Get(memory)));
        Assert.True(memory.IsRival(Get(memory)));
    }

    [Fact]
    public void ClearingTheWarLayer_WipesOnlyTheWarLayer_AndBumpsTheVersion()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, _) = Make();
        war.Enabled = true;
        Repeat(memory, PlayerbotSocialEvent.KilledBy, 1);
        PlayerbotRelation before = Normal(memory);
        long version = memory.Version(Bot);
        Assert.True(memory.ClearWarLayers());
        Assert.True(memory.Version(Bot) > version);
        Assert.Equal(before, Normal(memory));
        PlayerbotRelation r = memory.Snapshot(Bot).Single();
        Assert.Equal((short)0, r.WarAffinity);
        Assert.Equal(PlayerbotRelationFlags.None, r.WarFlags);
        Assert.False(memory.ClearWarLayers());
        Assert.False(new PlayerbotWarModeOptions().ClearOnDisable);
    }

    [Fact]
    public void BothLayers_SurviveAPersistenceRoundTrip()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, Clock clock) = Make();
        war.Enabled = true;
        Script(memory, clock);
        PlayerbotRelation[] stored = [.. memory.Snapshot(Bot)];
        (PlayerbotSocialMemory loaded, _, _, _) = Make();
        loaded.Load(stored);
        Assert.Equal(stored, loaded.Snapshot(Bot));
        Assert.NotEqual((short)0, stored[0].WarAffinity);
        Assert.True(stored[0].WarFlags.HasFlag(PlayerbotRelationFlags.WarSeeded));
    }

    [Fact]
    public void WarMode_NegativeMultiplier_ScalesNegativeWarDeltasOnly()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, _) = Make();
        war.Enabled = true;
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.Kicked);
        Assert.Equal(-30, Get(memory).WarAffinity);
        Assert.Equal(-10, Get(memory).Affinity);
        memory.Note(Bot, 1, Other, false, PlayerbotSocialEvent.HelpedBy);
        Assert.Equal(-22, Get(memory).WarAffinity); // +8, untouched
        war.NegativeMultiplier = 1.5f;
        memory.Note(Bot, 1, 50, false, PlayerbotSocialEvent.Kicked);
        Assert.Equal(-15, Get(memory, 50).WarAffinity);
    }

    [Fact]
    public void WarMode_NeverForget_StopsNegativeWarDrift_ButNotThePositiveOne_WhileTheNormalLayerForgives()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, Clock clock) = Make(uncapped: true);
        war.Enabled = true;
        Repeat(memory, PlayerbotSocialEvent.KilledBy, 1); // war -60, Rival; normal -20
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 5, other: 60);
        clock.Now += 200 * Day;
        PlayerbotRelation grudge = Get(memory);
        Assert.Equal(-60, grudge.WarAffinity);
        Assert.True(grudge.WarFlags.HasFlag(PlayerbotRelationFlags.Rival));
        Assert.Equal(0, grudge.Affinity); // the normal layer forgave long ago
        Assert.True(Get(memory, 60).WarAffinity < 1);

        war.NeverForget = false;
        Assert.Equal(-2, Get(memory).WarAffinity); // 200 days on the Rival half-life: the grudge fades once NeverForget is off
        Assert.False(Get(memory).WarFlags.HasFlag(PlayerbotRelationFlags.Rival));
    }

    [Fact]
    public void WarMode_RemoveCaps_LiftsTheWarDailyCap_AndTheWarApologyLimit_ButNotTheNormalOnes()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, _) = Make();
        war.Enabled = true;
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 4);
        Assert.Equal(20, Get(memory).Affinity);
        Assert.Equal(32, Get(memory).WarAffinity);

        Repeat(memory, PlayerbotSocialEvent.Kicked, 2, other: 45); // normal -20, war -60
        Repeat(memory, PlayerbotSocialEvent.Apologized, 3, other: 45);
        PlayerbotRelation sorry = Get(memory, 45);
        Assert.Equal(-10, sorry.Affinity); // once
        Assert.Equal(-30, sorry.WarAffinity); // three times

        war.RemoveCaps = false;
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 4, other: 46);
        Assert.Equal(20, Get(memory, 46).WarAffinity);
    }

    [Fact]
    public void WarMode_RemoveCaps_KeepsEveryWarGrudge_WhenTheMemoryIsFull_OnlyWhileOn()
    {
        PlayerbotSocialMemory Fill(bool warOn)
        {
            (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, PlayerbotWarModeOptions war, Clock clock) = Make();
            war.Enabled = true; // the layer is fed either way; only reading it for protection depends on the toggle below
            options.MaxRelationsPerBot = 16;
            memory.Note(Bot, 1, 1000, false, PlayerbotSocialEvent.Kicked);
            clock.Now += 60_000;
            for (ulong guid = 10; guid <= 24; guid++)
            {
                Repeat(memory, PlayerbotSocialEvent.Grouped, 2, guid);
                clock.Now += 60_000;
            }

            war.Enabled = warOn;
            memory.Note(Bot, 1, 2000, false, PlayerbotSocialEvent.Grouped);
            Assert.Equal(16, memory.Snapshot(Bot).Count(r => !r.Flags.HasFlag(PlayerbotRelationFlags.WarOnly)));
            return memory;
        }

        // The normal rules decide alone: the least familiar entry (1000, met once) leaves the normal layer with War Mode on or off.
        PlayerbotSocialMemory on = Fill(true);
        PlayerbotSocialMemory off = Fill(false);
        Assert.Equal(NormalView(off).Select(r => r.OtherGuid), NormalView(on).Select(r => r.OtherGuid));
        Assert.DoesNotContain(1000UL, NormalView(on).Select(r => r.OtherGuid));
        Assert.Equal(16, NormalView(on).Count());
        Assert.Contains(10UL, NormalView(on).Select(r => r.OtherGuid)); // nobody else was dropped to make room
        Assert.False(off.TryGet(Bot, 1000, out _));
        Assert.DoesNotContain(off.Snapshot(Bot), r => r.OtherGuid == 1000); // no war grudge to keep with War Mode off: gone outright

        // The war layer kept its grudge as a war-only row: visible to a war read only.
        PlayerbotRelation kept = Assert.Single(on.Snapshot(Bot), r => r.OtherGuid == 1000);
        Assert.True(kept.Flags.HasFlag(PlayerbotRelationFlags.WarOnly));
        Assert.True(kept.WarAffinity < 0);
        Assert.Equal(0, kept.Affinity);
        Assert.Equal(0, kept.Familiarity);
    }

    [Fact]
    public void AWarOnlyRelation_IsAbsentFromEveryNormalRead_VisibleToWarReads_AndStartsFreshOnANormalEvent()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, PlayerbotWarModeOptions war, Clock clock) = Make();
        war.Enabled = true;
        options.MaxRelationsPerBot = 16;
        memory.Note(Bot, 1, 1000, false, PlayerbotSocialEvent.Kicked);
        for (ulong guid = 10; guid <= 25; guid++)
        {
            clock.Now += 60_000;
            Repeat(memory, PlayerbotSocialEvent.Grouped, 2, guid);
        }

        Assert.True(memory.Snapshot(Bot).Single(r => r.OtherGuid == 1000).Flags.HasFlag(PlayerbotRelationFlags.WarOnly));
        Assert.True(memory.TryGet(Bot, 1000, out PlayerbotRelation seen)); // War Mode on: a war read sees it
        Assert.True(seen.WarAffinity < 0);
        Assert.Contains(1000UL, memory.Top(Bot, 100).Select(r => r.OtherGuid));

        war.Enabled = false;
        Assert.False(memory.TryGet(Bot, 1000, out _));
        Assert.DoesNotContain(1000UL, memory.Top(Bot, 100).Select(r => r.OtherGuid));
        memory.SetFlags(Bot, 1000, PlayerbotRelationFlags.WantsInvites); // no normal relation to set it on
        memory.SetTopic(Bot, 1000, "topic", "summary");
        Assert.Equal(PlayerbotRelationFlags.WarOnly, memory.Snapshot(Bot).Single(r => r.OtherGuid == 1000).Flags);

        // Load takes the flag back: the row is still war-only after a restart.
        var reloaded = new PlayerbotSocialMemory(options, () => clock.Now, war);
        reloaded.Load(memory.SnapshotAll());
        Assert.False(reloaded.TryGet(Bot, 1000, out _));
        war.Enabled = true;
        Assert.True(reloaded.TryGet(Bot, 1000, out _));
        war.Enabled = false;

        // A normal event: a brand-new relation, as if it had never been remembered (and the war layer keeps what it held).
        clock.Now += 60_000;
        memory.Note(Bot, 1, 1000, false, PlayerbotSocialEvent.TradeCompleted);
        Assert.True(memory.TryGet(Bot, 1000, out PlayerbotRelation fresh));
        Assert.Equal(1, fresh.Familiarity);
        Assert.Equal(options.AffinityPerTrade, fresh.Affinity);
        Assert.False(fresh.Flags.HasFlag(PlayerbotRelationFlags.WarOnly));
        Assert.True(fresh.WarAffinity < 0 || fresh.WarFlags.HasFlag(PlayerbotRelationFlags.WarSeeded));
    }

    [Fact]
    public void WarOnlyRelations_AreBoundedByFourTimesTheLimit_OldestFirst_AndClearOnDisableDeletesThem()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, PlayerbotWarModeOptions war, Clock clock) = Make();
        war.Enabled = true;
        options.MaxRelationsPerBot = 16;
        for (ulong guid = 100; guid < 100 + 80; guid++) // every one a grudge, so every eviction leaves a war-only row
        {
            clock.Now += 60_000;
            memory.Note(Bot, 1, guid, false, PlayerbotSocialEvent.Kicked);
        }

        IReadOnlyList<PlayerbotRelation> rows = memory.Snapshot(Bot);
        Assert.Equal(64, rows.Count); // 16 normal + 48 war-only
        Assert.Equal(16, rows.Count(r => !r.Flags.HasFlag(PlayerbotRelationFlags.WarOnly)));
        Assert.DoesNotContain(rows, r => r.OtherGuid is 100 or 115); // the oldest war-only ones went
        Assert.Contains(rows, r => r.OtherGuid == 179);

        Assert.True(memory.ClearWarLayers());
        Assert.Equal(16, memory.Snapshot(Bot).Count);
        Assert.DoesNotContain(memory.Snapshot(Bot), r => r.Flags.HasFlag(PlayerbotRelationFlags.WarOnly));
    }
}
