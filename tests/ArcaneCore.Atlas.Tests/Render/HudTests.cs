using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Render;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

public sealed class BitmapFontTests
{
    private static string Rows(char c)
    {
        byte[] glyph = BitmapFont.Glyph(c).ToArray();
        return string.Join('/', Enumerable.Range(0, BitmapFont.GlyphHeight).Select(row =>
            new string(Enumerable.Range(0, BitmapFont.GlyphWidth).Select(col => (glyph[row] & (1 << (BitmapFont.GlyphWidth - 1 - col))) != 0 ? '#' : '.').ToArray())));
    }

    [Fact]
    public void EveryPrintableAsciiCharacter_HasAGlyph_AndOnlySpaceIsBlank()
    {
        Assert.Equal(95, BitmapFont.Last - BitmapFont.First + 1);
        for (char c = BitmapFont.First; c <= BitmapFont.Last; c++)
        {
            Assert.True(BitmapFont.Has(c));
            bool blank = BitmapFont.Glyph(c).ToArray().All(r => r == 0);
            Assert.Equal(c == ' ', blank);
        }
    }

    [Fact]
    public void NoTwoGlyphsLookAlike()
    {
        var seen = new Dictionary<string, char>();
        for (char c = BitmapFont.First; c <= BitmapFont.Last; c++)
        {
            string rows = Rows(c);
            Assert.True(seen.TryAdd(rows, c), $"'{c}' and '{(seen.TryGetValue(rows, out char other) ? other : ' ')}' have the same glyph");
        }
    }

    [Fact]
    public void Glyphs_HaveTheDrawnShapes()
    {
        Assert.Equal(".###./#...#/#...#/#####/#...#/#...#/#...#", Rows('A'));
        Assert.Equal(".###./..#../..#../..#../..#../..#../.###.", Rows('I'));
        Assert.Equal("..#../.##../..#../..#../..#../..#../.###.", Rows('1'));
        Assert.Equal("...../...../...../#####/...../...../.....", Rows('-'));
        Assert.Equal("...../...../...../...../...../.##../.##..", Rows('.'));
    }

    [Fact]
    public void CharactersOutsidePrintableAscii_DrawAsAQuestionMark()
    {
        Assert.Equal(Rows('?'), Rows('é'));
        Assert.Equal(Rows('?'), Rows('\t'));
        Assert.False(BitmapFont.Has('é'));
    }

    [Fact]
    public void Draw_PaintsExactlyTheGlyphPixels_AtTheScale()
    {
        var frame = new Framebuffer(40, 30, Rgb.Background);
        var ink = new Rgb(1, 2, 3);

        int end = BitmapFont.Draw(frame, 4, 5, "A", 2, ink);

        Assert.Equal(4 + (BitmapFont.Advance * 2), end);
        ReadOnlySpan<byte> glyph = BitmapFont.Glyph('A');
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                int col = (x - 4) / 2;
                int row = (y - 5) / 2;
                bool inCell = x >= 4 && y >= 5 && col < BitmapFont.GlyphWidth && row < BitmapFont.GlyphHeight;
                bool lit = inCell && (glyph[row] & (1 << (BitmapFont.GlyphWidth - 1 - col))) != 0;
                Assert.Equal(lit ? ink : Rgb.Background, frame.Get(x, y));
            }
        }
    }

    [Fact]
    public void Measure_CountsAdvancesWithoutTheTrailingBlankColumn()
    {
        Assert.Equal(0, BitmapFont.Measure(string.Empty, 3));
        Assert.Equal(((2 * 6) - 1) * 3, BitmapFont.Measure("AB", 3));
    }

    [Fact]
    public void Draw_ClipsAtTheEdgesOfTheFrame()
    {
        var frame = new Framebuffer(16, 16, Rgb.Background);

        BitmapFont.Draw(frame, -3, 12, "WWW", 2, new Rgb(255, 255, 255));
        BitmapFont.Draw(frame, 10, -4, "WWW", 2, new Rgb(255, 255, 255));

        Assert.Equal(16 * 16 * 4, frame.Pixels.Length);
    }
}

public sealed class HudTests
{
    private static readonly WorldPoint Inn = new(-9462.66, 16.1915, 57.0459);

    [Theory]
    [InlineData(0.0, 1.0, 0.0, 0.0, 1.0)]
    [InlineData(0.0, 0.0, 1.0, -1.0, 0.0)]
    [InlineData(0.0, 0.0, -1.0, 1.0, 0.0)]
    [InlineData(0.0, -1.0, 0.0, 0.0, -1.0)]
    [InlineData(90.0, 1.0, 0.0, 1.0, 0.0)]
    [InlineData(180.0, 1.0, 0.0, 0.0, -1.0)]
    public void Compass_PutsTheCameraHeadingAtTheTop(double yaw, double worldX, double worldY, double right, double up)
    {
        (double r, double u) = Hud.CompassOffset(yaw, worldX, worldY);

        Assert.Equal(right, r, 9);
        Assert.Equal(up, u, 9);
    }

    [Fact]
    public void NiceLength_PicksTheLargestOneTwoFiveBarThatFits()
    {
        Assert.Equal(20.0, Hud.NiceLength(3.75, 96, 4));
        Assert.Equal(50.0, Hud.NiceLength(2.0, 100, 4));
        Assert.Equal(0.5, Hud.NiceLength(100, 60, 4));
        Assert.Equal(10000.0, Hud.NiceLength(0.01, 100, 4));
        Assert.Null(Hud.NiceLength(double.NaN, 100, 4));
        Assert.Null(Hud.NiceLength(0, 100, 4));
        Assert.Null(Hud.NiceLength(1e6, 100, 4));
        Assert.Null(Hud.NiceLength(1e-9, 100, 4));
    }

    [Fact]
    public void InfoLines_NameTheCoordinatesAndTheTileFile()
    {
        ViewState view = ViewState.Default with { Camera = ViewState.Default.Camera with { Target = Inn } };

        (string coordinates, string tile) = Hud.InfoLines(view);

        Assert.Equal("map 0  x -9462.66  y 16.19  z 57.05", coordinates);
        Assert.Equal("tile 49,31  0004931.map", tile);
        Assert.Equal("0004931.map", WorldGrid.MapFileName(new TileKey(0, 49, 31)));
    }

    [Fact]
    public void InfoLines_SayWhenTheTargetIsOffTheGrid()
    {
        ViewState view = ViewState.Default with { Camera = ViewState.Default.Camera with { Target = new WorldPoint(1e7, 0, 0) } };

        Assert.Equal("tile outside the 64x64 grid", Hud.InfoLines(view).Tile);
    }

    private static int Count(RenderedFrame frame, Rgb colour, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (frame.Buffer.Get(x, y) == colour)
                {
                    n++;
                }
            }
        }

        return n;
    }

    private static RenderedFrame Render(CameraState camera, bool hud, int radius = 0, bool withTile = true)
    {
        FakeSource source = new FakeSource().Serves(LayerIds.Terrain);
        if (withTile)
        {
            TileKey tile = Views.TileOf(camera);
            source.Add(LayerIds.Terrain, tile, Geometry.Rect(LayerIds.Terrain, tile, 90, 110, 190, 210, 0));
        }

        ViewState view = Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Wire))) with { ShowHud = hud, LoadRadiusTiles = radius };
        return new RenderRig(source).Frame(view, 320, 240);
    }

    [Fact]
    public void WithoutTheHud_NothingIsDrawnOverTheChart()
    {
        RenderedFrame frame = Render(Views.TopCamera with { Target = new WorldPoint(1e7, 0, 0) }, hud: false, withTile: false);

        Assert.All(frame.Buffer.Pixels.Chunk(4), p => Assert.Equal(Rgb.Background, new Rgb(p[0], p[1], p[2])));
    }

    [Fact]
    public void TheInfoBlock_IsInkOnAPanel_AtTheTopLeft()
    {
        RenderedFrame frame = Render(Views.TopCamera, hud: true);

        Assert.True(Count(frame, Hud.Ink, 0, 0, 120, 24) > 20);
        Assert.NotEqual(Rgb.Background, frame.Buffer.Get(4, 4));
    }

    [Fact]
    public void TheCompass_PutsNorthAtTheTop_ThenFollowsTheYaw()
    {
        // 320 x 240: scale 1, compass centre (295, 25), label radius 18.
        RenderedFrame north = Render(Views.TopCamera with { Yaw = 0 }, hud: true);
        RenderedFrame west = Render(Views.TopCamera with { Yaw = 90 }, hud: true);

        Assert.True(Count(north, Hud.Red, 285, 0, 305, 14) >= 5, "north letter at the top");
        Assert.Equal(0, Count(west, Hud.Red, 285, 0, 305, 13));
        Assert.True(Count(west, Hud.Red, 308, 20, 320, 32) >= 5, "north letter at the right when facing west");
    }

    [Fact]
    public void TheScaleBar_IsAsLongAsTheNiceLengthSaysItIs()
    {
        // Top camera, distance 32 on a 240 pixel high image: 3.75 pixels per yard, so the bar for 20 yards is 75 pixels.
        RenderedFrame frame = Render(Views.TopCamera, hud: true);

        int longest = 0;
        for (int y = 120; y < 240; y++)
        {
            int run = 0;
            for (int x = 0; x < 160; x++)
            {
                run = frame.Buffer.Get(x, y) == Hud.Ink ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }

        Assert.Equal(75, longest);
    }

    [Fact]
    public void TheFooter_ColoursMissingTilesAmber_OnlyWhenThereAreSome()
    {
        RenderedFrame complete = Render(Views.TopCamera, hud: true);
        RenderedFrame gaps = Render(Views.TopCamera, hud: true, radius: 1);

        int footerTop = 240 - Hud.FooterHeight(1);
        Assert.Equal(0, Count(complete, Hud.Amber, 0, footerTop, 320, 240));
        Assert.True(Count(gaps, Hud.Amber, 0, footerTop, 320, 240) > 10);
        Assert.NotEqual(Rgb.Background, gaps.Buffer.Get(2, 239));
    }

    [Fact]
    public void TheSplitDivider_IsDrawnInTheAccentColour()
    {
        var rig = new RenderRig(new FakeSource().Serves(LayerIds.Terrain));
        ViewState view = Views.Of(Views.TopCamera, (LayerIds.Terrain, Views.Style())) with { ShowHud = true, Compare = CompareMode.Split, SplitPosition = 0.25 };

        RenderedFrame frame = rig.Frame(view, 320, 240);

        Assert.Equal(Hud.Accent, frame.Buffer.Get(80, 120));
    }

    [Fact]
    public void HudSize_FollowsTheImageHeight()
    {
        Assert.Equal(1, Hud.ScaleFor(100));
        Assert.Equal(2, Hud.ScaleFor(720));
        Assert.Equal(3, Hud.ScaleFor(1080));
        Assert.Equal(4, Hud.ScaleFor(4000));
        Assert.Equal(20, Hud.FooterHeight(1));
    }
}
