using ArcaneCore.Game.Entities;
using ArcaneCore.World.OutdoorPvP;
using Xunit;

namespace ArcaneCore.World.Tests.OutdoorPvP;

/// <summary>
/// Characterises DEFAULT_WORLD_OBJECT_SIZE (vmangos ObjectDefines.h) as carried by the player constant and the outdoor-PvP
/// game-object constant: both must stay the same bit-exact float (0x3EC72B02) however the literal is shared.
/// </summary>
public sealed class DefaultBoundingRadiusTests
{
    private const int ExpectedBits = 0x3EC72B02; // BitConverter.SingleToInt32Bits(0.388999998569489f)

    [Fact]
    public void PlayerDefaultBoundingRadius_IsBitExact()
    {
        Assert.Equal(ExpectedBits, BitConverter.SingleToInt32Bits(Player.DefaultBoundingRadius));
    }

    [Fact]
    public void OutdoorPvPGameObjectBoundingRadius_IsBitExact()
    {
        Assert.Equal(ExpectedBits, BitConverter.SingleToInt32Bits(OutdoorPvPWorldHost.GameObjectBoundingRadius));
    }

    [Fact]
    public void PlayerAndGameObjectDefaults_AreIdentical()
    {
        Assert.Equal(Player.DefaultBoundingRadius, OutdoorPvPWorldHost.GameObjectBoundingRadius);
    }
}
