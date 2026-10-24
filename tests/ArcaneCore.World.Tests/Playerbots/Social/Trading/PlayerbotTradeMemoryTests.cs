using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social.Trading;

/// <summary>
/// P4 S4: the four trade events in the social memory (deltas from the memory options, the trust switch, the daily gain cap, the war layer),
/// and <see cref="PlayerbotSocialMemory.GivingAffinity"/>, the reading every giving decision uses (design 4 and 7, gate test C).
/// </summary>
public sealed class PlayerbotTradeMemoryTests
{
    private const long Day = 86_400_000L;
    private static readonly Guid Bot = Guid.Parse("00000000-0000-0000-0000-0000000000f4");
    private const ulong Other = 4242;

    private sealed class Clock
    {
        // Midnight UTC of some day, so a "day" in the tests is the same UTC day the memory counts.
        public long Now = 20_000 * Day;
    }

    private static (PlayerbotSocialMemory Memory, PlayerbotSocialMemoryOptions Options, PlayerbotWarModeOptions War, Clock Clock) Make()
    {
        var options = new PlayerbotSocialMemoryOptions();
        var war = new PlayerbotWarModeOptions();
        var clock = new Clock();
        var memory = new PlayerbotSocialMemory(options, () => clock.Now, war);
        memory.SetSelf(Bot, 1);
        return (memory, options, war, clock);
    }

    private static PlayerbotRelation Get(PlayerbotSocialMemory memory)
    {
        Assert.True(memory.TryGet(Bot, Other, out PlayerbotRelation relation));
        return relation;
    }

    private static void Repeat(PlayerbotSocialMemory memory, PlayerbotSocialEvent e, int times)
    {
        for (int i = 0; i < times; i++) memory.Note(Bot, 1, Other, false, e);
    }

    [Fact]
    public void TheMemoryOptions_CarryTheDesignsDefaultsForTheTradeEvents()
    {
        var options = new PlayerbotSocialMemoryOptions();
        Assert.Equal((3, 20, -15, -200, 4, -1),
            (options.AffinityPerFairDeal, options.TrustPerFairDeal, options.AffinityPerTradeScam, options.TrustPerTradeScam,
                options.AffinityPerGiftReceived, options.AffinityPerDealFlaked));
    }

    [Fact]
    public void FairDeal_RaisesAffinityAndTrust_CountsOneEncounter_AndIsAnOvertureFromTheOtherSide()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.FairDeal);
        PlayerbotRelation r = Get(memory);
        Assert.Equal((3, 1, 520), (r.Affinity, r.Familiarity, (int)r.TrustPermille));
        Assert.True(r.Flags.HasFlag(PlayerbotRelationFlags.Interacted));
    }

    [Fact]
    public void TradeScam_CostsAffinityAndTrust_AndIsNotAnOverture()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.TradeScam);
        PlayerbotRelation r = Get(memory);
        Assert.Equal((-15, 1, 300), (r.Affinity, r.Familiarity, (int)r.TrustPermille));
        Assert.False(r.Flags.HasFlag(PlayerbotRelationFlags.Interacted));
    }

    [Fact]
    public void GiftReceived_RaisesAffinity_AndLeavesTrustAlone()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.GiftReceived);
        PlayerbotRelation r = Get(memory);
        Assert.Equal((4, 500), (r.Affinity, (int)r.TrustPermille));
        Assert.True(r.Flags.HasFlag(PlayerbotRelationFlags.Interacted));
    }

    [Fact]
    public void DealFlaked_CostsOneAffinity_AndLeavesTrustAlone()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.DealFlaked);
        PlayerbotRelation r = Get(memory);
        Assert.Equal((-1, 500), (r.Affinity, (int)r.TrustPermille));
    }

    [Fact]
    public void TradeCompleted_IsUnchanged_ByTheNewEvents()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.TradeCompleted);
        PlayerbotRelation r = Get(memory);
        Assert.Equal((3, 500), (r.Affinity, (int)r.TrustPermille));
    }

    [Fact]
    public void TheDeltas_ComeFromTheLiveOptions_AtEveryEvent()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, _, _) = Make();
        options.AffinityPerFairDeal = 7;
        options.TrustPerFairDeal = 50;
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.FairDeal);
        Assert.Equal((7, 550), (Get(memory).Affinity, (int)Get(memory).TrustPermille));

        options.AffinityPerTradeScam = -40;
        options.TrustPerTradeScam = -100;
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.TradeScam);
        Assert.Equal((-33, 450), (Get(memory).Affinity, (int)Get(memory).TrustPermille));

        options.AffinityPerGiftReceived = 9;
        options.AffinityPerDealFlaked = -6;
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.GiftReceived);
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.DealFlaked);
        Assert.Equal(-30, Get(memory).Affinity);
    }

    [Fact]
    public void AfterAScam_ThreeFairDealsReachTheLowTrustLine_AndTrustNeverLeavesTheRange()
    {
        // Design 4.4: trust has no drift, recovery is earned: 500 - 200 = 300, then +20 per fair deal: 360 after three (LowTrustPermille is 350).
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.TradeScam);
        Repeat(memory, PlayerbotSocialEvent.FairDeal, 2);
        Assert.Equal(340, Get(memory).TrustPermille);
        Repeat(memory, PlayerbotSocialEvent.FairDeal, 1);
        PlayerbotRelation r = Get(memory);
        Assert.Equal(360, r.TrustPermille);
        Assert.True(r.TrustPermille >= new PlayerbotSocialTradingOptions().LowTrustPermille);
        Assert.Equal(-6, r.Affinity); // -15 + 3 x 3

        Repeat(memory, PlayerbotSocialEvent.TradeScam, 5);
        Assert.Equal(0, Get(memory).TrustPermille); // clamped, never negative
    }

    [Fact]
    public void ManyFairDeals_ClampTrustAtOneThousand()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        Repeat(memory, PlayerbotSocialEvent.FairDeal, 40); // 500 + 40 x 20 = 1300
        Assert.Equal(1000, Get(memory).TrustPermille);
    }

    [Fact]
    public void TheDailyGainCap_AppliesToTheNewGains()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, _, Clock clock) = Make();
        options.MaxDailyAffinityGainPerPair = 5;
        Repeat(memory, PlayerbotSocialEvent.FairDeal, 2); // 3 + 3 capped to 5
        Assert.Equal(5, Get(memory).Affinity);
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.GiftReceived); // nothing left today
        Assert.Equal(5, Get(memory).Affinity);
        Assert.Equal(540, Get(memory).TrustPermille); // trust is not capped by the affinity cap

        clock.Now += Day;
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.GiftReceived); // the next UTC day starts afresh (drift has not yet halved it)
        Assert.True(Get(memory).Affinity > 5);
    }

    [Fact]
    public void WithWarModeOn_TheScamFeedsTheWarLayerHarder_AndTrustStaysNormalOnly()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, _) = Make();
        war.Enabled = true; // NegativeMultiplier 3
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.TradeScam);
        PlayerbotRelation r = Get(memory);
        Assert.Equal(-15, r.Affinity);
        Assert.Equal(-45, r.WarAffinity);
        Assert.Equal(300, r.TrustPermille);
        Assert.Equal(-45, memory.EffectiveAffinity(r)); // restricting reads see the grudge
    }

    [Fact]
    public void SnapshotAndLoad_KeepTheTradeEventsEffect()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.TradeScam);
        Repeat(memory, PlayerbotSocialEvent.FairDeal, 3);
        var restarted = new PlayerbotSocialMemory(new PlayerbotSocialMemoryOptions(), () => 20_000 * Day, new PlayerbotWarModeOptions());
        restarted.Load(memory.Snapshot(Bot));
        Assert.True(restarted.TryGet(Bot, Other, out PlayerbotRelation r));
        Assert.Equal((-6, 360), (r.Affinity, (int)r.TrustPermille));
    }

    // ---- GivingAffinity (design 4 "Affinity reads", 7, B4) ----

    [Fact]
    public void GivingAffinity_IsTheNormalAffinity_WithWarModeOff()
    {
        (PlayerbotSocialMemory memory, _, _, _) = Make();
        Repeat(memory, PlayerbotSocialEvent.GiftReceived, 2);
        PlayerbotRelation r = Get(memory);
        Assert.Equal(8, r.Affinity);
        Assert.Equal(8, memory.GivingAffinity(r));
    }

    [Fact]
    public void GivingAffinity_FollowsAGrudgeDown_WhenTheWarLayerIsLower()
    {
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, _) = Make();
        war.Enabled = true;
        memory.Note(Bot, 7, Other, false, PlayerbotSocialEvent.Kicked); // normal -10, war -30
        PlayerbotRelation r = Get(memory);
        Assert.Equal((-10, -30), (r.Affinity, (int)r.WarAffinity));
        Assert.Equal(-30, memory.EffectiveAffinity(r));
        Assert.Equal(-30, memory.GivingAffinity(r)); // min(-10, -30)
    }

    [Fact]
    public void GateTestC_AWarAffinityClimbingPastTheFriendLine_NeverMakesTheBotMoreGenerous()
    {
        // War Mode on, RemoveCaps on: the war layer is not held by the daily gain cap, the normal layer is.
        (PlayerbotSocialMemory memory, _, PlayerbotWarModeOptions war, _) = Make();
        war.Enabled = true;
        war.RemoveCaps = true;
        Repeat(memory, PlayerbotSocialEvent.HelpedBy, 10); // +8 each, inside one day
        PlayerbotRelation r = Get(memory);
        Assert.Equal(20, r.Affinity); // MaxDailyAffinityGainPerPair
        Assert.True(memory.EffectiveAffinity(r) > 50, "the war layer should have climbed past the friend line");
        Assert.True(memory.EffectiveAffinity(r) > r.Affinity);

        Assert.Equal(r.Affinity, memory.GivingAffinity(r));
        Assert.True(memory.GivingAffinity(r) < 50, "a gift chance, friend discount or gift threshold must not see the war layer's climb");

        // Switching War Mode off instantly leaves the same giving reading (the normal layer never changed).
        war.Enabled = false;
        PlayerbotRelation off = Get(memory);
        Assert.Equal(20, memory.GivingAffinity(off));
        Assert.Equal(20, memory.EffectiveAffinity(off));
    }
}
