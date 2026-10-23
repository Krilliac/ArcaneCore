using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Entities;

/// <summary>
/// Boundary tests for every <see cref="WorldGeometry"/> variant, at exactly the limit and one ulp each side. The limits are chosen so the
/// squared comparison is exact at the limit (3-4-12 gives 13; ranges 10 and 11.5 square exactly), and the ulp is applied to the axis
/// that dominates the sum so the squared value always moves by at least one float step. The square-root variants are compared with an
/// independently computed square root, because a root can round an ulp step back onto the limit.
/// </summary>
public sealed class WorldGeometryTests
{
    private static Player At(uint guid, float x, float y, float z, float radius = 0f)
    {
        Player player = TestWorld.CreatePlayer(guid, x, y, new FakeSession((int)guid));
        player.Relocate(x, y, z);
        player.SetFloat(UpdateFields.UnitFieldBoundingradius, radius);
        return player;
    }

    [Fact]
    public void DistanceSq3D_IsExactAtTheLimit_AndMovesOneUlpEachSide()
    {
        Player origin = At(1, 0, 0, 0);

        Assert.Equal(169f, WorldGeometry.DistanceSq3D(origin, At(2, 3, 4, 12)));
        Assert.Equal(169f, WorldGeometry.DistanceSq3D(At(3, 3, 4, 12), origin));
        Assert.True(WorldGeometry.DistanceSq3D(origin, At(4, 3, 4, MathF.BitIncrement(12f))) > 169f);
        Assert.True(WorldGeometry.DistanceSq3D(origin, At(5, 3, 4, MathF.BitDecrement(12f))) < 169f);
    }

    [Fact]
    public void CenterDistance3D_IsExactAtTheLimit_AndFollowsTheRootEachSide()
    {
        Player origin = At(1, 0, 0, 0);
        float over = MathF.BitIncrement(12f);
        float under = MathF.BitDecrement(12f);

        Assert.Equal(13f, WorldGeometry.CenterDistance3D(origin, At(2, 3, 4, 12)));
        Assert.Equal(MathF.Sqrt((3f * 3f) + (4f * 4f) + (over * over)), WorldGeometry.CenterDistance3D(origin, At(3, 3, 4, over)));
        Assert.Equal(MathF.Sqrt((3f * 3f) + (4f * 4f) + (under * under)), WorldGeometry.CenterDistance3D(origin, At(4, 3, 4, under)));
        Assert.True(WorldGeometry.CenterDistance3D(origin, At(5, 3, 4, over)) >= 13f);
        Assert.True(WorldGeometry.CenterDistance3D(origin, At(6, 3, 4, under)) <= 13f);
    }

    [Fact]
    public void Distance2D_IgnoresZ_IsExactAtTheLimit_AndFollowsTheRootEachSide()
    {
        Player origin = At(1, 0, 0, 0);
        float over = MathF.BitIncrement(12f);
        float under = MathF.BitDecrement(12f);

        Assert.Equal(5f, WorldGeometry.Distance2D(origin, At(2, 3, 4, 0)));
        Assert.Equal(5f, WorldGeometry.Distance2D(origin, At(3, 3, 4, 1000)));
        Assert.Equal(13f, WorldGeometry.Distance2D(origin, At(4, 5, 12, -77)));
        Assert.Equal(MathF.Sqrt((5f * 5f) + (over * over)), WorldGeometry.Distance2D(origin, At(5, 5, over, 9)));
        Assert.Equal(MathF.Sqrt((5f * 5f) + (under * under)), WorldGeometry.Distance2D(origin, At(6, 5, under, 9)));
        Assert.True(WorldGeometry.Distance2D(origin, At(7, 5, over, 0)) >= 13f);
        Assert.True(WorldGeometry.Distance2D(origin, At(8, 5, under, 0)) <= 13f);
    }

    [Fact]
    public void DistanceMinusRadii_IsExactAtTouching_AndClampsAtZero()
    {
        Assert.Equal(3.5f, WorldGeometry.DistanceMinusRadii(At(1, 0, 0, 0, 1f), At(2, 3, 4, 0, 0.5f)));
        Assert.Equal(0f, WorldGeometry.DistanceMinusRadii(At(3, 0, 0, 0, 2f), At(4, 3, 4, 0, 3f)));
        Assert.Equal(0f, WorldGeometry.DistanceMinusRadii(At(5, 0, 0, 0, 4f), At(6, 3, 4, 0, 4f)));

        // One ulp beyond touching: the root of the squared sum, less 2, less 3 (the root may round back onto 5, so compare to that value).
        float over = MathF.BitIncrement(5f);
        float expectedOver = MathF.Sqrt(over * over) - 2f - 3f;
        Assert.Equal(expectedOver, WorldGeometry.DistanceMinusRadii(At(7, 0, 0, 0, 2f), At(8, over, 0, 0, 3f)));
        Assert.True(expectedOver >= 0f);
        Assert.Equal(0f, WorldGeometry.DistanceMinusRadii(At(9, 0, 0, 0, 2f), At(10, MathF.BitDecrement(5f), 0, 0, 3f)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void IsWithinDist_PlainRange_BoundaryDependsOnInclusive(bool inclusive, bool addRadii)
    {
        // Radii are zero, so addRadii must not change the reach.
        Player origin = At(1, 0, 0, 0);
        float limit = 10f;

        Assert.Equal(inclusive, WorldGeometry.IsWithinDist(origin, At(2, limit, 0, 0), limit, inclusive, addRadii, MapCheck.Id));
        Assert.False(WorldGeometry.IsWithinDist(origin, At(3, MathF.BitIncrement(limit), 0, 0), limit, inclusive, addRadii, MapCheck.Id));
        Assert.True(WorldGeometry.IsWithinDist(origin, At(4, MathF.BitDecrement(limit), 0, 0), limit, inclusive, addRadii, MapCheck.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsWithinDist_WithoutAddRadii_IgnoresTheRadii(bool inclusive)
    {
        Player origin = At(1, 0, 0, 0, 50f);

        Assert.False(WorldGeometry.IsWithinDist(origin, At(2, 10.5f, 0, 0, 50f), 10f, inclusive, addRadii: false, MapCheck.Id));
        Assert.True(WorldGeometry.IsWithinDist(origin, At(3, 9.5f, 0, 0, 50f), 10f, inclusive, addRadii: false, MapCheck.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsWithinDist_AddRadii_GrowsTheReachByBothRadii(bool inclusive)
    {
        // Range 10 plus radii 1 and 0.5 gives 11.5 (11.5 squared is exactly 132.25).
        Player origin = At(1, 0, 0, 0, 1f);
        float limit = 11.5f;

        Assert.Equal(inclusive, WorldGeometry.IsWithinDist(origin, At(2, limit, 0, 0, 0.5f), 10f, inclusive, addRadii: true, MapCheck.Id));
        Assert.False(WorldGeometry.IsWithinDist(origin, At(3, MathF.BitIncrement(limit), 0, 0, 0.5f), 10f, inclusive, addRadii: true, MapCheck.Id));
        Assert.True(WorldGeometry.IsWithinDist(origin, At(4, MathF.BitDecrement(limit), 0, 0, 0.5f), 10f, inclusive, addRadii: true, MapCheck.Id));

        // The same point is out of reach once the radii are not added, and the radii of either side count.
        Assert.False(WorldGeometry.IsWithinDist(origin, At(5, 11f, 0, 0, 0.5f), 10f, inclusive, addRadii: false, MapCheck.Id));
        Assert.True(WorldGeometry.IsWithinDist(origin, At(6, 11f, 0, 0, 0.5f), 10f, inclusive, addRadii: true, MapCheck.Id));
        Assert.True(WorldGeometry.IsWithinDist(At(7, 11f, 0, 0, 0.5f), origin, 10f, inclusive, addRadii: true, MapCheck.Id));
    }

    [Fact]
    public void IsWithinDist_UsesAllThreeAxes()
    {
        Player origin = At(1, 0, 0, 0);

        Assert.True(WorldGeometry.IsWithinDist(origin, At(2, 3, 4, 12), 13f, inclusive: true, addRadii: false, MapCheck.Id));
        Assert.False(WorldGeometry.IsWithinDist(origin, At(3, 3, 4, 12), 13f, inclusive: false, addRadii: false, MapCheck.Id));
        Assert.False(WorldGeometry.IsWithinDist(origin, At(4, 3, 4, MathF.BitIncrement(12f)), 13f, inclusive: true, addRadii: false, MapCheck.Id));
        Assert.True(WorldGeometry.IsWithinDist(origin, At(5, 3, 4, MathF.BitDecrement(12f)), 13f, inclusive: false, addRadii: false, MapCheck.Id));
    }

    [Fact]
    public void IsWithinDist_MapCheckNone_PassesAcrossMaps()
    {
        Player a = At(1, 0, 0, 0);
        Player otherId = At(2, 5, 0, 0);
        otherId.MapId = 1;

        using WorldRuntime world = TestWorld.CreateRuntime();
        Player otherMap = At(3, 5, 0, 0);
        otherMap.Map = world.GetMap(0, 1);

        Assert.True(WorldGeometry.IsWithinDist(a, otherId, 10f, inclusive: true, addRadii: false, MapCheck.None));
        Assert.True(WorldGeometry.IsWithinDist(a, otherMap, 10f, inclusive: true, addRadii: false, MapCheck.None));
        Assert.False(WorldGeometry.IsWithinDist(a, At(4, 10.5f, 0, 0), 10f, inclusive: true, addRadii: false, MapCheck.None));
        Assert.False(WorldGeometry.IsWithinDist(a, At(5, MathF.BitIncrement(10f), 0, 0), 10f, inclusive: true, addRadii: false, MapCheck.None));
        Assert.True(WorldGeometry.IsWithinDist(a, At(6, 10f, 0, 0), 10f, inclusive: true, addRadii: false, MapCheck.None));
    }

    [Fact]
    public void IsWithinDist_MapCheckId_RejectsADifferentMapId_ButNotADifferentInstance()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player a = At(1, 0, 0, 0);
        a.Map = world.GetMap(0, 1);
        Player sameIdOtherInstance = At(2, 5, 0, 0);
        sameIdOtherInstance.Map = world.GetMap(0, 2);
        Player otherId = At(3, 5, 0, 0);
        otherId.MapId = 1;

        Assert.True(WorldGeometry.IsWithinDist(a, sameIdOtherInstance, 10f, inclusive: true, addRadii: false, MapCheck.Id));
        Assert.False(WorldGeometry.IsWithinDist(a, otherId, 10f, inclusive: true, addRadii: false, MapCheck.Id));
        Assert.False(WorldGeometry.IsWithinDist(otherId, a, 10f, inclusive: true, addRadii: false, MapCheck.Id));
    }

    [Fact]
    public void IsWithinDist_MapCheckRef_RequiresTheSameMapInstance()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map first = world.GetMap(0, 1);
        Map second = world.GetMap(0, 2);
        Assert.Equal(first.MapId, second.MapId);

        Player a = At(1, 0, 0, 0);
        a.Map = first;
        Player sameMap = At(2, 5, 0, 0);
        sameMap.Map = first;
        Player sameIdOtherInstance = At(3, 5, 0, 0);
        sameIdOtherInstance.Map = second;

        Assert.True(WorldGeometry.IsWithinDist(a, sameMap, 10f, inclusive: true, addRadii: false, MapCheck.Ref));
        Assert.False(WorldGeometry.IsWithinDist(a, sameIdOtherInstance, 10f, inclusive: true, addRadii: false, MapCheck.Ref));
        Assert.False(WorldGeometry.IsWithinDist(sameIdOtherInstance, a, 10f, inclusive: true, addRadii: false, MapCheck.Ref));

        // The same Map instance passes even when the MapId fields disagree, and two objects in no map share the null reference.
        Player differentIdSameMap = At(4, 5, 0, 0);
        differentIdSameMap.Map = first;
        differentIdSameMap.MapId = 7;
        Assert.True(WorldGeometry.IsWithinDist(a, differentIdSameMap, 10f, inclusive: true, addRadii: false, MapCheck.Ref));
        Assert.True(WorldGeometry.IsWithinDist(At(5, 0, 0, 0), At(6, 5, 0, 0), 10f, inclusive: true, addRadii: false, MapCheck.Ref));
        Assert.False(WorldGeometry.IsWithinDist(a, At(7, 5, 0, 0), 10f, inclusive: true, addRadii: false, MapCheck.Ref));

        // A passing map check still honours the distance boundary.
        Player atLimit = At(8, 10f, 0, 0);
        atLimit.Map = first;
        Player beyond = At(9, MathF.BitIncrement(10f), 0, 0);
        beyond.Map = first;
        Assert.True(WorldGeometry.IsWithinDist(a, atLimit, 10f, inclusive: true, addRadii: false, MapCheck.Ref));
        Assert.False(WorldGeometry.IsWithinDist(a, atLimit, 10f, inclusive: false, addRadii: false, MapCheck.Ref));
        Assert.False(WorldGeometry.IsWithinDist(a, beyond, 10f, inclusive: true, addRadii: false, MapCheck.Ref));
    }
}
