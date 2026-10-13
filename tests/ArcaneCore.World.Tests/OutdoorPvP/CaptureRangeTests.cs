using ArcaneCore.Game.Entities;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.World.OutdoorPvP;
using Xunit;

namespace ArcaneCore.World.Tests.OutdoorPvP;

/// <summary>
/// The capture point's reach (vmangos OPvPCapturePoint::Update, ZoneScript.cpp:306-328): IsWithinDistInMap in 3D, less both bounding
/// radii; and the fallback template matches the classic-db z2815 type-29 rows of the Eastern Plaguelands towers.
/// </summary>
public sealed class CaptureRangeTests
{
    [Fact]
    public void Reach_IsTheRadiusPlusBothBoundingRadii_In3D()
    {
        float edge = 80f + OutdoorPvPWorldHost.GameObjectBoundingRadius + Player.DefaultBoundingRadius;
        Assert.True(OutdoorPvPWorldHost.InCaptureRange(edge - 0.01f, 0, 0, 80f, Player.DefaultBoundingRadius));
        Assert.True(OutdoorPvPWorldHost.InCaptureRange(80.5f, 0, 0, 80f, Player.DefaultBoundingRadius));
        Assert.False(OutdoorPvPWorldHost.InCaptureRange(edge + 0.01f, 0, 0, 80f, Player.DefaultBoundingRadius));
        Assert.False(OutdoorPvPWorldHost.InCaptureRange(60f, 0, 60f, 80f, Player.DefaultBoundingRadius)); // 84.9 yd in 3D
    }

    [Fact]
    public void Fallback_IsTheTowerTemplate()
    {
        CapturePointTemplate t = EasternPlaguelandsCatalog.FallbackTemplate;
        Assert.Equal((80f, 2426u, 2427u, 2428u, 20u, 480u, 1200u),
            (t.Radius, t.WorldStateDisplay, t.WorldStatePosition, t.WorldStateNeutral, t.NeutralPercent, t.MinTime, t.MaxTime));
    }
}
