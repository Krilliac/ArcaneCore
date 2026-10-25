using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// <see cref="PlayerbotTownIdle.Recentre"/> (P7a H3, design 4.2 and review item I1): the town idle keeps walk state (best distance, give-up)
/// that is only meaningful against one centre. A festival spot makes the centre change, so the brain recentres on every change; a walk is
/// then never judged by distances measured from two different points, and the give-up of one centre never carries over to the next.
/// </summary>
public sealed class PlayerbotTownIdleRecentreTests
{
    [Fact]
    public void AWalkTowardOneCentre_ThenRecentred_IsNotJudgedByTheProgressMadeTowardTheOther()
    {
        var idle = new PlayerbotTownIdle();

        // Toward the inn: 100, 60, 30 yards in 15 seconds, steady progress.
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 100f, 0));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 60f, 10_000));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 30f, 15_000));

        // A festival spot becomes the centre: 200 yards away. Its walk starts afresh at the next decision.
        idle.Recentre();
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 200f, 16_000));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 150f, 30_000)); // progress toward the new centre

        // 19 seconds without further progress: still inside WalkStallMs of the last progress. Without the recentre the old best (30 yards,
        // set at 15 s) would have given this walk up at 35 s.
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 150f, 49_000));
    }

    [Fact]
    public void WithoutARecentre_TheSameWalkIsGivenUp_SoTheRecentreIsWhatKeepsItGoing()
    {
        // The control for the test above: the very same distances with no Recentre are judged against the old centre's best.
        var idle = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 100f, 0));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 60f, 10_000));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 30f, 15_000));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 200f, 16_000));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 150f, 30_000));

        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 150f, 49_000));
    }

    [Fact]
    public void ASecondaryBotInItsExploreWindow_ThenDecidedAsACapitalBot_IsNoLongerExploring_WithoutARehome()
    {
        var idle = new PlayerbotTownIdle();
        idle.Decide(TownStay.Secondary, 0f, 1_000);
        Assert.Equal(TownIdleAction.Explore, idle.Decide(TownStay.Secondary, 0f, 1_000 + PlayerbotTownIdle.LingerMs));
        Assert.True(idle.Exploring);

        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 0f, 2_000 + PlayerbotTownIdle.LingerMs));
        Assert.False(idle.Exploring);
    }

    [Fact]
    public void ARehomedBot_StartsItsLingerAndExploreWindowAfresh()
    {
        var idle = new PlayerbotTownIdle();
        idle.Decide(TownStay.Secondary, 0f, 1_000);
        idle.Decide(TownStay.Secondary, 0f, 1_000 + PlayerbotTownIdle.LingerMs);
        Assert.True(idle.Exploring);

        idle.Rehomed();

        Assert.False(idle.Exploring);
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Secondary, 0f, 5_000 + PlayerbotTownIdle.LingerMs));
    }

    [Fact]
    public void AWalkGivenUp_ThenRecentred_WalksToTheNewCentreAtOnce_NotAfterTheDeferral()
    {
        var idle = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 500f, 0));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 500f, PlayerbotTownIdle.WalkStallMs)); // given up
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 500f, PlayerbotTownIdle.WalkStallMs + 1_000));

        // The brain gives the spot up, backs it off and recentres on the inn: the walk back starts in the same think.
        idle.Recentre();
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 300f, PlayerbotTownIdle.WalkStallMs + 2_000));
    }

    [Fact]
    public void WithoutARecentre_AGivenUpWalk_StaysGivenUpForTheWholeDeferral()
    {
        // The control: a different centre alone does not lift the deferral (this is revision 1's "falls through to explore" gap).
        var idle = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 500f, 0));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 500f, PlayerbotTownIdle.WalkStallMs));

        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 300f, PlayerbotTownIdle.WalkStallMs + 2_000));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 300f, PlayerbotTownIdle.WalkStallMs + PlayerbotTownIdle.StallDeferMs - 1));
    }

    [Fact]
    public void ARecentredWalk_CanStillStallAndBeGivenUp_ByTheNormalRule()
    {
        var idle = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 400f, 0));
        idle.Recentre();
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 300f, 5_000));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 300f, 5_000 + PlayerbotTownIdle.WalkStallMs - 1));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 300f, 5_000 + PlayerbotTownIdle.WalkStallMs));
    }

    [Fact]
    public void ARecentre_AtTheCentre_StillLingersThere()
    {
        var idle = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 2f, 0));

        idle.Recentre();

        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 2f, 1_000));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, PlayerbotTownIdle.LingerYards, 2_000));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, PlayerbotTownIdle.LingerYards + 1f, 3_000));
    }

    [Fact]
    public void ARecentre_KeepsTheSecondaryTownsLingerAndExploreWindow()
    {
        var idle = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Secondary, 2f, 0));
        Assert.Equal(TownIdleAction.Explore, idle.Decide(TownStay.Secondary, 2f, PlayerbotTownIdle.LingerMs + 1)); // the explore half starts

        idle.Recentre();

        // Still inside the explore half: the recentre did not restart the window (which would walk or linger).
        Assert.Equal(TownIdleAction.Explore, idle.Decide(TownStay.Secondary, 900f, PlayerbotTownIdle.LingerMs + 100_000));
        // And the cycle goes on to its next linger half at the same time as without the recentre.
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Secondary, 900f, PlayerbotTownIdle.LingerMs + PlayerbotTownIdle.ExploreMs + 2));
    }

    [Fact]
    public void ARecentre_InTheLingerHalf_DoesNotCutTheLingerShort_OrExtendIt()
    {
        var idle = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Secondary, 2f, 0));

        idle.Recentre();

        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Secondary, 2f, PlayerbotTownIdle.LingerMs - 1));
        Assert.Equal(TownIdleAction.Explore, idle.Decide(TownStay.Secondary, 2f, PlayerbotTownIdle.LingerMs + 1));
    }

    [Fact]
    public void ARecentreBeforeAnyDecision_ChangesNothing()
    {
        var idle = new PlayerbotTownIdle();
        idle.Recentre();

        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 90f, 0));
        Assert.Equal(TownIdleAction.Explore, new PlayerbotTownIdle().Decide(TownStay.None, 5000f, 0));
        var none = new PlayerbotTownIdle();
        none.Recentre();
        Assert.Equal(TownIdleAction.Explore, none.Decide(TownStay.None, 5000f, 0));
    }
}
