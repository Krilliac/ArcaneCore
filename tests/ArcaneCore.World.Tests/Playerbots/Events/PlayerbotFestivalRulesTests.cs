using ArcaneCore.World.Playerbots.Population;
using System.Numerics;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Events;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// The pure festival rules (P7a H2 and H3, design 4.2): which festival NPCs are anchors, the spot on the 8 to 12 yard ring, the
/// celebrate roll with its staggered day, and the height rule for a spot. No world, no clock.
/// </summary>
public sealed class PlayerbotFestivalRulesTests
{
    private const float Radius = 250f;

    private static PlayerbotAnchorKind? Kind(PlayerbotAnchorFacts facts, float radius = Radius) => PlayerbotFestivalRules.AnchorKind(facts, radius);

    // --- anchors ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AGossipQuestOrVendorNpc_IsAnAnchor_WithTheKindTheDesignGivesIt()
    {
        Assert.Equal(PlayerbotAnchorKind.Gossip, Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip }));
        Assert.Equal(PlayerbotAnchorKind.Service, Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.QuestGiver }));
        Assert.Equal(PlayerbotAnchorKind.Service, Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Vendor }));
        // A quest giver that also talks is a service anchor (the stricter cap), not a gossip one.
        Assert.Equal(PlayerbotAnchorKind.Service, Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip | NpcFlags.QuestGiver }));
        Assert.Equal(PlayerbotAnchorKind.Service, Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip | NpcFlags.Vendor }));
        Assert.Equal(PlayerbotAnchorKind.Service, Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.QuestGiver | NpcFlags.Vendor | NpcFlags.Repair }));
    }

    [Theory]
    [InlineData(NpcFlags.None)]
    [InlineData(NpcFlags.FlightMaster)]
    [InlineData(NpcFlags.Trainer)]
    [InlineData(NpcFlags.Innkeeper)]
    [InlineData(NpcFlags.Banker)]
    [InlineData(NpcFlags.Auctioneer)]
    [InlineData(NpcFlags.Repair)]
    public void ANpcWithNoGossipQuestOrVendorFlag_IsNotAnAnchor(NpcFlags flags)
        => Assert.Null(Kind(new PlayerbotAnchorFacts { NpcFlags = flags }));

    private static readonly (string Why, PlayerbotAnchorFacts Facts)[] RejectedFacts =
    [
        ("not selectable (a spotlight or credit marker)", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, NotSelectable = true }),
        ("invisible (a trigger)", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, Behaviour = CreatureBehaviourFlags.Invisible }),
        ("a guard", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, Behaviour = CreatureBehaviourFlags.Guard }),
        ("invisible among other flags", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, Behaviour = CreatureBehaviourFlags.NoAggro | CreatureBehaviourFlags.Invisible }),
        ("absent (its event is off)", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, Present = false }),
        ("outside the home", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, InsideHome = false }),
        ("beyond the radius", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, DistanceFromTownYards = Radius + 0.5f }),
        ("hostile to the team", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, Hostile = true }),
        ("not approachable by the team", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, Approachable = false }),
        ("PvP enforced on the team", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, PvpEnforced = true }),
        ("inside a GM scenario area", new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, InsideScenarioArea = true }),
    ];

    public static TheoryData<int> Rejected()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < RejectedFacts.Length; i++) data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void AnNpcThatIsAnyOfThese_IsNotAnAnchor(int index)
    {
        (string why, PlayerbotAnchorFacts facts) = RejectedFacts[index];
        Assert.True(Kind(facts) is null, why);
        // The same NPC with a quest flag instead is rejected for the same reason: the flag is not what decides.
        Assert.Null(Kind(facts with { NpcFlags = NpcFlags.QuestGiver }));
        Assert.NotNull(why);
    }

    [Fact]
    public void TheRadius_IsInclusive_AndCountsFromTheTownCentre()
    {
        Assert.NotNull(Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, DistanceFromTownYards = Radius }));
        Assert.NotNull(Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, DistanceFromTownYards = 0f }));
        Assert.Null(Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, DistanceFromTownYards = Radius + 0.01f }));
        Assert.NotNull(Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, DistanceFromTownYards = 20f }, radius: 20f));
        Assert.Null(Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, DistanceFromTownYards = 20.5f }, radius: 20f));
        Assert.NotNull(Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, DistanceFromTownYards = 1000f }, radius: 1000f));
        Assert.Null(Kind(new PlayerbotAnchorFacts { NpcFlags = NpcFlags.Gossip, DistanceFromTownYards = float.NaN }));
    }

    // --- the ring --------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRingConstants_AreOutsideTheInteractionRange()
    {
        Assert.Equal(8f, PlayerbotFestivalRules.RingMinYards);
        Assert.Equal(12f, PlayerbotFestivalRules.RingMaxYards);
        Assert.Equal(5f, PlayerbotFestivalRules.SpotZToleranceYards);
        Assert.True(PlayerbotFestivalRules.RingMinYards > 5f, "the ring must stay outside the 5 yard NPC interaction distance");
    }

    [Fact]
    public void ARingPoint_IsEightToTwelveYardsFromTheAnchor_OnTheAnchorsHeight()
    {
        var anchor = new Vector3(-8845.1f, 640.4f, 94.2f);
        for (ulong bot = 1; bot <= 256; bot++)
        {
            Vector3 point = PlayerbotFestivalRules.RingPoint(bot, 15310, anchor);
            float distance2D = MathF.Sqrt(((point.X - anchor.X) * (point.X - anchor.X)) + ((point.Y - anchor.Y) * (point.Y - anchor.Y)));

            Assert.InRange(distance2D, PlayerbotFestivalRules.RingMinYards - 0.001f, PlayerbotFestivalRules.RingMaxYards + 0.001f);
            Assert.Equal(anchor.Z, point.Z);
        }
    }

    [Fact]
    public void ARingPoint_IsTheSameForTheSameBotAndAnchor_AndDiffersBetweenBots()
    {
        var anchor = new Vector3(100f, -200f, 30f);

        Assert.Equal(PlayerbotFestivalRules.RingPoint(42, 7, anchor), PlayerbotFestivalRules.RingPoint(42, 7, anchor));
        Assert.NotEqual(PlayerbotFestivalRules.RingPoint(42, 7, anchor), PlayerbotFestivalRules.RingPoint(42, 8, anchor)); // another anchor, another point

        HashSet<Vector3> points = [.. Enumerable.Range(1, 64).Select(bot => PlayerbotFestivalRules.RingPoint((ulong)bot, 7, anchor))];
        Assert.True(points.Count >= 60, $"64 bots shared only {points.Count} ring points");
    }

    [Fact]
    public void TheRingPoints_SpreadAroundTheAnchor_NotInOneDirection()
    {
        var anchor = new Vector3(0f, 0f, 0f);
        var quadrants = new HashSet<int>();
        for (ulong bot = 1; bot <= 256; bot++)
        {
            Vector3 point = PlayerbotFestivalRules.RingPoint(bot, 99, anchor);
            quadrants.Add((point.X >= 0 ? 1 : 0) + (point.Y >= 0 ? 2 : 0));
        }

        Assert.Equal(4, quadrants.Count);
    }

    // --- the celebrate roll ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheProbability_IsExactAtTheEnds_AndTheChanceAtMidSociability()
    {
        foreach (double sociability in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            Assert.Equal(0.0, PlayerbotFestivalRules.CelebrateProbability(0, sociability));
            Assert.Equal(1.0, PlayerbotFestivalRules.CelebrateProbability(100, sociability));
        }

        foreach (int chance in new[] { 1, 10, 33, 60, 99 })
            Assert.Equal(chance / 100.0, PlayerbotFestivalRules.CelebrateProbability(chance, 0.5), 12);
    }

    [Fact]
    public void TheProbability_AtSixtyPercent_RunsFromAboutThirtySevenToSeventyFivePercentOverTheTrait()
    {
        // p = 1 - (1 - c)^(0.5 + S): 1 - 0.4^0.5 at S = 0 and 1 - 0.4^1.5 at S = 1 (the design quotes 0.37 and 0.75).
        double low = PlayerbotFestivalRules.CelebrateProbability(60, 0.0);
        double high = PlayerbotFestivalRules.CelebrateProbability(60, 1.0);

        Assert.Equal(1.0 - Math.Pow(0.4, 0.5), low, 12);
        Assert.Equal(1.0 - Math.Pow(0.4, 1.5), high, 12);
        Assert.InRange(low, 0.365, 0.375);
        Assert.InRange(high, 0.745, 0.755);

        double previous = -1;
        for (int step = 0; step <= 20; step++)
        {
            double p = PlayerbotFestivalRules.CelebrateProbability(60, step / 20.0);
            Assert.True(p > previous, "the probability must rise with sociability");
            previous = p;
        }
    }

    [Fact]
    public void TheRoll_IsInUnitRange_StableForABotRootAndDay_AndFollowsPlayerbotContentionMix()
    {
        const int count = 512;
        var rolls = new List<(ulong Raw, double Roll)>();
        for (ulong guid = 1; guid <= count; guid++)
        {
            double roll = PlayerbotFestivalRules.Roll(guid, 12, 20_000);
            Assert.InRange(roll, 0.0, 1.0);
            Assert.True(roll < 1.0);
            Assert.Equal(roll, PlayerbotFestivalRules.Roll(guid, 12, 20_000));
            rolls.Add((PlayerbotContention.Mix(PlayerbotContention.Mix(guid, 12), 20_000), roll));
        }

        // Whatever the mapping to 0..1, it is monotone in the hash the design names (Mix(Mix(guid, root), day)): never string.GetHashCode.
        double last = -1;
        foreach ((ulong _, double roll) in rolls.OrderBy(r => r.Raw))
        {
            Assert.True(roll >= last, "the roll must be an order-preserving map of PlayerbotContention.Mix(Mix(guid, root), day)");
            last = roll;
        }

        // A new day, a new festival and a new bot each roll afresh.
        Assert.Contains(Enumerable.Range(1, 64), g => PlayerbotFestivalRules.Roll((ulong)g, 12, 20_000) != PlayerbotFestivalRules.Roll((ulong)g, 12, 20_001));
        Assert.Contains(Enumerable.Range(1, 64), g => PlayerbotFestivalRules.Roll((ulong)g, 12, 20_000) != PlayerbotFestivalRules.Roll((ulong)g, 2, 20_000));
        Assert.True(rolls.Select(r => r.Roll).Distinct().Count() > count - 4);
    }

    [Fact]
    public void ZeroPercentCelebratesNever_AndHundredPercentEveryBot_OverTwoHundredFiftySixGuids()
    {
        foreach (double sociability in new[] { 0.0, 0.5, 1.0 })
        {
            int never = 0, always = 0;
            for (ulong guid = 1; guid <= 256; guid++)
            {
                if (PlayerbotFestivalRules.Celebrates(guid, 2, 20_000, 0, sociability)) never++;
                if (PlayerbotFestivalRules.Celebrates(guid, 2, 20_000, 100, sociability)) always++;
            }

            Assert.Equal(0, never);
            Assert.Equal(256, always);
        }
    }

    [Fact]
    public void SixtyPercent_RisesWithSociability_PerBot_AndAveragesTheChanceAtHalf()
    {
        const int count = 4096;
        int atHalf = 0;
        for (ulong guid = 1; guid <= count; guid++)
        {
            bool low = PlayerbotFestivalRules.Celebrates(guid, 12, 20_100, 60, 0.0);
            bool mid = PlayerbotFestivalRules.Celebrates(guid, 12, 20_100, 60, 0.5);
            bool high = PlayerbotFestivalRules.Celebrates(guid, 12, 20_100, 60, 1.0);
            Assert.True(!low || mid, "a bot that celebrates at low sociability must celebrate at middle");
            Assert.True(!mid || high, "a bot that celebrates at middle sociability must celebrate at high");
            if (mid) atHalf++;
        }

        // The standard error of 4096 draws at p = 0.6 is 0.0077: 0.035 is more than four of them.
        Assert.InRange(atHalf / (double)count, 0.60 - 0.035, 0.60 + 0.035);
    }

    // --- the staggered day -----------------------------------------------------------------------------------------------

    private static long ChangeInstant(ulong guid, long from)
    {
        long low = from, high = from + 86_400;
        long dayAtLow = PlayerbotFestivalRules.StaggeredDay(guid, low);
        Assert.Equal(dayAtLow + 1, PlayerbotFestivalRules.StaggeredDay(guid, high));
        while (high - low > 1)
        {
            long mid = low + ((high - low) / 2);
            if (PlayerbotFestivalRules.StaggeredDay(guid, mid) == dayAtLow) low = mid;
            else high = mid;
        }

        return high;
    }

    [Fact]
    public void TheStaggeredDay_ChangesOnceInEveryEightySixThousandFourHundredSeconds_AtAnInstantOfTheBotsOwn()
    {
        const long start = 1_800_000_000;
        foreach (ulong guid in new ulong[] { 1, 2, 77, 123_456 })
        {
            long change = ChangeInstant(guid, start);
            long day = PlayerbotFestivalRules.StaggeredDay(guid, change);

            Assert.Equal(day - 1, PlayerbotFestivalRules.StaggeredDay(guid, change - 1));
            Assert.Equal(day, PlayerbotFestivalRules.StaggeredDay(guid, change + 86_399));
            Assert.Equal(day + 1, PlayerbotFestivalRules.StaggeredDay(guid, change + 86_400));
            Assert.Equal(day, PlayerbotFestivalRules.StaggeredDay(guid, change + 43_200));

            // It is a day count like the UTC day: a bot's day is within one of floor(unix / 86400).
            Assert.InRange(day - (change / 86_400), -1, 1);
        }
    }

    [Fact]
    public void TheDayChangesOfTheBots_AreSpreadOverTheWholeDay_NotAllAtMidnight()
    {
        const long start = 1_800_000_000;
        long[] offsets = [.. Enumerable.Range(1, 256).Select(guid => ChangeInstant((ulong)guid, start) % 86_400)];

        Assert.True(offsets.Distinct().Count() >= 200, "256 bots had only " + offsets.Distinct().Count() + " distinct change instants");
        Assert.True(offsets.Max() - offsets.Min() > 60_000, "the change instants must cover most of the day");
        // No clump: every sixth of the day holds some.
        for (int sixth = 0; sixth < 6; sixth++)
            Assert.Contains(offsets, o => o / 14_400 == sixth);
    }

    // --- the height rule -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(0f, 5f, true)]
    [InlineData(0f, -5f, true)]
    [InlineData(0f, 6f, false)]
    [InlineData(0f, -6f, false)]
    [InlineData(30f, 0f, true)]
    [InlineData(30.01f, 0f, false)]
    [InlineData(30f, 5f, true)]
    [InlineData(30f, 5.01f, false)]
    [InlineData(2f, 20f, false)]
    [InlineData(0f, float.NaN, false)]
    [InlineData(float.NaN, 0f, false)]
    public void ASpotIsHeldOnlyWithin2DHoldYards_AndFiveYardsOfHeight(float distance2D, float dz, bool held)
        => Assert.Equal(held, PlayerbotFestivalRules.IsHeldAtSpot(distance2D, dz, 10f));

    [Theory]
    [InlineData(0f, false)]
    [InlineData(4.99f, false)]
    [InlineData(5f, true)]
    [InlineData(12f, true)]
    [InlineData(float.NaN, false)]
    public void ASpotIsNotHeldWithinFiveYardsOfTheAnchor_ASoStalledWalkNeverParksInAnInteractionDisc(float anchorDistance, bool held)
        => Assert.Equal(held, PlayerbotFestivalRules.IsHeldAtSpot(2f, 0f, anchorDistance));

    [Fact]
    public void TheSpotDistance_IsThe2DDistanceOnTheSameFloor_AndAddsTheHeightOnAnother()
    {
        Assert.Equal(12f, PlayerbotFestivalRules.SpotDistance(12f, 0f));
        Assert.Equal(12f, PlayerbotFestivalRules.SpotDistance(12f, 5f));
        Assert.Equal(12f, PlayerbotFestivalRules.SpotDistance(12f, -5f));
        Assert.Equal(18f, PlayerbotFestivalRules.SpotDistance(12f, 6f));
        Assert.Equal(18f, PlayerbotFestivalRules.SpotDistance(12f, -6f));
        Assert.Equal(32f, PlayerbotFestivalRules.SpotDistance(12f, 20f));
    }

    [Fact]
    public void ABotOnTheWrongFloor_IsNeverArrivedAtItsSpot_WhateverItsGroundDistance()
    {
        // Straight above or below the spot (2D distance 0), one yard beyond the tolerance: the town idle must still walk it, not linger.
        var idle = new PlayerbotTownIdle();
        float distance = PlayerbotFestivalRules.SpotDistance(0f, 6f);

        Assert.True(distance > PlayerbotTownIdle.LingerYards);
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, distance, 0));
        Assert.False(PlayerbotFestivalRules.IsHeldAtSpot(0f, 6f, 10f));
    }
}
