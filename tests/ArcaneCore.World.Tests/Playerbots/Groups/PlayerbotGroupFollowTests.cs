using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Group follow catch-up (bot QA #989). A member on step <c>follow</c> used to only walk, so the party bots' distance teleport
/// never ran: one 428 yards from its leader stayed there for the whole left-behind budget while the group travelled.
/// </summary>
public sealed class PlayerbotGroupFollowTests
{
    private static PlayerbotGroupCatchUpFacts Far(PlayerbotGroupState state = PlayerbotGroupState.Travelling, float yards = 428f,
        bool fightThroughRun = false, PlayerbotCatchUpMode mode = PlayerbotCatchUpMode.Teleport, bool inCombat = false,
        bool leaderInCombat = false, bool teleport = true, float teleportDistance = 100f)
        => new(state, yards, inCombat, leaderInCombat, teleport, mode, teleportDistance, 50f, fightThroughRun);

    [Fact]
    public void AMember428YardsOut_WhileTravelling_IsTeleportedToTheLeader()
    {
        Assert.True(PlayerbotGroupAI.ShouldCatchUp(Far()));
        Assert.True(PlayerbotGroupAI.ShouldCatchUp(Far(yards: 100.1f)));
        Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far(yards: 100f))); // vmangos IsWithinDistInMap(100) still walks
        Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far(yards: 30f)));
    }

    [Theory]
    [InlineData((byte)PlayerbotGroupState.Engaging)]
    [InlineData((byte)PlayerbotGroupState.Regrouping)]
    public void EngagingAndRegrouping_CatchUpTheSameWay(byte state)
        => Assert.True(PlayerbotGroupAI.ShouldCatchUp(Far((PlayerbotGroupState)state)));

    [Theory]
    [InlineData((byte)PlayerbotGroupState.Gathering)]
    [InlineData((byte)PlayerbotGroupState.Forming)]
    [InlineData((byte)PlayerbotGroupState.Entering)]
    [InlineData((byte)PlayerbotGroupState.Wiped)]
    public void GatheringAndTheOtherWaits_StillWalk(byte state)
        => Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far((PlayerbotGroupState)state)));

    [Fact]
    public void AFightThroughRun_OrAModeThatWalks_DoesNotTeleport_EvenAt428Yards()
    {
        Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far(fightThroughRun: true)));
        Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far(mode: PlayerbotCatchUpMode.FightThrough)));
        Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far(mode: PlayerbotCatchUpMode.Auto)));
        Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far(teleport: false)));
        Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far(teleportDistance: 500f)));
    }

    [Fact]
    public void AMemberInTheLeadersFight_Walks_OneWhoseLeaderLeftIt_Teleports()
    {
        Assert.False(PlayerbotGroupAI.ShouldCatchUp(Far(inCombat: true, leaderInCombat: true)));
        Assert.True(PlayerbotGroupAI.ShouldCatchUp(Far(inCombat: true, leaderInCombat: false)));
    }
}
