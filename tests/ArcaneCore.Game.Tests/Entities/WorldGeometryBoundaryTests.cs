using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Entities;

/// <summary>
/// Boundary tests for the exact <see cref="WorldGeometry.IsWithinDist"/> configurations the spell code migrated onto (talent proc
/// target search, feign death creature check, persistent area aura leave test). Each pins the comparison (&lt; or &lt;=), the radii and
/// the map check at exactly the limit and one ulp each side; the limits square exactly (range 10 plus radii 1 and 0.5 is 11.5).
/// </summary>
public sealed class WorldGeometryBoundaryTests
{
    private const float Limit = 11.5f;

    private static Player At(uint guid, float x, float radius)
    {
        Player player = TestWorld.CreatePlayer(guid, x, 0, new FakeSession((int)guid));
        player.Relocate(x, 0, 0);
        player.SetFloat(UpdateFields.UnitFieldBoundingradius, radius);
        return player;
    }

    [Fact]
    public void InclusiveWithRadiiAndNoMapCheck_TalentProcAndPersistentAura()
    {
        Player origin = At(1, 0, 1f);

        Assert.True(WorldGeometry.IsWithinDist(origin, At(2, Limit, 0.5f), 10f, inclusive: true, addRadii: true, MapCheck.None));
        Assert.False(WorldGeometry.IsWithinDist(origin, At(3, MathF.BitIncrement(Limit), 0.5f), 10f, inclusive: true, addRadii: true, MapCheck.None));
        Assert.True(WorldGeometry.IsWithinDist(origin, At(4, MathF.BitDecrement(Limit), 0.5f), 10f, inclusive: true, addRadii: true, MapCheck.None));
    }

    [Fact]
    public void ExclusiveWithRadiiAndRefMapCheck_FeignDeath()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player origin = At(1, 0, 1f);
        origin.Map = world.GetMap(0, 0);
        Player atLimit = At(2, Limit, 0.5f);
        atLimit.Map = origin.Map;
        Player inside = At(3, MathF.BitDecrement(Limit), 0.5f);
        inside.Map = origin.Map;
        Player otherInstance = At(4, 1f, 0.5f);
        otherInstance.Map = world.GetMap(0, 1);

        Assert.False(WorldGeometry.IsWithinDist(origin, atLimit, 10f, inclusive: false, addRadii: true, MapCheck.Ref));
        Assert.True(WorldGeometry.IsWithinDist(origin, inside, 10f, inclusive: false, addRadii: true, MapCheck.Ref));
        Assert.False(WorldGeometry.IsWithinDist(origin, otherInstance, 10f, inclusive: false, addRadii: true, MapCheck.Ref));
    }
}
