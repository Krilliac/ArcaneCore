using ArcaneCore.Game.Entities;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// Characterises DEFAULT_WORLD_OBJECT_SIZE (vmangos ObjectDefines.h) as seen through live objects: a default player's
/// UNIT_FIELD_BOUNDINGRADIUS and the base-class constant stay the same bit-exact float (0x3EC72B02).
/// </summary>
public sealed class DefaultBoundingRadiusObjectTests
{
    private const int ExpectedBits = 0x3EC72B02; // BitConverter.SingleToInt32Bits(0.388999998569489f)

    [Fact]
    public void WorldObjectDefaultBoundingRadius_IsBitExact()
    {
        Assert.Equal(ExpectedBits, BitConverter.SingleToInt32Bits(WorldObject.DefaultBoundingRadius));
    }

    [Fact]
    public void NewPlayer_BoundingRadius_IsTheBitExactDefault()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        Assert.Equal(ExpectedBits, BitConverter.SingleToInt32Bits(player.BoundingRadius));
    }
}
