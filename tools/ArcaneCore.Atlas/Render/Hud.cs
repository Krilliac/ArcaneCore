using System.Globalization;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// The heads-up display painted over a render: a coordinate and tile block (top left), a compass (top right), a scale
/// bar (bottom left) and a statistics footer. Everything is drawn from the view and the scene's counts only, never from
/// the clock, so a render stays byte-for-byte reproducible. Sizes scale with the image height in whole font pixels.
/// </summary>
internal static class Hud
{
    /// <summary>Panel fill (design tokens: Graphite panel).</summary>
    public static readonly Rgb Panel = new(0x1C, 0x25, 0x2C);

    /// <summary>Primary text (Graphite ink).</summary>
    public static readonly Rgb Ink = new(0xE6, 0xEC, 0xEF);

    /// <summary>Secondary text and the compass ring (Graphite muted).</summary>
    public static readonly Rgb Muted = new(0x93, 0xA1, 0xAA);

    /// <summary>The compass's north needle and rejected-tile counts (signal red).</summary>
    public static readonly Rgb Red = new(0xE5, 0x48, 0x4D);

    /// <summary>Missing-tile counts and layers without a source (signal amber).</summary>
    public static readonly Rgb Amber = new(0xF5, 0xA5, 0x24);

    /// <summary>The split divider (accent cobalt).</summary>
    public static readonly Rgb Accent = new(0x1F, 0x5F, 0xFF);

    private const double PanelAlpha = 0.82;

    /// <summary>The integer font scale for an image of the given height.</summary>
    public static int ScaleFor(int height) => Math.Clamp(height / 360, 1, 4);

    /// <summary>Height in pixels of the statistics footer at <paramref name="scale"/> (two text lines plus padding).</summary>
    public static int FooterHeight(int scale) => (2 * 2 * scale) + (((2 * BitmapFont.LineAdvance) - 2) * scale);

    /// <summary>
    /// Paints the HUD. <paramref name="splitX"/> is the divider column for <see cref="CompareMode.Split"/>.
    /// The rasterizer's scissor must be the whole frame.
    /// </summary>
    public static void Draw(Framebuffer frame, Rasterizer raster, ViewState view, in ViewTransform transform, Scene scene, int splitX)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(scene);
        int scale = ScaleFor(frame.Height);
        DrawInfoBlock(frame, view, scale);
        DrawCompass(frame, raster, view.Camera, scale);
        DrawScaleBar(frame, view.Camera, transform, scale);
        DrawFooter(frame, view, scene, scale);
        if (view.Compare == CompareMode.Split)
        {
            DrawDivider(frame, splitX, scale);
        }
    }

    /// <summary>The two lines of the top-left block: the camera target's coordinates and the tile (with its .map file name) it lies in.</summary>
    public static (string Coordinates, string Tile) InfoLines(ViewState view)
    {
        ArgumentNullException.ThrowIfNull(view);
        WorldPoint t = view.Camera.Target;
        string coordinates = string.Create(CultureInfo.InvariantCulture, $"map {view.MapId}  x {t.X:F2}  y {t.Y:F2}  z {t.Z:F2}");
        string tile = WorldGrid.TileOf(view.MapId, t.X, t.Y) is { } key
            ? string.Create(CultureInfo.InvariantCulture, $"tile {key.X},{key.Y}  {WorldGrid.MapFileName(key)}")
            : "tile outside the 64x64 grid";
        return (coordinates, tile);
    }

    /// <summary>
    /// The largest 1, 2 or 5 times a power of ten yards whose bar fits in <paramref name="maxPixels"/>, or null when even
    /// the smallest such bar would be shorter than <paramref name="minPixels"/>.
    /// </summary>
    public static double? NiceLength(double pixelsPerYard, double maxPixels, double minPixels)
    {
        if (!double.IsFinite(pixelsPerYard) || pixelsPerYard <= 0 || maxPixels <= 0)
        {
            return null;
        }

        double best = 0;
        for (int exponent = -3; exponent <= 7; exponent++)
        {
            double decade = Math.Pow(10, exponent);
            foreach (double mantissa in (ReadOnlySpan<double>)[1, 2, 5])
            {
                double yards = mantissa * decade;
                if (yards * pixelsPerYard <= maxPixels)
                {
                    best = yards;
                }
            }
        }

        return best > 0 && best * pixelsPerYard >= minPixels ? best : null;
    }

    /// <summary>
    /// Where a world direction lands on the compass, as (right, up) in [-1, 1]: the camera's heading is the top. A
    /// direction is (x, y) in world axes, x north and y west.
    /// </summary>
    public static (double Right, double Up) CompassOffset(double yawDegrees, double worldX, double worldY)
    {
        double yaw = yawDegrees * Math.PI / 180;
        return ((worldX * Math.Sin(yaw)) - (worldY * Math.Cos(yaw)), (worldX * Math.Cos(yaw)) + (worldY * Math.Sin(yaw)));
    }

    private static void DrawInfoBlock(Framebuffer frame, ViewState view, int scale)
    {
        (string coordinates, string tile) = InfoLines(view);
        int pad = 3 * scale;
        int inner = 2 * scale;
        int width = Math.Max(BitmapFont.Measure(coordinates, scale), BitmapFont.Measure(tile, scale));
        int height = (BitmapFont.LineAdvance + BitmapFont.GlyphHeight) * scale;
        frame.FillRect(pad, pad, pad + width + (2 * inner), pad + height + (2 * inner), Panel, PanelAlpha);
        BitmapFont.Draw(frame, pad + inner, pad + inner, coordinates, scale, Ink);
        BitmapFont.Draw(frame, pad + inner, pad + inner + (BitmapFont.LineAdvance * scale), tile, scale, Muted);
    }

    private static void DrawCompass(Framebuffer frame, Rasterizer raster, CameraState camera, int scale)
    {
        double radius = 12.0 * scale;
        double labelRadius = radius + (6.0 * scale);
        double extent = radius + (10.0 * scale);
        double cx = frame.Width - (3.0 * scale) - extent;
        double cy = (3.0 * scale) + extent;
        Disc(frame, cx, cy, extent, Panel, PanelAlpha * 0.7);
        Ring(frame, cx, cy, radius, 0.75, Muted, 1.0);

        (string Letter, double X, double Y, Rgb Colour)[] cardinals =
        [
            ("N", 1, 0, Red),
            ("W", 0, 1, Muted),
            ("S", -1, 0, Muted),
            ("E", 0, -1, Muted),
        ];
        foreach ((string letter, double wx, double wy, Rgb colour) in cardinals)
        {
            (double right, double up) = CompassOffset(camera.Yaw, wx, wy);
            int lx = (int)Math.Round(cx + (right * labelRadius) - (BitmapFont.GlyphWidth * scale / 2.0));
            int ly = (int)Math.Round(cy - (up * labelRadius) - (BitmapFont.GlyphHeight * scale / 2.0));
            BitmapFont.Draw(frame, lx, ly, letter, scale, colour);
        }

        (double nr, double nu) = CompassOffset(camera.Yaw, 1, 0);
        double needle = radius * 0.85;
        var south = new StrokeStyle(Muted, 1.0, 0.75 * scale, false);
        var north = new StrokeStyle(Red, 1.0, 0.75 * scale, false);
        raster.StrokeScreen(cx, cy, 0, cx - (nr * needle), cy + (nu * needle), 0, south);
        raster.StrokeScreen(cx, cy, 0, cx + (nr * needle), cy - (nu * needle), 0, north);
    }

    private static void DrawScaleBar(Framebuffer frame, CameraState camera, in ViewTransform transform, int scale)
    {
        bool perspective = camera.Mode != CameraMode.Top;
        double reference = camera.Mode == CameraMode.Orbit ? camera.Distance : 50.0;
        double pixelsPerYard = perspective ? transform.FocalPixels / reference : transform.FocalPixels;
        double? yards = NiceLength(pixelsPerYard, Math.Min(0.3 * frame.Width, 160.0 * scale), 4.0 * scale);
        if (yards is not { } length)
        {
            return;
        }

        string label = perspective
            ? string.Create(CultureInfo.InvariantCulture, $"{length:0.###} yd at {reference:0} yd range")
            : string.Create(CultureInfo.InvariantCulture, $"{length:0.###} yd");
        int bar = (int)Math.Round(length * pixelsPerYard);
        int pad = 3 * scale;
        int inner = 2 * scale;
        int footerTop = frame.Height - FooterHeight(scale);
        int panelWidth = Math.Max(bar, BitmapFont.Measure(label, scale)) + (2 * inner);
        int panelHeight = ((BitmapFont.GlyphHeight + 2 + 6) * scale) + (2 * inner);
        int panelBottom = footerTop - pad;
        int panelTop = panelBottom - panelHeight;
        frame.FillRect(pad, panelTop, pad + panelWidth, panelBottom, Panel, PanelAlpha);
        BitmapFont.Draw(frame, pad + inner, panelTop + inner, label, scale, Ink);
        int barY = panelTop + inner + ((BitmapFont.GlyphHeight + 2) * scale);
        int x0 = pad + inner;
        frame.FillRect(x0, barY + (2 * scale), x0 + bar, barY + (4 * scale), Ink, 1.0);
        frame.FillRect(x0, barY, x0 + (2 * scale), barY + (6 * scale), Ink, 1.0);
        frame.FillRect(x0 + bar - (2 * scale), barY, x0 + bar, barY + (6 * scale), Ink, 1.0);
    }

    private static void DrawFooter(Framebuffer frame, ViewState view, Scene scene, int scale)
    {
        int height = FooterHeight(scale);
        int top = frame.Height - height;
        int inner = 2 * scale;
        frame.FillRect(0, top, frame.Width, frame.Height, Panel, PanelAlpha);

        TileTally tiles = scene.Tiles;
        var first = new List<(string Text, Rgb Colour)>
        {
            ($"tiles {tiles.Requested}: ", Ink),
            ($"{tiles.Count(TileState.Ok)} ok, ", Muted),
            ($"{tiles.Count(TileState.Empty)} empty, ", Muted),
            ($"{tiles.Count(TileState.Missing)} missing", tiles.Count(TileState.Missing) > 0 ? Amber : Muted),
            (", ", Muted),
            ($"{tiles.Count(TileState.Rejected)} rejected", tiles.Count(TileState.Rejected) > 0 ? Red : Muted),
        };
        if (tiles.Failed > 0)
        {
            first.Add(($", {tiles.Failed} failed", Red));
        }

        if (scene.LayersWithoutSource.Count > 0)
        {
            first.Add(($"  {scene.LayersWithoutSource.Count} layers without a source", Amber));
        }

        CameraState camera = view.Camera;
        string second = string.Create(
            CultureInfo.InvariantCulture,
            $"{scene.Triangles} tris  {scene.Lines} lines  {scene.Points} pts  {CompareName(view.Compare)}  {ViewState.Name(camera.Mode)} yaw {camera.Yaw:0} pitch {camera.Pitch:0}  {frame.Width}x{frame.Height}");
        DrawSegments(frame, inner, top + inner, scale, first);
        DrawSegments(frame, inner, top + inner + (BitmapFont.LineAdvance * scale), scale, [(second, Muted)]);
    }

    private static void DrawDivider(Framebuffer frame, int splitX, int scale)
    {
        frame.FillRect(splitX, 0, splitX + scale, frame.Height, Accent, 1.0);
        int y = 3 * scale;
        int server = BitmapFont.Measure("server", scale);
        int pad = 3 * scale;
        BitmapFont.Draw(frame, splitX - server - pad, y, "server", scale, Accent);
        BitmapFont.Draw(frame, splitX + scale + pad, y, "reference", scale, Accent);
    }

    private static void DrawSegments(Framebuffer frame, int x, int y, int scale, IEnumerable<(string Text, Rgb Colour)> segments)
    {
        int cursor = x;
        foreach ((string text, Rgb colour) in segments)
        {
            cursor = BitmapFont.Draw(frame, cursor, y, text, scale, colour);
        }
    }

    private static string CompareName(CompareMode mode) => ViewState.Name(mode);

    private static void Disc(Framebuffer frame, double cx, double cy, double radius, Rgb colour, double alpha)
    {
        ForEachCovered(frame, cx, cy, radius + 1, (x, y, distance) =>
        {
            double coverage = radius + 0.5 - distance;
            if (coverage > 0)
            {
                frame.Blend(x, y, colour, Math.Min(coverage, 1.0) * alpha);
            }
        });
    }

    private static void Ring(Framebuffer frame, double cx, double cy, double radius, double halfWidth, Rgb colour, double alpha)
    {
        ForEachCovered(frame, cx, cy, radius + halfWidth + 1, (x, y, distance) =>
        {
            double coverage = halfWidth + 0.5 - Math.Abs(distance - radius);
            if (coverage > 0)
            {
                frame.Blend(x, y, colour, Math.Min(coverage, 1.0) * alpha);
            }
        });
    }

    private static void ForEachCovered(Framebuffer frame, double cx, double cy, double reach, Action<int, int, double> visit)
    {
        int x0 = Math.Max(0, (int)Math.Floor(cx - reach));
        int x1 = Math.Min(frame.Width - 1, (int)Math.Ceiling(cx + reach));
        int y0 = Math.Max(0, (int)Math.Floor(cy - reach));
        int y1 = Math.Min(frame.Height - 1, (int)Math.Ceiling(cy + reach));
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - cx;
                double dy = y + 0.5 - cy;
                visit(x, y, Math.Sqrt((dx * dx) + (dy * dy)));
            }
        }
    }
}
