using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The placement surface gate refuses only what ADT data can prove wrong: non-finite coordinates and water. Ground height is not judged,
/// because a graveyard inside a WMO (Undercity, Ironforge) or a cave sits far from the ADT heightfield and is correct.
/// </summary>
public sealed class PopulationSurfaceGateTests
{
    private static PopulationProvisioner Provisioner(Func<WorldSafeLoc, LiquidStatus> surface)
        => new(new ServiceCollection().BuildServiceProvider()) { SurfaceSource = surface };

    [Fact]
    public void WaterAndNonFiniteCoordinates_AreRefused()
    {
        PopulationProvisioner provisioner = Provisioner(_ => LiquidStatus.UnderWater);
        Assert.True(provisioner.TryGetUnsafeSurfaceReason(null!, new WorldSafeLoc(1, 0, 10, 10, 10, 0, "lake"), out string? reason));
        Assert.Contains("water", reason);

        provisioner = Provisioner(_ => LiquidStatus.NoWater);
        Assert.True(provisioner.TryGetUnsafeSurfaceReason(null!, new WorldSafeLoc(2, 0, float.NaN, 10, 10, 0, "nan"), out reason));
        Assert.Contains("non-finite", reason);
    }

    [Fact]
    public void ADryLocation_FarAboveOrBelowTheAdtGround_IsNotRefused()
    {
        // Undercity-like: the heightfield beneath is hundreds of yards from the graveyard; the gate must not read it at all.
        PopulationProvisioner provisioner = Provisioner(_ => LiquidStatus.NoWater);
        Assert.False(provisioner.TryGetUnsafeSurfaceReason(null!, new WorldSafeLoc(3, 0, 1586f, 239f, -62f, 0, "Undercity"), out string? reason));
        Assert.Null(reason);
        Assert.False(provisioner.TryGetUnsafeSurfaceReason(null!, new WorldSafeLoc(4, 0, -4900f, -950f, 501f, 0, "Ironforge"), out _));
    }
}
