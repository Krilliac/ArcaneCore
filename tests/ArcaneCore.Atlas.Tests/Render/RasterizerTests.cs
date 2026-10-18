using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Render;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

public sealed class RasterizerTests
{
    private const int Size = 64;

    private static readonly Rgb Terrain = Pixels.Parse("#B9A77A");
    private static readonly Rgb Marker = Pixels.Parse("#F2A541");

    private static Rgb Flat(Rgb layer) => Shading.Fill(layer, 0, 0, 1, false);

    private static TileKey Tile => Views.TileOf(Views.TopCamera);

    [Fact]
    public void BackgroundIsTheChartColour()
    {
        var rig = new RenderRig(new FakeSource());

        RenderedFrame frame = rig.Frame(Views.Of(Views.TopCamera), Size, Size);

        Assert.Equal(new Rgb(0x10, 0x1A, 0x21), frame.Buffer.Get(0, 0));
        Assert.Equal(new Rgb(0x10, 0x1A, 0x21), frame.Buffer.Get(Size - 1, Size - 1));
    }

    [Theory]
    [InlineData(CameraMode.Top)]
    [InlineData(CameraMode.Orbit)]
    [InlineData(CameraMode.Fly)]
    public void FilledQuad_HasTheLayerColourTimesTheLight_AtProjectedPoints(CameraMode mode)
    {
        CameraState camera = mode switch
        {
            CameraMode.Top => Views.TopCamera,
            CameraMode.Orbit => new CameraState(new WorldPoint(100, 200, 0), 60, 30, 50, 55, CameraMode.Orbit),
            _ => new CameraState(new WorldPoint(100, 200, 40), 1, 0, 90 - 1, 55, CameraMode.Fly),
        };
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, Views.TileOf(camera), Geometry.Rect(LayerIds.Terrain, Views.TileOf(camera), 85, 115, 185, 215, 0)));
        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style())), Size, Size);

        // The centre of the quad, and a point part way to each of its corners, project inside it.
        foreach ((double dx, double dy) in new[] { (0.0, 0.0), (10, 10), (-10, 10), (10, -10), (-10, -10) })
        {
            var world = new WorldPoint(100 + dx, 200 + dy, 0);
            (int x, int y) = Pixels.Of(camera, world, Size, Size);
            Assert.Equal(Flat(Terrain), frame.Buffer.Get(x, y));
        }

        // Well outside the quad's projection nothing is drawn.
        Assert.Equal(Rgb.Background, frame.Buffer.Get(0, 0));
    }

    [Fact]
    public void TopDown_NorthIsUp_AndEastIsRight()
    {
        CameraState camera = Views.TopCamera;
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 118, 126, 192, 208, 0))
            .Add(LayerIds.VMapM2, Tile, Geometry.Rect(LayerIds.VMapM2, Tile, 92, 108, 168, 176, 0)));
        RenderedFrame frame = rig.Frame(
            Views.Of(camera, (LayerIds.Terrain, Views.Style()), (LayerIds.VMapM2, Views.Style("#F2A541"))),
            Size,
            Size);

        // x 118..126 is 18..26 yards north of the target: above the centre row, horizontally centred.
        (int northX, int northY) = Pixels.Of(camera, new WorldPoint(122, 200, 0), Size, Size);
        Assert.True(northY < Size / 2, $"north should be above the centre, got row {northY}");
        Assert.Equal(Flat(Terrain), frame.Buffer.Get(northX, northY));
        Assert.Equal(Rgb.Background, frame.Buffer.Get(northX, Size - 1 - northY));

        // y 168..176 is 24..32 yards EAST of the target (world y points west): right of the centre column.
        (int eastX, int eastY) = Pixels.Of(camera, new WorldPoint(100, 172, 0), Size, Size);
        Assert.True(eastX > Size / 2, $"east should be right of the centre, got column {eastX}");
        Assert.Equal(Flat(Marker), frame.Buffer.Get(eastX, eastY));
        Assert.Equal(Rgb.Background, frame.Buffer.Get(Size - 1 - eastX, eastY));
    }

    [Fact]
    public void TopDown_YawTurnsTheMapUnderTheCamera()
    {
        // Yaw 90 makes the screen's up axis point west (+Y): a quad to the west appears above the centre.
        var camera = Views.TopCamera with { Yaw = 90 };
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 92, 108, 218, 226, 0)));
        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style())), Size, Size);

        (int x, int y) = Pixels.Of(camera, new WorldPoint(100, 222, 0), Size, Size);

        Assert.True(y < Size / 2);
        Assert.Equal(Flat(Terrain), frame.Buffer.Get(x, y));
    }

    [Fact]
    public void FillDepth_IsTheDistanceFromTheEye()
    {
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 80, 120, 180, 220, 7)));
        RenderedFrame frame = rig.Frame(Views.Of(Views.TopCamera, (LayerIds.Terrain, Views.Style())), Size, Size);

        // The top-down eye is CameraMath.TopEyeHeight above the target (z 0); the quad is at z 7.
        Assert.Equal(CameraMath.TopEyeHeight - 7, frame.Buffer.FillDepth[(Size / 2 * Size) + (Size / 2)], 2);
    }

    [Fact]
    public void WireLine_BehindAFill_IsHidden_AndVisibleWhenXRay()
    {
        CameraState camera = Views.TopCamera;
        WorldPoint left = Pixels.TopDownWorld(camera, Size, Size, 26, 32, -10);
        WorldPoint right = Pixels.TopDownWorld(camera, Size, Size, 38, 32, -10);
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 80, 120, 180, 220, 0))
            .Add(LayerIds.VMapM2, Tile, Geometry.Segment(LayerIds.VMapM2, Tile, left, right)));

        RenderedFrame wire = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Fill)), (LayerIds.VMapM2, Views.Style("#F2A541", RenderMode.Wire))), Size, Size);
        RenderedFrame xray = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Fill)), (LayerIds.VMapM2, Views.Style("#F2A541", RenderMode.XRay))), Size, Size);

        // The line sits 10 yards under the filled quad. Hidden-line mode shows the fill; x-ray draws the line over it.
        Assert.Equal(Flat(Terrain), wire.Buffer.Get(32, 32));
        Assert.Equal(Marker, xray.Buffer.Get(32, 32));
    }

    [Fact]
    public void WireLine_BehindAWireOnlyLayer_IsAlsoHidden()
    {
        // Hidden-line mode: a layer drawn as wire still occludes (its triangles are drawn depth-only).
        CameraState camera = Views.TopCamera;
        WorldPoint left = Pixels.TopDownWorld(camera, Size, Size, 26, 36, -10);
        WorldPoint right = Pixels.TopDownWorld(camera, Size, Size, 38, 36, -10);
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 80, 120, 180, 220, 0))
            .Add(LayerIds.VMapM2, Tile, Geometry.Segment(LayerIds.VMapM2, Tile, left, right)));

        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Wire)), (LayerIds.VMapM2, Views.Style("#F2A541", RenderMode.Wire))), Size, Size);
        RenderedFrame withoutTerrain = rig.Frame(Views.Of(camera, (LayerIds.VMapM2, Views.Style("#F2A541", RenderMode.Wire))), Size, Size);

        // (30, 36) is on the hidden line and off the quad's own diagonal edge; without the quad the line is plainly there.
        Assert.Equal(Marker, withoutTerrain.Buffer.Get(30, 36));
        Assert.Equal(Rgb.Background, frame.Buffer.Get(30, 36));
    }

    [Fact]
    public void WireLine_InFrontOfAFill_IsVisible()
    {
        CameraState camera = Views.TopCamera;
        WorldPoint left = Pixels.TopDownWorld(camera, Size, Size, 26, 32, 10);
        WorldPoint right = Pixels.TopDownWorld(camera, Size, Size, 38, 32, 10);
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 80, 120, 180, 220, 0))
            .Add(LayerIds.VMapM2, Tile, Geometry.Segment(LayerIds.VMapM2, Tile, left, right)));

        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Fill)), (LayerIds.VMapM2, Views.Style("#F2A541", RenderMode.Wire))), Size, Size);

        Assert.Equal(Marker, frame.Buffer.Get(32, 32));
    }

    [Fact]
    public void LineWidth_IsAboutOneAndAHalfPixels()
    {
        CameraState camera = Views.TopCamera;
        WorldPoint left = Pixels.TopDownWorld(camera, Size, Size, 10, 32, 0);
        WorldPoint right = Pixels.TopDownWorld(camera, Size, Size, 50, 32, 0);
        var rig = new RenderRig(new FakeSource().Add(LayerIds.VMapM2, Tile, Geometry.Segment(LayerIds.VMapM2, Tile, left, right)));
        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.VMapM2, Views.Style("#FFFFFF", RenderMode.Wire))), Size, Size);

        // Coverage along a column through a horizontal line centred on pixel row 32: full at the axis, partial next to it.
        double Coverage(int row) => (frame.Buffer.Get(30, row).R - (double)Rgb.Background.R) / (255 - Rgb.Background.R);
        double sum = Enumerable.Range(28, 9).Sum(Coverage);

        Assert.Equal(1.0, Coverage(32), 6);
        Assert.InRange(sum, 1.4, 1.6);
        Assert.Equal(0.0, Coverage(28), 6);
        Assert.Equal(0.0, Coverage(36), 6);
    }

    [Fact]
    public void Wire_EdgesOfATiltedSurface_AreNotHiddenByItsOwnDepth()
    {
        // A planar slope seen obliquely: every edge lies in the plane of the triangles that own it, so the slope-scaled
        // depth bias must let each edge through even though a pixel next to the axis is nearer or farther than the axis.
        var camera = new CameraState(new WorldPoint(100, 200, 4), 60, 20, 35, 55, CameraMode.Orbit);
        var builder = new GeometryBuilder(LayerIds.Terrain, Tile, new WorldPoint(0, 0, 0));
        const int n = 4;
        uint[,] v = new uint[n + 1, n + 1];
        for (int i = 0; i <= n; i++)
        {
            for (int j = 0; j <= n; j++)
            {
                double x = 88 + (i * 6.0);
                double y = 188 + (j * 6.0);
                v[i, j] = builder.AddVertex(x, y, (0.35 * (x - 100)) + (0.2 * (y - 200)));
            }
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                builder.AddTriangle(v[i, j], v[i + 1, j], v[i + 1, j + 1]);
                builder.AddTriangle(v[i, j], v[i + 1, j + 1], v[i, j + 1]);
            }
        }

        GeometryChunk chunk = builder.Build();
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, Tile, chunk));
        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style("#FFFFFF", RenderMode.Wire))), 256, 256);

        int checkedEdges = 0;
        for (int e = 0; e < chunk.LineCount; e++)
        {
            WorldPoint a = chunk.Vertex(chunk.Lines[2 * e]);
            WorldPoint b = chunk.Vertex(chunk.Lines[(2 * e) + 1]);
            var mid = new WorldPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
            (int x, int y) = Pixels.Of(camera, mid, 256, 256);
            Rgb pixel = frame.Buffer.Get(x, y);
            Assert.True(Pixels.Distance(pixel, Rgb.Background) > 150, $"edge {e} midpoint pixel ({x},{y}) is {pixel}, the edge was hidden by its own surface");
            checkedEdges++;
        }

        Assert.Equal(n * n * 3 + (2 * n), checkedEdges);
    }

    [Fact]
    public void Both_DrawsADimmedFill_WithFullColourEdgesOnTop()
    {
        CameraState camera = Views.TopCamera;
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 90, 110, 190, 210, 0)));
        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Both))), Size, Size);

        // Inside, off the diagonal: the fill at 55% of the shaded colour. On the quad's diagonal (through the centre) and on its side edge: the full layer colour.
        Assert.Equal(Shading.Fill(Terrain, 0, 0, 1, true), frame.Buffer.Get(28, 36));
        Assert.Equal(Terrain, frame.Buffer.Get(32, 32));
        (int ex, int ey) = Pixels.Of(camera, new WorldPoint(100, 190, 0), Size, Size);
        Assert.True(Pixels.Distance(frame.Buffer.Get(ex, ey), Terrain) < Pixels.Distance(frame.Buffer.Get(ex, ey), Rgb.Background));
    }

    [Fact]
    public void Opacity_BlendsFillsAndLines()
    {
        CameraState camera = Views.TopCamera;
        WorldPoint left = Pixels.TopDownWorld(camera, Size, Size, 4, 8, 0);
        WorldPoint right = Pixels.TopDownWorld(camera, Size, Size, 14, 8, 0);
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 90, 110, 190, 210, 0))
            .Add(LayerIds.VMapM2, Tile, Geometry.Segment(LayerIds.VMapM2, Tile, left, right)));

        RenderedFrame frame = rig.Frame(
            Views.Of(camera, (LayerIds.Terrain, Views.Style(opacity: 0.5)), (LayerIds.VMapM2, Views.Style("#F2A541", RenderMode.Wire, 0.25))),
            Size,
            Size);

        Assert.Equal(Rgb.Background.Mixed(Flat(Terrain), 0.5), frame.Buffer.Get(32, 32));
        Assert.Equal(Rgb.Background.Mixed(Marker, 0.25), frame.Buffer.Get(8, 8));
    }

    [Fact]
    public void TranslucentFill_IsBlendedOnce_AcrossASharedDiagonal()
    {
        // Pixel-aligned quad whose diagonal passes exactly through pixel centres: the top-left rule must give every
        // diagonal pixel to exactly one of the two triangles, so no pixel is blended twice and none is skipped.
        CameraState camera = Views.TopCamera;
        WorldPoint corner0 = Pixels.TopDownWorld(camera, Size, Size, 10, 10, 0);
        double ax = corner0.X + 0.5;
        double ay = corner0.Y + 0.5;
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, ax - 30, ax, ay - 30, ay, 0)));
        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style(opacity: 0.5))), Size, Size);

        Rgb once = Rgb.Background.Mixed(Flat(Terrain), 0.5);
        int painted = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Rgb pixel = frame.Buffer.Get(x, y);
                if (pixel != Rgb.Background)
                {
                    Assert.Equal(once, pixel);
                    painted++;
                }
            }
        }

        Assert.Equal(30 * 30, painted);
    }

    [Fact]
    public void ColourByAttribute_UsesTheColourKeyOfEachPrimitive()
    {
        CameraState camera = Views.TopCamera;
        IReadOnlyDictionary<uint, JsonObject> keys = Geometry.Keys((1, ColorKeys.Indoor), (2, ColorKeys.Outdoor), (3, "no-such-key"));
        var builder = new GeometryBuilder(LayerIds.VMapWmo, Tile, new WorldPoint(0, 0, 0));
        void AddRect(double y0, double y1, uint attribute)
        {
            uint a = builder.AddVertex(90, y0, 0);
            uint b = builder.AddVertex(110, y0, 0);
            uint c = builder.AddVertex(110, y1, 0);
            uint d = builder.AddVertex(90, y1, 0);
            builder.AddTriangle(a, b, c, attribute);
            builder.AddTriangle(a, c, d, attribute);
        }

        AddRect(180, 188, 1);
        AddRect(196, 204, 2);
        AddRect(212, 220, 3);
        foreach ((uint id, JsonObject table) in keys)
        {
            builder.DescribeAttribute(id, () => table);
        }

        var rig = new RenderRig(new FakeSource().Add(LayerIds.VMapWmo, Tile, builder.Build()));
        Rgb Pixel(RenderedFrame f, double y)
        {
            (int px, int py) = Pixels.Of(camera, new WorldPoint(100, y, 0), Size, Size);
            return f.Buffer.Get(px, py);
        }

        RenderedFrame keyed = rig.Frame(Views.Of(camera, (LayerIds.VMapWmo, Views.Style("#808080", byAttribute: true))), Size, Size);
        RenderedFrame plain = rig.Frame(Views.Of(camera, (LayerIds.VMapWmo, Views.Style("#808080", byAttribute: false))), Size, Size);

        Assert.Equal(Flat(Pixels.Parse("#D9C9A3")), Pixel(keyed, 184));
        Assert.Equal(Flat(Pixels.Parse("#E8E3D9")), Pixel(keyed, 200));
        Assert.Equal(Flat(Pixels.Parse("#808080")), Pixel(keyed, 216));
        Assert.Equal(Flat(Pixels.Parse("#808080")), Pixel(plain, 184));
        Assert.Equal(Flat(Pixels.Parse("#808080")), Pixel(plain, 200));
    }

    [Fact]
    public void NearerFill_Wins_WhateverTheDrawOrder()
    {
        CameraState camera = Views.TopCamera;
        TileKey tile = Tile;
        // vmap.wmo is drawn after terrain. Above the terrain it wins; below it the terrain wins.
        var above = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, tile, Geometry.Rect(LayerIds.Terrain, tile, 80, 120, 180, 220, 0))
            .Add(LayerIds.VMapWmo, tile, Geometry.Rect(LayerIds.VMapWmo, tile, 90, 110, 190, 210, 5)));
        var below = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, tile, Geometry.Rect(LayerIds.Terrain, tile, 80, 120, 180, 220, 0))
            .Add(LayerIds.VMapWmo, tile, Geometry.Rect(LayerIds.VMapWmo, tile, 90, 110, 190, 210, -5)));
        ViewState view = Views.Of(camera, (LayerIds.Terrain, Views.Style("#B9A77A")), (LayerIds.VMapWmo, Views.Style("#E8E3D9")));

        Assert.Equal(Flat(Pixels.Parse("#E8E3D9")), above.Frame(view, Size, Size).Buffer.Get(32, 32));
        Assert.Equal(Flat(Terrain), below.Frame(view, Size, Size).Buffer.Get(32, 32));
    }

    [Fact]
    public void TriangleCrossingTheCameraPlane_IsClippedNotDropped()
    {
        // The ground reaches 1000 yards behind the eye. Without near-plane clipping those vertices make the quad vanish or smear.
        var camera = new CameraState(new WorldPoint(100, 200, 0), 20, 0, 30, 55, CameraMode.Orbit);
        TileKey tile = Views.TileOf(camera);
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, tile, Geometry.Rect(LayerIds.Terrain, tile, -900, 1100, -800, 1200, 0)));
        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style())), Size, Size);

        // Pitch 30 with a 55 degree field of view looks 2.5 to 57.5 degrees below the horizon: the ground fills the whole frame.
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Assert.Equal(Flat(Terrain), frame.Buffer.Get(x, y));
            }
        }
    }

    [Fact]
    public void GeometryBehindTheCamera_DrawsNothing()
    {
        var camera = new CameraState(new WorldPoint(100, 200, 0), 20, 0, 0, 55, CameraMode.Orbit);
        TileKey tile = Views.TileOf(camera);
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, tile, Geometry.Rect(LayerIds.Terrain, tile, -200, -100, 190, 210, 0)));

        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.Terrain, Views.Style())), Size, Size);

        Assert.All(frame.Buffer.Pixels.Chunk(4), p => Assert.Equal(Rgb.Background, new Rgb(p[0], p[1], p[2])));
    }

    [Fact]
    public void SameInput_GivesByteIdenticalOutput()
    {
        var camera = new CameraState(new WorldPoint(100, 200, 0), 60, 30, 50, 55, CameraMode.Orbit);
        TileKey tile = Views.TileOf(camera);
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, tile, Geometry.Rect(LayerIds.Terrain, tile, 70, 130, 170, 230, 0))
            .Add(LayerIds.VMapM2, tile, Geometry.Rect(LayerIds.VMapM2, tile, 90, 110, 190, 210, 4)));
        ViewState view = (Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Both, opacity: 0.8)), (LayerIds.VMapM2, Views.Style("#F2A541", RenderMode.Fill, 0.6))))
            with { ShowHud = true };

        RenderedImage first = rig.Renderer.Render(view, 160, 120, CancellationToken.None);
        RenderedImage second = rig.Renderer.Render(view, 160, 120, CancellationToken.None);

        Assert.Equal(first.Png, second.Png);
    }

    [Fact]
    public void MissingAndRejectedTiles_AreCountedAndListed_NotDrawnAsEmptySpace()
    {
        CameraState camera = Views.TopCamera;
        TileKey centre = Tile;
        var rejected = new TileKey(0, centre.X + 1, centre.Y);
        var source = new FakeSource()
            .Add(LayerIds.Terrain, centre, Geometry.Rect(LayerIds.Terrain, centre, 90, 110, 190, 210, 0))
            .Add(LayerIds.Terrain, rejected, LayerBuild.Rejected(LayerIds.Terrain, rejected, "bad magic 0x12345678"))
            .Add(LayerIds.TerrainHoles, centre, LayerBuild.Empty(LayerIds.TerrainHoles, centre, "no holes"));
        var rig = new RenderRig(source);
        ViewState view = Views.Of(camera, (LayerIds.Terrain, Views.Style()), (LayerIds.TerrainHoles, Views.Style("#E5484D", RenderMode.Wire))) with { LoadRadiusTiles = 1 };

        RenderedFrame frame = rig.Frame(view, Size, Size);

        JsonObject tiles = frame.Stats["tiles"]!.AsObject();
        Assert.Equal(18, tiles["requested"]!.GetValue<int>());
        Assert.Equal(1, tiles["ok"]!.GetValue<int>());
        Assert.Equal(1, tiles["empty"]!.GetValue<int>());
        Assert.Equal(1, tiles["rejected"]!.GetValue<int>());
        Assert.Equal(15, tiles["missing"]!.GetValue<int>());
        JsonArray problems = frame.Stats["problems"]!.AsArray();
        Assert.Equal(16, problems.Count);
        JsonObject reject = problems.Select(p => p!.AsObject()).Single(p => p["state"]!.GetValue<string>() == "Rejected");
        Assert.Equal("bad magic 0x12345678", reject["detail"]!.GetValue<string>());
        Assert.Equal(LayerIds.Terrain, reject["layer"]!.GetValue<string>());
    }

    [Fact]
    public void TheGlobalTile_IsDrawnWhenTheSourceListsIt()
    {
        CameraState camera = Views.TopCamera;
        TileKey global = TileKey.Global(0);
        var rig = new RenderRig(new FakeSource().Add(LayerIds.VMapWmo, global, Geometry.Rect(LayerIds.VMapWmo, global, 90, 110, 190, 210, 0)));

        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.VMapWmo, Views.Style("#E8E3D9"))), Size, Size);

        Assert.Equal(Flat(Pixels.Parse("#E8E3D9")), frame.Buffer.Get(32, 32));
        Assert.Equal(2, frame.Stats["tiles"]!["requested"]!.GetValue<int>());
        Assert.Equal(1, frame.Stats["tiles"]!["ok"]!.GetValue<int>());
        Assert.Equal(1, frame.Stats["tiles"]!["missing"]!.GetValue<int>());
    }

    [Fact]
    public void VisibleLayersWithoutASource_AreReported()
    {
        var rig = new RenderRig(new FakeSource().Serves(LayerIds.Terrain));
        ViewState view = Views.Of(
            Views.TopCamera,
            (LayerIds.Terrain, Views.Style()),
            (LayerIds.RefTerrain, Views.Style("#8FA9B8")),
            (LayerIds.LivePlayers, Views.Style("#FFFFFF")));

        RenderedFrame frame = rig.Frame(view with { Compare = CompareMode.Split }, Size, Size);

        Assert.Equal(new[] { LayerIds.RefTerrain }, frame.Stats["layersWithoutSource"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { LayerIds.LivePlayers }, frame.Stats["untiledSkipped"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Compare_ReferenceMode_LeavesServerLayersOut()
    {
        CameraState camera = Views.TopCamera;
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 80, 120, 180, 220, 0))
            .Add(LayerIds.RefTerrain, Tile, Geometry.Rect(LayerIds.RefTerrain, Tile, 90, 110, 190, 210, 0)));
        ViewState view = Views.Of(camera, (LayerIds.Terrain, Views.Style("#B9A77A")), (LayerIds.RefTerrain, Views.Style("#8FA9B8")));

        RenderedFrame server = rig.Frame(view, Size, Size);
        RenderedFrame reference = rig.Frame(view with { Compare = CompareMode.Reference }, Size, Size);

        Assert.Equal(new[] { LayerIds.RefTerrain }, server.Stats["hiddenByCompare"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(Flat(Terrain), server.Buffer.Get(32, 32));
        Assert.Equal(new[] { LayerIds.Terrain }, reference.Stats["hiddenByCompare"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(Flat(Pixels.Parse("#8FA9B8")), reference.Buffer.Get(32, 32));
        Assert.Equal(Rgb.Background, reference.Buffer.Get(8, 32));
    }

    [Fact]
    public void Compare_Split_ClipsServerLeftAndReferenceRight()
    {
        CameraState camera = Views.TopCamera;
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 40, 160, 120, 280, 0))
            .Add(LayerIds.RefTerrain, Tile, Geometry.Rect(LayerIds.RefTerrain, Tile, 40, 160, 120, 280, 0)));
        ViewState view = Views.Of(camera, (LayerIds.Terrain, Views.Style("#B9A77A")), (LayerIds.RefTerrain, Views.Style("#8FA9B8")))
            with { Compare = CompareMode.Split, SplitPosition = 0.5 };

        RenderedFrame frame = rig.Frame(view, Size, Size);

        Assert.Equal(Flat(Terrain), frame.Buffer.Get(10, 32));
        Assert.Equal(Flat(Terrain), frame.Buffer.Get(31, 32));
        Assert.Equal(Flat(Pixels.Parse("#8FA9B8")), frame.Buffer.Get(32, 32));
        Assert.Equal(Flat(Pixels.Parse("#8FA9B8")), frame.Buffer.Get(55, 32));
    }

    [Fact]
    public void Compare_Diff_DimsTheServerView()
    {
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 80, 120, 180, 220, 0))
            .Add(LayerIds.DiffTerrain, Tile, Geometry.Rect(LayerIds.DiffTerrain, Tile, 120, 140, 180, 220, 0)));
        ViewState view = Views.Of(Views.TopCamera, (LayerIds.Terrain, Views.Style("#B9A77A")), (LayerIds.DiffTerrain, Views.Style("#FF3EA5")))
            with { Compare = CompareMode.Diff };

        RenderedFrame frame = rig.Frame(view, Size, Size);

        Assert.Equal(Rgb.Background.Mixed(Flat(Terrain), Shading.DiffServerDim), frame.Buffer.Get(32, 32));
        (int x, int y) = Pixels.Of(Views.TopCamera, new WorldPoint(130, 200, 0), Size, Size);
        Assert.Equal(Flat(Pixels.Parse("#FF3EA5")), frame.Buffer.Get(x, y));
    }

    [Fact]
    public void Points_AreDrawnAsDiscs_AndRespectTheDepthTest()
    {
        CameraState camera = Views.TopCamera;
        var builder = new GeometryBuilder(LayerIds.VMapBounds, Tile, new WorldPoint(0, 0, 0));
        WorldPoint p = Pixels.TopDownWorld(camera, Size, Size, 20, 20, 0);
        WorldPoint hidden = Pixels.TopDownWorld(camera, Size, Size, 44, 44, -10);
        builder.AddPoint(builder.AddVertex(p));
        builder.AddPoint(builder.AddVertex(hidden));
        WorldPoint near = Pixels.TopDownWorld(camera, Size, Size, 38, 38, 0);
        WorldPoint far = Pixels.TopDownWorld(camera, Size, Size, 50, 50, 0);
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.VMapBounds, Tile, builder.Build())
            .Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, far.X, near.X, far.Y, near.Y, 0)));

        RenderedFrame frame = rig.Frame(Views.Of(camera, (LayerIds.VMapBounds, Views.Style("#FFFFFF", RenderMode.Wire)), (LayerIds.Terrain, Views.Style())), Size, Size);

        Assert.Equal(new Rgb(255, 255, 255), frame.Buffer.Get(20, 20));
        Assert.NotEqual(new Rgb(255, 255, 255), frame.Buffer.Get(44, 44));
    }

    [Fact]
    public void ValidateSize_RefusesTinyHugeAndOversizedImages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuRenderer.ValidateSize(15, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuRenderer.ValidateSize(100, 8193));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuRenderer.ValidateSize(8192, 8192));
        CpuRenderer.ValidateSize(3840, 2160);
    }

    [Fact]
    public void ASmallRender_OfAFewThousandTriangles_IsFast()
    {
        // A 40 x 40 height grid (3200 triangles, 4800 edges) at 640 x 360 with the HUD, hidden-line wire plus fill, from an orbit camera.
        var camera = new CameraState(new WorldPoint(100, 200, 0), 60, 30, 45, 55, CameraMode.Orbit);
        var builder = new GeometryBuilder(LayerIds.Terrain, Tile, new WorldPoint(0, 0, 0));
        const int cells = 40;
        var vertices = new uint[cells + 1, cells + 1];
        for (int i = 0; i <= cells; i++)
        {
            for (int j = 0; j <= cells; j++)
            {
                vertices[i, j] = builder.AddVertex(80 + i, 180 + j, 3 * Math.Sin(i / 5.0) * Math.Cos(j / 7.0));
            }
        }

        for (int i = 0; i < cells; i++)
        {
            for (int j = 0; j < cells; j++)
            {
                builder.AddTriangle(vertices[i, j], vertices[i + 1, j], vertices[i + 1, j + 1]);
                builder.AddTriangle(vertices[i, j], vertices[i + 1, j + 1], vertices[i, j + 1]);
            }
        }

        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, Tile, builder.Build()));
        ViewState view = Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Both))) with { ShowHud = true };
        rig.Frame(view, 64, 64);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        RenderedFrame frame = rig.Frame(view, 640, 360);
        clock.Stop();

        Assert.Equal(3200, Json.Int(frame.Stats["triangles"]));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"640x360 with 3200 triangles took {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(frame.Stats["elapsedMs"]!.GetValue<double>() < 2000);
    }

    [Fact]
    public void Untiled_AndInvisibleLayers_DoNothing()
    {
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, Tile, Geometry.Rect(LayerIds.Terrain, Tile, 90, 110, 190, 210, 0)));
        ViewState view = Views.Of(Views.TopCamera, (LayerIds.Terrain, Views.Style() with { Visible = false }));

        RenderedFrame frame = rig.Frame(view, Size, Size);

        Assert.Equal(Rgb.Background, frame.Buffer.Get(32, 32));
        Assert.Empty(frame.Stats["layers"]!.AsArray());
    }
}
