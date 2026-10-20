using System.Numerics;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Render;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

public sealed class CpuRendererShadingTests
{
    private static readonly Rgb BaseTerrain = Pixels.Parse("#B9A77A");

    private static int LuminanceSum(Rgb c) => c.R + c.G + c.B;

    [Fact]
    public void ShadeTriangle_SunFacingSlope_IsBrighterThanAwayFacingSlope()
    {
        // Primary sun comes from (+0.32, -0.24, +0.915): a slope tilted toward north-east (+X, -Y) faces the sun.
        var sunFacing0 = new Vector3(0f, 0f, 10f);
        var sunFacing1 = new Vector3(10f, 0f, 6f);
        var sunFacing2 = new Vector3(0f, 10f, 13f);

        var awayFacing0 = new Vector3(0f, 0f, 10f);
        var awayFacing1 = new Vector3(10f, 0f, 14f);
        var awayFacing2 = new Vector3(0f, 10f, 7f);

        Rgb sunLit = CpuRenderer.ShadeTriangle(BaseTerrain, sunFacing0, sunFacing1, sunFacing2, LayerIds.Terrain);
        Rgb shadowLit = CpuRenderer.ShadeTriangle(BaseTerrain, awayFacing0, awayFacing1, awayFacing2, LayerIds.Terrain);

        Assert.True(
            LuminanceSum(sunLit) > LuminanceSum(shadowLit) + 30,
            $"Expected sun-facing slope {sunLit} to be clearly brighter than away-facing slope {shadowLit}");
    }

    [Fact]
    public void ShadeTriangle_SteepCliffSlope_IsDarkenedComparedToGentleSlope()
    {
        // Both triangles slope along the same azimuth, but the cliff has |n.Z| < 0.5 while the gentle slope has |n.Z| > 0.8.
        var gentle0 = new Vector3(0f, 0f, 0f);
        var gentle1 = new Vector3(10f, 0f, 0f);
        var gentle2 = new Vector3(0f, 10f, -3f);

        var cliff0 = new Vector3(0f, 0f, 0f);
        var cliff1 = new Vector3(10f, 0f, 0f);
        var cliff2 = new Vector3(0f, 2f, -10f);

        Rgb gentle = CpuRenderer.ShadeTriangle(BaseTerrain, gentle0, gentle1, gentle2, LayerIds.Terrain);
        Rgb cliff = CpuRenderer.ShadeTriangle(BaseTerrain, cliff0, cliff1, cliff2, LayerIds.Terrain);

        Assert.True(
            LuminanceSum(cliff) < LuminanceSum(gentle) - 40,
            $"Expected steep cliff {cliff} (|n.Z| < 0.5) to be darkened relative to gentle slope {gentle}");
    }

    [Fact]
    public void ShadeTriangle_HypsometricElevationModulation_BrightensHigherTerrain()
    {
        var low0 = new Vector3(0f, 0f, -250f);
        var low1 = new Vector3(10f, 0f, -250f);
        var low2 = new Vector3(0f, 10f, -250f);

        var mid0 = new Vector3(0f, 0f, 0f);
        var mid1 = new Vector3(10f, 0f, 0f);
        var mid2 = new Vector3(0f, 10f, 0f);

        var high0 = new Vector3(0f, 0f, 250f);
        var high1 = new Vector3(10f, 0f, 250f);
        var high2 = new Vector3(0f, 10f, 250f);

        Rgb low = CpuRenderer.ShadeTriangle(BaseTerrain, low0, low1, low2, LayerIds.Terrain);
        Rgb mid = CpuRenderer.ShadeTriangle(BaseTerrain, mid0, mid1, mid2, LayerIds.Terrain);
        Rgb high = CpuRenderer.ShadeTriangle(BaseTerrain, high0, high1, high2, LayerIds.Terrain);

        Assert.True(LuminanceSum(low) < LuminanceSum(mid), $"Expected basin {low} to be darker than sea-level {mid}");
        Assert.True(LuminanceSum(mid) < LuminanceSum(high), $"Expected sea-level {mid} to be darker than highland {high}");
    }

    [Fact]
    public void ShadeTriangle_IsWindingOrderIndependent()
    {
        var a = new Vector3(0f, 0f, 20f);
        var b = new Vector3(10f, 0f, 25f);
        var c = new Vector3(0f, 10f, 18f);

        Rgb ccw = CpuRenderer.ShadeTriangle(BaseTerrain, a, b, c, LayerIds.Terrain);
        Rgb cw = CpuRenderer.ShadeTriangle(BaseTerrain, a, c, b, LayerIds.Terrain);

        Assert.Equal(ccw, cw);
    }

    [Theory]
    [InlineData(CameraMode.Top, 32.0)]
    [InlineData(CameraMode.Top, 250.0)]
    [InlineData(CameraMode.Orbit, 120.0)]
    public void Hud_DrawScaleBar_PaintsYardScaleBarIn2DAnd3DModes(CameraMode mode, double distance)
    {
        var fb = new Framebuffer(320, 240, Rgb.Background);
        var camera = new CameraState(new WorldPoint(100, 200, 0), distance, 0, 35, 55, mode);

        Hud.DrawScaleBar(fb, camera);

        int inkPixels = 0;
        for (int y = 120; y < 240; y++)
        {
            for (int x = 0; x < 220; x++)
            {
                if (fb.Get(x, y) == Hud.Ink)
                {
                    inkPixels++;
                }
            }
        }

        Assert.True(inkPixels > 30, $"Expected scale bar ink pixels in bottom-left for {mode} at distance {distance}, got {inkPixels}");
    }

    [Fact]
    public void Hud_DrawScaleBar_SkipsFramesSmallerThan240x120()
    {
        var smallWidth = new Framebuffer(200, 160, Rgb.Background);
        var smallHeight = new Framebuffer(320, 100, Rgb.Background);

        Hud.DrawScaleBar(smallWidth, Views.TopCamera);
        Hud.DrawScaleBar(smallHeight, Views.TopCamera);

        Assert.All(smallWidth.Pixels.Chunk(4), p => Assert.Equal(Rgb.Background, new Rgb(p[0], p[1], p[2])));
        Assert.All(smallHeight.Pixels.Chunk(4), p => Assert.Equal(Rgb.Background, new Rgb(p[0], p[1], p[2])));
    }
}
