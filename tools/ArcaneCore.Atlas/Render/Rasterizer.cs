namespace ArcaneCore.Atlas.Render;

/// <summary>How <see cref="Rasterizer.FillTriangle"/> treats a triangle.</summary>
/// <param name="Occlude">Write the triangle's biased depth to the occluder plane so hidden-line edges behind it are rejected.</param>
/// <param name="Paint">Write colour. With <paramref name="Alpha"/> of 1 the fill is opaque (depth-tested against, and written to, the fill depth plane); below 1 it is blended and tested only.</param>
/// <param name="Colour">The already shaded fill colour.</param>
/// <param name="Alpha">Opacity in (0, 1].</param>
internal readonly record struct TriangleFill(bool Occlude, bool Paint, Rgb Colour, double Alpha);

/// <summary>How lines and points are drawn.</summary>
/// <param name="Colour">Line colour.</param>
/// <param name="Alpha">Opacity (the layer's opacity), multiplied by the anti-aliasing coverage per pixel.</param>
/// <param name="HalfWidth">Half the stroke width in pixels; pixels within <c>HalfWidth + 0.5</c> of the axis get partial coverage.</param>
/// <param name="DepthTest">True to reject pixels behind the occluder plane (hidden-line); false for x-ray and for the HUD.</param>
internal readonly record struct StrokeStyle(Rgb Colour, double Alpha, double HalfWidth, bool DepthTest);

/// <summary>
/// The CPU scanline rasterizer: filled triangles with a z-buffer, and anti-aliased thick lines and points tested against
/// an occluder depth plane. Single-threaded and free of randomness, so one input renders to one byte-identical output.
/// <para>
/// Coverage: pixel centres are at (i + 0.5, j + 0.5). A pixel belongs to a triangle when all three edge functions are
/// positive, or zero on a top or left edge; the edge function is evaluated in a canonical endpoint order so that the
/// two triangles sharing an edge compute exactly opposite values and a shared edge is never painted twice or skipped.
/// </para>
/// <para>
/// Hidden lines: every occluding triangle writes <c>depth + bias</c> to the occluder plane, with a constant part, a part
/// proportional to depth (the plane is stored as float) and a slope part (the triangle's own depth gradient in yards
/// per pixel, capped at a fraction of the depth), the CPU form of a GL polygon offset. A wire pixel lying in the plane of
/// the triangle that owns the edge, up to one pixel off its axis, therefore always passes; geometry farther behind than
/// the bias is hidden.
/// </para>
/// </summary>
internal sealed class Rasterizer
{
    /// <summary>Constant part of the hidden-line bias, yards.</summary>
    public const double BiasConstant = 0.02;

    /// <summary>Part of the bias proportional to view depth (float rounding of the depth plane).</summary>
    public const double BiasRelative = 4e-6;

    /// <summary>Multiple of the triangle's depth gradient (yards per pixel) added to the bias.</summary>
    public const double BiasSlope = 1.0;

    /// <summary>The slope part of the bias never exceeds this fraction of the depth.</summary>
    public const double BiasMaxSlopeRelative = 0.02;

    /// <summary>Half the stroke width of wireframe lines: coverage falls to one half at this distance, so lines look about 1.5 px wide.</summary>
    public const double LineHalfWidth = 0.75;

    /// <summary>Half the stroke width of points, pixels.</summary>
    public const double PointHalfWidth = 1.5;

    private readonly Framebuffer _frame;
    private readonly ViewTransform _view;
    private int _scissorMin;
    private int _scissorMax;

    public Rasterizer(Framebuffer frame, in ViewTransform view)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
        _view = view;
        _scissorMax = frame.Width;
    }

    /// <summary>Restricts drawing to pixel columns [<paramref name="minX"/>, <paramref name="maxXExclusive"/>) (split compare).</summary>
    public void SetScissor(int minX, int maxXExclusive)
    {
        _scissorMin = Math.Clamp(minX, 0, _frame.Width);
        _scissorMax = Math.Clamp(maxXExclusive, _scissorMin, _frame.Width);
    }

    /// <summary>
    /// Rasterizes a camera-space triangle, clipped to the near plane. With a <paramref name="shader"/> the colour (and opacity) of each
    /// pixel comes from it at the perspective-correct interpolated <see cref="ViewVertex.U"/>, <see cref="ViewVertex.V"/>; a pixel it
    /// discards writes neither colour nor depth. Without one the triangle is the flat fill of <paramref name="fill"/>.
    /// </summary>
    public void FillTriangle(in ViewVertex a, in ViewVertex b, in ViewVertex c, in TriangleFill fill, BatchShader? shader = null)
    {
        double near = _view.Near;
        double far = _view.Far;
        bool inA = a.D >= near;
        bool inB = b.D >= near;
        bool inC = c.D >= near;
        if (!inA && !inB && !inC)
        {
            return;
        }

        if (a.D > far && b.D > far && c.D > far)
        {
            return;
        }

        if (inA && inB && inC)
        {
            Raster(a, b, c, fill, shader);
            return;
        }

        // Sutherland-Hodgman against the plane D = near: a triangle becomes at most a quad.
        Span<ViewVertex> polygon = stackalloc ViewVertex[4];
        int count = 0;
        Span<ViewVertex> corners = [a, b, c];
        for (int i = 0; i < 3; i++)
        {
            ViewVertex current = corners[i];
            ViewVertex next = corners[(i + 1) % 3];
            bool currentIn = current.D >= near;
            bool nextIn = next.D >= near;
            if (currentIn)
            {
                polygon[count++] = current;
            }

            if (currentIn != nextIn)
            {
                polygon[count++] = ClipToNear(current, next, near);
            }
        }

        for (int i = 1; i + 1 < count; i++)
        {
            Raster(polygon[0], polygon[i], polygon[i + 1], fill, shader);
        }
    }

    /// <summary>Draws a camera-space segment as an anti-aliased stroke, clipped to the near plane.</summary>
    public void StrokeLine(in ViewVertex a, in ViewVertex b, in StrokeStyle style)
    {
        double near = _view.Near;
        if (a.D < near && b.D < near)
        {
            return;
        }

        if (a.D > _view.Far && b.D > _view.Far)
        {
            return;
        }

        ViewVertex pa = a.D < near ? ClipToNear(b, a, near) : a;
        ViewVertex pb = b.D < near ? ClipToNear(a, b, near) : b;
        (double x0, double y0) = _view.ToScreen(pa);
        (double x1, double y1) = _view.ToScreen(pb);
        StrokeScreen(x0, y0, DepthQuantity(pa.D), x1, y1, DepthQuantity(pb.D), style);
    }

    /// <summary>Draws a camera-space point as an anti-aliased disc.</summary>
    public void DrawPoint(in ViewVertex a, in StrokeStyle style)
    {
        if (a.D < _view.Near || a.D > _view.Far)
        {
            return;
        }

        (double x, double y) = _view.ToScreen(a);
        double q = DepthQuantity(a.D);
        StrokeScreen(x, y, q, x, y, q, style);
    }

    /// <summary>
    /// Draws a stroke between two pixel positions. <paramref name="q0"/> and <paramref name="q1"/> are the screen-space
    /// affine depth quantity at the ends (1/depth in perspective, depth for the top-down camera); ignored without a depth test.
    /// </summary>
    public void StrokeScreen(double x0, double y0, double q0, double x1, double y1, double q1, in StrokeStyle style)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double len2 = (dx * dx) + (dy * dy);
        if (!double.IsFinite(len2) || !double.IsFinite(x0) || !double.IsFinite(y0))
        {
            return;
        }

        double r = style.HalfWidth + 0.5;
        double len = Math.Sqrt(len2);
        double ux = len > 0 ? dx / len : 1;
        double uy = len > 0 ? dy / len : 0;
        double minX = Math.Min(x0, x1) - r;
        double maxX = Math.Max(x0, x1) + r;
        double minY = Math.Min(y0, y1) - r;
        double maxY = Math.Max(y0, y1) + r;
        double rowFirst = Math.Max(0, Math.Ceiling(minY - 0.5));
        double rowLast = Math.Min(_frame.Height - 1, Math.Floor(maxY - 0.5));
        if (rowFirst > rowLast)
        {
            return;
        }

        int width = _frame.Width;
        float[] occluder = _frame.OccluderDepth;
        double far = _view.Far;
        bool perspective = _view.Perspective;
        for (int j = (int)rowFirst; j <= (int)rowLast; j++)
        {
            double py = j + 0.5;
            double xl = minX;
            double xr = maxX;
            if (Math.Abs(uy) > 1e-6)
            {
                // Perpendicular distance |(px - x0) * uy - (py - y0) * ux| <= r solved for px: a strip around the axis.
                double axis = x0 + (((py - y0) * ux) / uy);
                double half = r / Math.Abs(uy);
                xl = Math.Max(xl, axis - half);
                xr = Math.Min(xr, axis + half);
            }

            double colFirst = Math.Max(_scissorMin, Math.Ceiling(xl - 0.5));
            double colLast = Math.Min(_scissorMax - 1, Math.Floor(xr - 0.5));
            for (int i = (int)Math.Min(colFirst, int.MaxValue); i <= colLast; i++)
            {
                double px = i + 0.5;
                double t = len2 > 0 ? (((px - x0) * dx) + ((py - y0) * dy)) / len2 : 0;
                t = Math.Clamp(t, 0, 1);
                double ex = px - (x0 + (t * dx));
                double ey = py - (y0 + (t * dy));
                double coverage = r - Math.Sqrt((ex * ex) + (ey * ey));
                if (coverage <= 0)
                {
                    continue;
                }

                int index = (j * width) + i;
                if (style.DepthTest)
                {
                    double q = q0 + (t * (q1 - q0));
                    double d = perspective ? 1.0 / q : q;
                    if (!(d > 0 && d <= far && d <= occluder[index]))
                    {
                        continue;
                    }
                }

                _frame.BlendAt(index * 4, style.Colour, Math.Min(coverage, 1.0) * style.Alpha);
            }
        }
    }

    /// <summary>The screen-space affine depth quantity: 1/depth for perspective, depth for the orthographic top-down camera.</summary>
    internal double DepthQuantity(double depth) => _view.Perspective ? 1.0 / depth : depth;

    private static ViewVertex ClipToNear(ViewVertex inside, ViewVertex outside, double near)
    {
        double t = (near - inside.D) / (outside.D - inside.D);
        return new ViewVertex(
            inside.X + (t * (outside.X - inside.X)),
            inside.Y + (t * (outside.Y - inside.Y)),
            near,
            inside.U + (t * (outside.U - inside.U)),
            inside.V + (t * (outside.V - inside.V)));
    }

    private void Raster(in ViewVertex va, in ViewVertex vb, in ViewVertex vc, in TriangleFill fill, BatchShader? shader)
    {
        (double ax, double ay) = _view.ToScreen(va);
        (double bx, double by) = _view.ToScreen(vb);
        (double cx, double cy) = _view.ToScreen(vc);
        double qa = DepthQuantity(va.D);
        double qb = DepthQuantity(vb.D);
        double qc = DepthQuantity(vc.D);

        double area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        if (!double.IsFinite(area) || area == 0)
        {
            return;
        }

        double ua = va.U;
        double uvA = va.V;
        double ub = vb.U;
        double uvB = vb.V;
        double uc = vc.U;
        double uvC = vc.V;
        if (area < 0)
        {
            (bx, cx) = (cx, bx);
            (by, cy) = (cy, by);
            (qb, qc) = (qc, qb);
            (ub, uc) = (uc, ub);
            (uvB, uvC) = (uvC, uvB);
            area = -area;
        }

        double rowFirst = Math.Max(0, Math.Ceiling(Math.Min(ay, Math.Min(by, cy)) - 0.5));
        double rowLast = Math.Min(_frame.Height - 1, Math.Floor(Math.Max(ay, Math.Max(by, cy)) - 0.5));
        double colFirst = Math.Max(_scissorMin, Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)) - 0.5));
        double colLast = Math.Min(_scissorMax - 1, Math.Floor(Math.Max(ax, Math.Max(bx, cx)) - 0.5));
        if (rowFirst > rowLast || colFirst > colLast)
        {
            return;
        }

        var edge0 = new EdgeEquation(ax, ay, bx, by);
        var edge1 = new EdgeEquation(bx, by, cx, cy);
        var edge2 = new EdgeEquation(cx, cy, ax, ay);
        double invArea = 1.0 / area;

        // Depth gradient of the screen-affine quantity q, for the slope part of the hidden-line bias.
        double dqdx = -((qa * (cy - by)) + (qb * (ay - cy)) + (qc * (by - ay))) * invArea;
        double dqdy = ((qa * (cx - bx)) + (qb * (ax - cx)) + (qc * (bx - ax))) * invArea;
        double gradient = Math.Sqrt((dqdx * dqdx) + (dqdy * dqdy));

        bool perspective = _view.Perspective;
        double far = _view.Far;
        int width = _frame.Width;
        float[] fillDepth = _frame.FillDepth;
        float[] occluderDepth = _frame.OccluderDepth;
        byte[] pixels = _frame.Pixels;
        bool opaque = fill.Alpha >= 1.0;
        double sa = perspective ? qa : 1.0;
        double sb = perspective ? qb : 1.0;
        double sc = perspective ? qc : 1.0;
        for (int j = (int)rowFirst; j <= (int)rowLast; j++)
        {
            double py = j + 0.5;
            for (int i = (int)colFirst; i <= (int)colLast; i++)
            {
                double px = i + 0.5;
                double w2 = edge0.Evaluate(px, py);
                if (w2 < 0 || (w2 == 0 && !edge0.Owns))
                {
                    continue;
                }

                double w0 = edge1.Evaluate(px, py);
                if (w0 < 0 || (w0 == 0 && !edge1.Owns))
                {
                    continue;
                }

                double w1 = edge2.Evaluate(px, py);
                if (w1 < 0 || (w1 == 0 && !edge2.Owns))
                {
                    continue;
                }

                double q = ((w0 * qa) + (w1 * qb) + (w2 * qc)) * invArea;
                double d = perspective ? 1.0 / q : q;
                if (!(d > 0 && d <= far))
                {
                    continue;
                }

                int index = (j * width) + i;
                Rgb colour = fill.Colour;
                double alpha = fill.Alpha;
                bool solid = opaque;
                if (shader is not null)
                {
                    if (fill.Paint && !fill.Occlude && (float)d >= fillDepth[index])
                    {
                        continue;
                    }

                    // Perspective-correct: interpolate u/depth and 1/depth in screen space (weights of 1 for the orthographic camera).
                    double wa = w0 * sa;
                    double wb = w1 * sb;
                    double wc = w2 * sc;
                    double inverse = 1.0 / (wa + wb + wc);
                    if (!shader.Shade(
                            ((wa * ua) + (wb * ub) + (wc * uc)) * inverse,
                            ((wa * uvA) + (wb * uvB) + (wc * uvC)) * inverse,
                            out colour,
                            out double shaded))
                    {
                        continue;
                    }

                    alpha = fill.Alpha * shaded;
                    solid = fill.Alpha >= 1.0 && shader.WritesDepth;
                }

                if (fill.Occlude)
                {
                    double slope = BiasSlope * (perspective ? d * d * gradient : gradient);
                    double cap = BiasMaxSlopeRelative * d;
                    float biased = (float)(d + BiasConstant + (BiasRelative * d) + (slope > cap ? cap : slope));
                    if (biased < occluderDepth[index])
                    {
                        occluderDepth[index] = biased;
                    }
                }

                if (fill.Paint)
                {
                    float depth = (float)d;
                    if (depth < fillDepth[index])
                    {
                        if (solid)
                        {
                            fillDepth[index] = depth;
                            int offset = index * 4;
                            pixels[offset] = colour.R;
                            pixels[offset + 1] = colour.G;
                            pixels[offset + 2] = colour.B;
                        }
                        else if (shader is null)
                        {
                            _frame.BlendAt(index * 4, colour, alpha);
                        }
                        else
                        {
                            _frame.Combine(index * 4, colour, alpha, shader.Blend);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// One triangle edge as a function of the sample point, positive inside a triangle wound with positive area. The
    /// endpoints are stored in lexicographic order with a sign so the same edge walked the other way yields the exact
    /// negation (bitwise), which makes the top-left ownership rule a partition.
    /// </summary>
    private readonly struct EdgeEquation
    {
        private readonly double _ax;
        private readonly double _ay;
        private readonly double _dx;
        private readonly double _dy;
        private readonly double _sign;

        public EdgeEquation(double ax, double ay, double bx, double by)
        {
            bool forward = ax < bx || (ax == bx && ay < by);
            _sign = forward ? 1.0 : -1.0;
            _ax = forward ? ax : bx;
            _ay = forward ? ay : by;
            _dx = forward ? bx - ax : ax - bx;
            _dy = forward ? by - ay : ay - by;

            // Top edge: horizontal with the interior below it (y grows downward). Left edge: runs up the screen.
            double walkedDx = bx - ax;
            double walkedDy = by - ay;
            Owns = walkedDy < 0 || (walkedDy == 0 && walkedDx > 0);
        }

        /// <summary>True when a sample exactly on this edge belongs to the triangle (top-left rule).</summary>
        public bool Owns { get; }

        public double Evaluate(double px, double py) => _sign * ((_dx * (py - _ay)) - (_dy * (px - _ax)));
    }
}
