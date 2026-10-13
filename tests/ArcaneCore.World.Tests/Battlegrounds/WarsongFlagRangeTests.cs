using ArcaneCore.Game.Entities;
using ArcaneCore.World.Battlegrounds;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>
/// A Warsong Gulch ground flag is clicked with vmangos <c>IsWithinDistInMap(go, 10)</c> (BattleGroundWS.cpp:404, 439): 3D, the player's
/// bounding radius plus <c>DEFAULT_WORLD_OBJECT_SIZE</c> (GameObject::GetObjectBoundingRadius), and a strict less-than (Object.cpp:1738-1751).
/// Center-to-center <c><= 10</c> refuses a pickup the reference accepts.
/// </summary>
public sealed class WarsongFlagRangeTests
{
    [Fact]
    public void GroundFlagClick_IncludesBothBoundingRadii_AndStopsShortOfTheEdge()
    {
        float playerRadius = Player.DefaultBoundingRadius;
        float flagRadius = Player.DefaultBoundingRadius; // GameObject::GetObjectBoundingRadius
        float edge = 10f + playerRadius + flagRadius;

        // Centers more than 10 yards apart are still in range once both radii count (about 10.78 yards).
        Assert.True(MatchRuntime.IsWithinDist(10.5f, 0f, 0f, 10f, playerRadius, flagRadius));
        Assert.True(MatchRuntime.IsWithinDist(edge - 0.01f, 0f, 0f, 10f, playerRadius, flagRadius));

        // distsq < maxdist^2, so the surface itself is outside.
        Assert.False(MatchRuntime.IsWithinDist(edge, 0f, 0f, 10f, playerRadius, flagRadius));
        Assert.False(MatchRuntime.IsWithinDist(edge + 0.01f, 0f, 0f, 10f, playerRadius, flagRadius));

        // Height counts: 8 yards over and 8 up is about 11.3 yards, past the reach.
        Assert.False(MatchRuntime.IsWithinDist(8f, 0f, 8f, 10f, playerRadius, flagRadius));
    }
}
