using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// A point in camera space: <see cref="X"/> to the screen's right, <see cref="Y"/> up the screen, <see cref="D"/> the distance in front
/// of the camera along its forward axis (yards). <see cref="U"/> and <see cref="V"/> are the vertex's texture coordinate (0 for
/// untextured geometry); they are interpolated perspective-correct by the textured fill.
/// </summary>
internal readonly record struct ViewVertex(double X, double Y, double D, double U = 0, double V = 0);

/// <summary>
/// <see cref="CameraMath"/>'s camera applied to world points, with the same arithmetic as
/// <see cref="CameraMath.Project"/>: the eye is subtracted first, then the view matrix, then the projection matrix.
/// Callers subtract the eye from each chunk's double-precision origin before adding the chunk-relative float positions,
/// so no precision is spent on the distance from the world origin.
/// </summary>
internal readonly struct ViewTransform
{
    private readonly double[] _v;
    private readonly double[] _p;

    public ViewTransform(CameraState camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Eye = CameraMath.Eye(camera);
        _v = CameraMath.View(camera);
        _p = CameraMath.Projection(camera, (double)width / height);
        (Near, Far) = CameraMath.Planes(camera);
        Perspective = camera.Mode != CameraMode.Top;
        FocalPixels = height / 2.0 * _p[5];
    }

    public int Width { get; }

    public int Height { get; }

    public WorldPoint Eye { get; }

    /// <summary>Near clip distance, yards.</summary>
    public double Near { get; }

    /// <summary>Far clip distance, yards.</summary>
    public double Far { get; }

    /// <summary>True for perspective, false for the top-down orthographic camera.</summary>
    public bool Perspective { get; }

    /// <summary>
    /// Screen pixels per unit of camera-space Y at depth 1 (perspective) or per yard (top-down): the projection's
    /// vertical scale times half the image height. Used by the scale bar.
    /// </summary>
    public double FocalPixels { get; }

    /// <summary>The vertex for a point given relative to the eye (<c>world - Eye</c>).</summary>
    public ViewVertex ToView(double rx, double ry, double rz)
    {
        double[] m = _v;
        double x = (m[0] * rx) + (m[4] * ry) + (m[8] * rz) + (m[12] * 1);
        double y = (m[1] * rx) + (m[5] * ry) + (m[9] * rz) + (m[13] * 1);
        double z = (m[2] * rx) + (m[6] * ry) + (m[10] * rz) + (m[14] * 1);
        return new ViewVertex(x, y, -z);
    }

    /// <summary>Pixel coordinates (x right, y down, origin at the image's top-left corner) of a camera-space point in front of the camera.</summary>
    public (double X, double Y) ToScreen(in ViewVertex v)
    {
        double[] p = _p;
        double vz = -v.D;
        double cx = (p[0] * v.X) + (p[4] * v.Y) + (p[8] * vz) + (p[12] * 1);
        double cy = (p[1] * v.X) + (p[5] * v.Y) + (p[9] * vz) + (p[13] * 1);
        double cw = (p[3] * v.X) + (p[7] * v.Y) + (p[11] * vz) + (p[15] * 1);
        return (((cx / cw) + 1) * 0.5 * Width, (1 - (cy / cw)) * 0.5 * Height);
    }
}
