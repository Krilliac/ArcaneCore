using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>
/// Characterisation of the hand-rolled world distance checks that Wave 2 item 2a folds into WorldGeometry:
/// <see cref="LootService.Distance3D"/> (centre distance minus both radii, clamped at 0) and
/// <see cref="TradeSession.InRange"/> (centre distance squared, inclusive, radii ignored, map id compare).
/// Expected values are exact: 3-4-0 and 2-3-6 give integral centre distances, and the radii used are exactly representable.
/// </summary>
public sealed class WorldDistanceCharacterisationTests
{
    private static Player At(uint guid, float x, float y, float z, float radius, uint mapId = 0)
    {
        Player player = TestWorld.CreatePlayer(guid, x, y, new FakeSession((int)guid), mapId);
        player.Relocate(x, y, z);
        player.SetFloat(UpdateFields.UnitFieldBoundingradius, radius);
        return player;
    }

    [Fact]
    public void Distance3D_IsTheCentreDistanceWithNoRadii()
    {
        Assert.Equal(5f, LootService.Distance3D(At(1, 0, 0, 0, 0f), At(2, 3, 4, 0, 0f)));
        Assert.Equal(7f, LootService.Distance3D(At(3, 0, 0, 0, 0f), At(4, 2, 3, 6, 0f)));
    }

    [Fact]
    public void Distance3D_SubtractsBothBoundingRadii()
    {
        Assert.Equal(3.5f, LootService.Distance3D(At(1, 0, 0, 0, 1f), At(2, 3, 4, 0, 0.5f)));
        Assert.Equal(3.5f, LootService.Distance3D(At(2, 3, 4, 0, 0.5f), At(1, 0, 0, 0, 1f)));
    }

    [Fact]
    public void Distance3D_ClampsAtZero_WhenTheRadiiOverlapOrTouch()
    {
        Assert.Equal(0f, LootService.Distance3D(At(1, 0, 0, 0, 2f), At(2, 3, 4, 0, 3f)));   // exactly touching: 5 - 5
        Assert.Equal(0f, LootService.Distance3D(At(3, 0, 0, 0, 4f), At(4, 3, 4, 0, 4f)));   // overlapping: 5 - 8 < 0
        Assert.Equal(0f, LootService.Distance3D(At(5, 1, 1, 1, 1f), At(6, 1, 1, 1, 1f)));   // coincident
    }

    [Fact]
    public void TradeInRange_IsInclusiveAtMaxDistance_AndFalseJustBeyond()
    {
        Player a = At(1, 0, 0, 0, 0f);
        Player atLimit = At(2, TradeRules.MaxDistance, 0, 0, 0f);
        Player beyond = At(3, MathF.BitIncrement(TradeRules.MaxDistance), 0, 0, 0f);
        Player inside = At(4, MathF.BitDecrement(TradeRules.MaxDistance), 0, 0, 0f);

        Assert.True(TradeSession.InRange(a, atLimit));
        Assert.True(TradeSession.InRange(a, inside));
        Assert.False(TradeSession.InRange(a, beyond));
    }

    [Fact]
    public void TradeInRange_UsesAllThreeAxes_IgnoresRadii_AndRequiresTheSameMapId()
    {
        Player a = At(1, 0, 0, 0, 0f);

        // 6-6-6 away in 3D is 10.39 (in range); 7-7-7 is 12.12 (out of range) although each axis alone is below 11.11.
        Assert.True(TradeSession.InRange(a, At(2, 6, 6, 6, 0f)));
        Assert.False(TradeSession.InRange(a, At(3, 7, 7, 7, 0f)));

        // Large radii neither extend nor shrink the reach.
        Assert.False(TradeSession.InRange(At(4, 0, 0, 0, 50f), At(5, 12, 0, 0, 50f)));
        Assert.True(TradeSession.InRange(At(6, 0, 0, 0, 0f), At(7, 11, 0, 0, 0f)));

        // Same coordinates on another map id.
        Assert.False(TradeSession.InRange(a, At(8, 0, 0, 0, 0f, mapId: 1)));
    }
}
