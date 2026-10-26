using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Goal points of the retreat and the inward explore leg stand on the floor of a cave. Live 2026-10-25 (issues #1478 and #1479,
/// Timbermaw Hold): the floor under a goal point was searched from 20 yards above it only, which inside a tunnel starts in the rock over
/// the bot, so the point stayed at the bot's height in rock, on no region of the region graph, and every plan toward it was refused.
/// </summary>
public sealed class PlayerbotCaveGroundTests
{
    private const float BotZ = 100f;

    /// <summary>
    /// A tunnel: the floor is 1 yard under the bot, the roof 40 yards over it. A probe that starts 10 yards or more above the bot starts
    /// in the rock over the tunnel and finds the roof, as the old search from 20 yards up did.
    /// </summary>
    private static float Tunnel(float x, float y, float probeFrom) => probeFrom >= BotZ + 10f ? BotZ + 40f : BotZ - 1f;

    [Fact]
    public void GroundNear_InsideACave_PutsThePointOnTheFloor_NotTheRoofNorTheBotsHeight()
    {
        Vector3 point = new(10f, 20f, BotZ);
        // The old search: from 20 yards up, the roof 40 yards over the bot, out of reach, so the point stayed at the bot's height.
        Assert.Equal(BotZ + 40f, Tunnel(point.X, point.Y, BotZ + 20f));

        Vector3 grounded = PlayerbotNavigation.GroundNear(Tunnel, null, BotZ, point);

        Assert.Equal(BotZ - 1f, grounded.Z);
        Assert.Equal(point.X, grounded.X);
        Assert.Equal(point.Y, grounded.Y);
    }

    [Fact]
    public void GroundNear_TakesTheFloorNearestTheBot_AndLeavesThePointWhenThereIsNone()
    {
        // The low probe finds a ledge 9 yards under the bot, the high one the floor the bot stands near (1 under it).
        static float Ledges(float x, float y, float probeFrom) => probeFrom <= BotZ + 2f ? BotZ - 9f : BotZ - 1f;
        Assert.Equal(BotZ - 1f, PlayerbotNavigation.GroundNear(Ledges, null, BotZ, new Vector3(0f, 0f, BotZ)).Z);

        Vector3 point = new(1f, 2f, 55f);
        Assert.Equal(point, PlayerbotNavigation.GroundNear((_, _, _) => BotZ + 40f, null, BotZ, point));
        Assert.Equal(point, PlayerbotNavigation.GroundNear((_, _, _) => float.NaN, null, BotZ, point));
    }

    [Fact]
    public void GroundNear_DropsAFloorThatIsOnNoRegionOrAnotherComponent()
    {
        Vector3 point = new(0f, 0f, BotZ);
        // Both floors are dropped: the point is left as it was (no new height claimed).
        Assert.Equal(point, PlayerbotNavigation.GroundNear(Tunnel, _ => false, BotZ, point));
        // Only the floor at BotZ - 1 is walkable.
        Assert.Equal(BotZ - 1f, PlayerbotNavigation.GroundNear(Tunnel, floor => floor.Z < BotZ, BotZ, point).Z);
    }

    [Fact]
    public void InwardLeg_EndsOnTheFloor_WhenGroundIsGiven()
    {
        var home = new PlayerbotHomeArea(1, new Vector3(0f, 0f, BotZ), 100f, "Test");
        var from = new Vector3(80f, 0f, BotZ);

        Vector3 ungrounded = PlayerbotBrain.InwardLeg(home, from, 0);
        Vector3 grounded = PlayerbotBrain.InwardLeg(home, from, 0, end => PlayerbotNavigation.GroundNear(Tunnel, null, BotZ, end));

        Assert.Equal(BotZ, ungrounded.Z);
        Assert.Equal(BotZ - 1f, grounded.Z);
        Assert.Equal(ungrounded.X, grounded.X);
        Assert.Equal(ungrounded.Y, grounded.Y);
    }
}
