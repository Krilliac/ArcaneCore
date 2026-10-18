using ArcaneCore.Atlas.Core;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

public sealed class CameraMathTests
{
    private static readonly WorldPoint Inn = new(-9462.66, 16.1915, 57.0459);

    [Fact]
    public void Orbit_EyeSitsBehindAndAboveTheTarget()
    {
        var camera = new CameraState(new WorldPoint(100, 200, 10), 10, 0, 30, 55, CameraMode.Orbit);
        WorldPoint eye = CameraMath.Eye(camera);

        // Yaw 0 looks along +X, so the eye is on the -X side; pitch 30 puts it 5 yd up (10 * sin 30).
        Assert.Equal(100 - (10 * Math.Cos(Math.PI / 6)), eye.X, 9);
        Assert.Equal(200, eye.Y, 9);
        Assert.Equal(15, eye.Z, 9);
    }

    [Theory]
    [InlineData(CameraMode.Orbit)]
    [InlineData(CameraMode.Top)]
    public void Target_ProjectsToTheViewportCentre(CameraMode mode)
    {
        var camera = new CameraState(Inn, 120, 37, 40, 55, mode);

        (double x, double y, double depth) = CameraMath.Project(camera, Inn, 16.0 / 9)!.Value;

        Assert.Equal(0, x, 9);
        Assert.Equal(0, y, 9);
        Assert.InRange(depth, -1, 1);
    }

    [Fact]
    public void FacingNorth_EastIsRight_AndUpIsUp()
    {
        // Yaw 0 faces +X (north). World y points west, so east is -Y and must land right of centre.
        var camera = new CameraState(Inn, 50, 0, 10, 60, CameraMode.Orbit);

        (double eastX, _, _) = CameraMath.Project(camera, Inn with { Y = Inn.Y - 5 }, 1)!.Value;
        (_, double upY, _) = CameraMath.Project(camera, Inn with { Z = Inn.Z + 5 }, 1)!.Value;

        Assert.True(eastX > 0, $"east projected to x {eastX}");
        Assert.True(upY > 0, $"up projected to y {upY}");
    }

    [Fact]
    public void TopDown_NorthIsUp_AtYawZero()
    {
        var camera = new CameraState(Inn, 100, 0, 0, 55, CameraMode.Top);

        (double nx, double ny, _) = CameraMath.Project(camera, Inn with { X = Inn.X + 50 }, 1)!.Value;
        (double ex, double ey, _) = CameraMath.Project(camera, Inn with { Y = Inn.Y - 50 }, 1)!.Value;

        // Distance is the centre-to-top half-height: 50 yd north is half way up, 50 yd east half way right.
        Assert.Equal(0, nx, 9);
        Assert.Equal(0.5, ny, 9);
        Assert.Equal(0.5, ex, 9);
        Assert.Equal(0, ey, 9);
    }

    [Fact]
    public void PointsBehindTheCamera_DoNotProject()
    {
        var camera = new CameraState(Inn, 20, 0, 0, 55, CameraMode.Orbit);

        Assert.Null(CameraMath.Project(camera, Inn with { X = Inn.X - 100 }, 1));
    }

    [Theory]
    [InlineData(CameraMode.Orbit)]
    [InlineData(CameraMode.Fly)]
    [InlineData(CameraMode.Top)]
    public void RayThroughAProjectedPoint_PassesThroughIt(CameraMode mode)
    {
        var camera = new CameraState(Inn, 80, 123, 35, 50, mode);
        WorldPoint probe = mode == CameraMode.Fly
            ? Inn + new WorldPoint(30 * Math.Cos(2.1), 30 * Math.Sin(2.1), -15)
            : Inn + new WorldPoint(6, -4, 2);
        double aspect = 4.0 / 3;
        (double x, double y, _) = CameraMath.Project(camera, probe, aspect)!.Value;

        PickRay ray = CameraMath.RayThrough(camera, 0, x, y, aspect);

        // Distance from the probe to the ray line must be ~0.
        WorldPoint d = ray.Direction;
        double len = Math.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
        WorldPoint w = probe - ray.Origin;
        double t = ((w.X * d.X) + (w.Y * d.Y) + (w.Z * d.Z)) / (len * len);
        WorldPoint closest = ray.Origin + new WorldPoint(d.X * t, d.Y * t, d.Z * t);
        WorldPoint miss = probe - closest;
        Assert.True(t > 0, "the probe is in front of the ray origin");
        Assert.True(Math.Sqrt((miss.X * miss.X) + (miss.Y * miss.Y) + (miss.Z * miss.Z)) < 1e-6, $"missed by {miss}");
    }

    [Fact]
    public void Projection_RefusesBadAspect()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraMath.Projection(ViewState.Default.Camera, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraMath.Projection(ViewState.Default.Camera, double.NaN));
    }
}
