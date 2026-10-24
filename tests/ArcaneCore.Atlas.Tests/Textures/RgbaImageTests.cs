using ArcaneCore.Atlas.Textures;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Textures;

/// <summary>
/// <see cref="RgbaImage.Sample"/> is what the CPU renderer reads textures with. Texel centres sit at <c>(i + 0.5) / size</c>,
/// the way GL and the browser sample, so a CPU render and the WebGL render of the same material agree. Values are chosen so
/// bilinear results are exact (0 and 200 average to 100).
/// </summary>
public sealed class RgbaImageTests
{
    // 2x1: texel 0 = (0,10,20,255), texel 1 = (200,110,120,255).
    private static RgbaImage TwoTexels() => new(2, 1, [0, 10, 20, 255, 200, 110, 120, 255]);

    [Fact]
    public void Constructor_RejectsAPixelArrayOfTheWrongLength()
    {
        Assert.ThrowsAny<ArgumentException>(() => new RgbaImage(2, 2, new byte[15]));
        Assert.ThrowsAny<ArgumentException>(() => new RgbaImage(0, 2, []));
    }

    [Fact]
    public void Get_ReadsRowMajorRgba()
    {
        RgbaImage image = TwoTexels();

        Assert.Equal(new Rgba(200, 110, 120, 255), image.Get(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Get(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Get(0, 1));
    }

    [Theory]
    [InlineData(0.10, 0)]
    [InlineData(0.49, 0)]
    [InlineData(0.51, 200)]
    [InlineData(0.99, 200)]
    public void Nearest_PicksTheTexelThatContainsThePoint(double u, int expectedRed)
    {
        Rgba sample = TwoTexels().Sample(u, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Nearest);

        Assert.Equal(expectedRed, sample.R);
    }

    [Fact]
    public void Nearest_Repeat_WrapsPastOneAndBelowZero_ClampHoldsTheEdge()
    {
        RgbaImage image = TwoTexels();

        Assert.Equal(0, image.Sample(1.25, 0.5, TextureWrap.Repeat, TextureWrap.Repeat, TextureFilter.Nearest).R);
        Assert.Equal(200, image.Sample(-0.25, 0.5, TextureWrap.Repeat, TextureWrap.Repeat, TextureFilter.Nearest).R);
        Assert.Equal(0, image.Sample(1.0, 0.5, TextureWrap.Repeat, TextureWrap.Repeat, TextureFilter.Nearest).R);
        Assert.Equal(200, image.Sample(1.5, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Nearest).R);
        Assert.Equal(0, image.Sample(-0.5, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Nearest).R);
    }

    [Fact]
    public void Bilinear_AtTexelCentres_IsExact_AndHalfwayAveragesTheNeighbours()
    {
        RgbaImage image = TwoTexels();

        Assert.Equal(new Rgba(0, 10, 20, 255), image.Sample(0.25, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Bilinear));
        Assert.Equal(new Rgba(200, 110, 120, 255), image.Sample(0.75, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Bilinear));
        Assert.Equal(new Rgba(100, 60, 70, 255), image.Sample(0.5, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Bilinear));
    }

    [Fact]
    public void Bilinear_AtTheSeam_BlendsAcrossForRepeat_AndStaysPutForClamp()
    {
        RgbaImage image = TwoTexels();

        // u = 0 is half a texel left of texel 0's centre: Repeat blends with texel 1 (the wrapped neighbour), Clamp repeats texel 0.
        Assert.Equal(100, image.Sample(0.0, 0.5, TextureWrap.Repeat, TextureWrap.Repeat, TextureFilter.Bilinear).R);
        Assert.Equal(0, image.Sample(0.0, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Bilinear).R);
        Assert.Equal(200, image.Sample(1.0, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Bilinear).R);
    }

    [Fact]
    public void Bilinear_AcrossRows_InterpolatesVertically()
    {
        // 1x2: top (0,0,0,255), bottom (200,200,200,255); v = 0.5 is the seam between the two texel centres.
        var image = new RgbaImage(1, 2, [0, 0, 0, 255, 200, 200, 200, 255]);

        Assert.Equal(100, image.Sample(0.5, 0.5, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Bilinear).G);
        Assert.Equal(0, image.Sample(0.5, 0.25, TextureWrap.Clamp, TextureWrap.Clamp, TextureFilter.Bilinear).G);
    }
}
