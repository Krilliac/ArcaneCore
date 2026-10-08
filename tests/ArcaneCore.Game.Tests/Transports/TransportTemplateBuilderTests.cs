using System.Numerics;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.Transports.TransportTestKit;

namespace ArcaneCore.Game.Tests.Transports;

/// <summary>
/// vmangos <c>TransportMgr::GenerateWaypoints</c> on synthetic TaxiPathNode paths (TransportTestKit has the hand-worked times).
/// </summary>
public sealed class TransportTemplateBuilderTests
{
    [Fact]
    public void Ferry_KeyFramesSkipTheFirstAndLastNode_AndTheLastFrameTeleports()
    {
        TransportTemplate ferry = Build(Ferry);

        Assert.Equal([1u, 2u, 3u, 4u], ferry.KeyFrames.Select(k => k.Node.Index));
        Assert.Equal([false, false, false, true], ferry.KeyFrames.Select(k => k.Teleport));
        Assert.Equal([0u], ferry.MapsUsed);
        Assert.False(ferry.InInstance);
        Assert.Equal(10f, ferry.AccelTime);
        Assert.Equal(50f, ferry.AccelDist);
        Assert.True(ferry.KeyFrames[0].IsStopFrame);
        Assert.True(ferry.KeyFrames[3].IsStopFrame);
    }

    [Fact]
    public void Ferry_Times_AccelerateCruiseAndBrake_BetweenStops()
    {
        TransportTemplate ferry = Build(Ferry);
        IReadOnlyList<TransportKeyFrame> k = ferry.KeyFrames;

        // Distances along the (straight) spline.
        Assert.Equal([0f, 100f, 100f, 100f], k.Select(f => MathF.Round(f.DistFromPrev, 3)));
        Assert.Equal([0f, 100f, 200f, 0f], k.Select(f => MathF.Round(f.DistSinceStop, 3)));
        Assert.Equal([300f, 200f, 100f, 0f], k.Select(f => MathF.Round(f.DistUntilStop, 3)));

        // Wait 10 s, accelerate 10 s (50 yd), cruise 200 yd in 20 s, brake 10 s, wait 10 s.
        Assert.Equal([0u, 25000u, 35000u, 50000u], k.Select(f => f.ArriveTime));
        Assert.Equal([10000u, 25000u, 35000u, 60000u], k.Select(f => f.DepartureTime));
        Assert.Equal([25000u, 35000u, 50000u, 60000u], k.Select(f => f.NextArriveTime));
        Assert.Equal(60000u, ferry.PathTime);
    }

    [Fact]
    public void Crossing_MapChange_EndsTheFirstStretch_AndSkipsTheNodeAfterIt()
    {
        TransportTemplate crossing = Build(Crossing);
        IReadOnlyList<TransportKeyFrame> k = crossing.KeyFrames;

        // Node 4 (the last before map 1) and node 5 (the first on map 1) are not key frames; node 3 teleports.
        Assert.Equal([1u, 2u, 3u, 6u, 7u, 8u], k.Select(f => f.Node.Index));
        Assert.Equal([false, false, true, false, false, true], k.Select(f => f.Teleport));
        Assert.Equal([0u, 1u], crossing.MapsUsed);
        Assert.Same(k[0].Spline, k[2].Spline);
        Assert.Same(k[3].Spline, k[5].Spline);
        Assert.NotSame(k[0].Spline, k[3].Spline);
        Assert.Equal(0f, k[3].DistFromPrev); // reached by teleportation
        Assert.Equal([0u, 20000u, 35000u, 35000u, 55000u, 70000u], k.Select(f => f.ArriveTime));
        Assert.Equal([5000u, 20000u, 35000u, 40000u, 55000u, 70000u], k.Select(f => f.DepartureTime));
        Assert.Equal(70000u, crossing.PathTime);
    }

    [Fact]
    public void PeriodOverride_ReplacesThePathTime_AndOnlyTheLastDeparture()
    {
        TransportTemplate ferry = Build(Ferry, period: 61234);

        Assert.Equal(61234u, ferry.PathTime);
        Assert.Equal(61234u, ferry.KeyFrames[^1].DepartureTime);
        Assert.Equal(60000u, ferry.KeyFrames[^1].NextArriveTime); // set before the override, as in TransportMgr.cpp
        Assert.Equal(25000u, ferry.KeyFrames[1].ArriveTime);
    }

    [Fact]
    public void InitialOrientation_FacesAgainstTheDirectionOfTravel_AsVmangosAddsPi()
    {
        TransportTemplate ferry = Build(Ferry);

        // Travel is +x: atan2(0, +) = 0, plus pi.
        Assert.All(ferry.KeyFrames, f => Assert.Equal(MathF.PI, f.InitialOrientation, 4));
    }

    [Theory]
    [InlineData(0u, 1u, TransportTemplateError.InvalidSpeed)]
    [InlineData(10u, 0u, TransportTemplateError.InvalidSpeed)]
    public void ZeroSpeedOrAcceleration_IsRefused(uint speed, uint accel, TransportTemplateError expected)
    {
        TransportTemplate? template = TransportTemplateBuilder.Build(Ship(Ferry, FerryPath, speed, accel), Paths(), _ => false, null, out TransportTemplateError error);

        Assert.Null(template);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void UnknownPath_NotAShip_AndTooShortPath_AreRefused()
    {
        Assert.Null(TransportTemplateBuilder.Build(Ship(Ferry, 4242), Paths(), _ => false, null, out TransportTemplateError noPath));
        Assert.Equal(TransportTemplateError.NoPath, noPath);

        GameObjectTemplate chest = Ship(Ferry, FerryPath) with { Type = 3 };
        Assert.Null(TransportTemplateBuilder.Build(chest, Paths(), _ => false, null, out TransportTemplateError notShip));
        Assert.Equal(TransportTemplateError.NotAShip, notShip);

        var shortPath = new TaxiPathNodeCatalog([Node(77, 0, 0, 0), Node(77, 1, 0, 100)]);
        Assert.Null(TransportTemplateBuilder.Build(Ship(Ferry, 77), shortPath, _ => false, null, out TransportTemplateError tooShort));
        Assert.Equal(TransportTemplateError.BadPath, tooShort);
    }

    [Fact]
    public void MultiMapRoute_ThroughAnInstanceableMap_IsRefused()
    {
        Assert.Null(TransportTemplateBuilder.Build(Ship(Crossing, CrossingPath), Paths(), map => map == 1, null, out TransportTemplateError error));
        Assert.Equal(TransportTemplateError.BadPath, error);
    }

    [Fact]
    public void SingleMapRoute_OnAnInstanceableMap_IsAnInstanceRoute()
    {
        TransportTemplate? template = TransportTemplateBuilder.Build(Ship(Ferry, FerryPath), Paths(), _ => true, null, out _);

        Assert.NotNull(template);
        Assert.True(template.InInstance);
    }

    [Fact]
    public void Spline_PassesThroughItsControls_AndMeasuresAStraightLineExactly()
    {
        TransportSpline spline = TransportSpline.CatmullRom([new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(20, 0, 0)]);

        Assert.Equal(new Vector3(0, 0, 0), spline.Evaluate(1, 0f));
        Assert.Equal(new Vector3(10, 0, 0), spline.Evaluate(1, 1f));
        Assert.Equal(new Vector3(10, 0, 0), spline.Evaluate(2, 0f));
        Assert.Equal(5f, spline.Evaluate(1, 0.5f).X, 4); // evenly spaced neighbours: linear
        Assert.NotEqual(15f, spline.Evaluate(2, 0.5f).X); // the end copies its last control (InitCatmullRom): not linear there
        Assert.Equal(20.0, spline.TotalLength, 4);
        Assert.Equal(10.0, spline.Length(1, 2), 4);
        Assert.True(spline.Derivative(1, 0.5f).X > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Evaluate(3, 0f));
    }

    [Fact]
    public void NormalizeOrientation_EmulatesFmodForNegatives()
    {
        Assert.Equal(MathF.PI, TransportTemplateBuilder.NormalizeOrientation(-MathF.PI), 5);
        Assert.Equal(1f, TransportTemplateBuilder.NormalizeOrientation(1f + (2f * MathF.PI)), 5);
        Assert.Equal(0f, TransportTemplateBuilder.NormalizeOrientation(0f));
    }
}
