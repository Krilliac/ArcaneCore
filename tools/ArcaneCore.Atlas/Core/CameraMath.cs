namespace ArcaneCore.Atlas.Core;

/// <summary>
/// The one camera model shared by the UI (Web/js mirrors these formulas) and the headless renderer.
/// <para>
/// World space is WoW's: x north, y west, z up (right-handed, so a standard right-handed look-at needs no mirror).
/// <see cref="CameraState.Yaw"/> turns from +X toward +Y; <see cref="CameraState.Pitch"/> is degrees below the
/// horizon, so the look direction is (cos p cos y, cos p sin y, -sin p).
/// </para>
/// <para>
/// Orbit: the eye sits <see cref="CameraState.Distance"/> behind <see cref="CameraState.Target"/> along the look
/// direction. Fly: the eye IS the target point and looks along the direction. Top: orthographic, looking straight
/// down at the target, the screen's up axis along the yaw heading, <see cref="CameraState.Distance"/> yards from the
/// centre to the top edge.
/// </para>
/// <para>
/// Matrices are 4x4 column-major (WebGL's <c>uniformMatrix4fv</c> layout, element [col*4 + row]). The view matrix
/// is camera-relative: it has no translation, and every renderer subtracts the eye (in double) from each chunk
/// origin before converting to float, so float precision is spent near the camera.
/// </para>
/// </summary>
public static class CameraMath
{
    /// <summary>Height above the target of the top-down eye, yards.</summary>
    public const double TopEyeHeight = 5000;

    public static WorldPoint Direction(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (camera.Mode == CameraMode.Top)
        {
            return new WorldPoint(0, 0, -1);
        }

        double yaw = camera.Yaw * Math.PI / 180;
        double pitch = camera.Pitch * Math.PI / 180;
        return new WorldPoint(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), -Math.Sin(pitch));
    }

    public static WorldPoint Eye(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.Mode switch
        {
            CameraMode.Fly => camera.Target,
            CameraMode.Top => camera.Target with { Z = camera.Target.Z + TopEyeHeight },
            _ => camera.Target - Scale(Direction(camera), camera.Distance),
        };
    }

    /// <summary>Screen up in world space (world +Z, except top-down, where it is the yaw heading).</summary>
    public static WorldPoint Up(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (camera.Mode == CameraMode.Top)
        {
            double yaw = camera.Yaw * Math.PI / 180;
            return new WorldPoint(Math.Cos(yaw), Math.Sin(yaw), 0);
        }

        return new WorldPoint(0, 0, 1);
    }

    /// <summary>Near and far clip distances, yards.</summary>
    public static (double Near, double Far) Planes(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.Mode switch
        {
            CameraMode.Top => (1, TopEyeHeight + 2000),
            CameraMode.Fly => (0.1, 6000),
            _ => (Math.Clamp(camera.Distance / 500, 0.05, 5), Math.Max(6000, camera.Distance * 4)),
        };
    }

    /// <summary>Camera-relative view matrix (rotation only; the eye is the origin).</summary>
    public static double[] View(CameraState camera)
    {
        (WorldPoint s, WorldPoint u, WorldPoint f) = Basis(camera);
        return
        [
            s.X, u.X, -f.X, 0,
            s.Y, u.Y, -f.Y, 0,
            s.Z, u.Z, -f.Z, 0,
            0, 0, 0, 1,
        ];
    }

    /// <summary>Projection matrix (OpenGL clip space, z in [-1, 1]) for a viewport of the given width/height ratio.</summary>
    public static double[] Projection(CameraState camera, double aspect)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!double.IsFinite(aspect) || aspect <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aspect), aspect, "Aspect ratio must be positive.");
        }

        (double near, double far) = Planes(camera);
        if (camera.Mode == CameraMode.Top)
        {
            double h = camera.Distance;
            double w = h * aspect;
            return
            [
                1 / w, 0, 0, 0,
                0, 1 / h, 0, 0,
                0, 0, -2 / (far - near), 0,
                0, 0, -(far + near) / (far - near), 1,
            ];
        }

        double focal = 1 / Math.Tan(camera.Fov * Math.PI / 360);
        return
        [
            focal / aspect, 0, 0, 0,
            0, focal, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0,
        ];
    }

    /// <summary>
    /// The world-space ray under a viewport point given in normalized device coordinates (x right, y up, each in
    /// [-1, 1]). The ray starts on the near plane (perspective) or the eye plane (top-down).
    /// </summary>
    public static PickRay RayThrough(CameraState camera, uint mapId, double ndcX, double ndcY, double aspect, double maxDistance = 2000)
    {
        (WorldPoint s, WorldPoint u, WorldPoint f) = Basis(camera);
        WorldPoint eye = Eye(camera);
        if (camera.Mode == CameraMode.Top)
        {
            double h = camera.Distance;
            WorldPoint origin = eye + Scale(s, ndcX * h * aspect) + Scale(u, ndcY * h);
            return new PickRay(mapId, origin, f, TopEyeHeight + 2000);
        }

        double tan = Math.Tan(camera.Fov * Math.PI / 360);
        WorldPoint direction = Normalize(f + Scale(s, ndcX * tan * aspect) + Scale(u, ndcY * tan));
        return new PickRay(mapId, eye, direction, maxDistance);
    }

    /// <summary>Projects a world point to normalized device coordinates; null when it is behind the camera.</summary>
    public static (double X, double Y, double Depth)? Project(CameraState camera, WorldPoint world, double aspect)
    {
        double[] view = View(camera);
        double[] projection = Projection(camera, aspect);
        WorldPoint rel = world - Eye(camera);
        double[] v = Multiply(view, [rel.X, rel.Y, rel.Z, 1]);
        double[] clip = Multiply(projection, v);
        if (clip[3] <= 0)
        {
            return null;
        }

        return (clip[0] / clip[3], clip[1] / clip[3], clip[2] / clip[3]);
    }

    private static (WorldPoint Side, WorldPoint Up, WorldPoint Forward) Basis(CameraState camera)
    {
        WorldPoint f = Normalize(Direction(camera));
        WorldPoint up = Up(camera);
        WorldPoint s = Cross(f, up);
        if (Length(s) < 1e-9)
        {
            // Looking straight up or down with world up: fall back to the yaw heading.
            double yaw = camera.Yaw * Math.PI / 180;
            s = Cross(f, new WorldPoint(Math.Cos(yaw), Math.Sin(yaw), 0));
        }

        s = Normalize(s);
        return (s, Cross(s, f), f);
    }

    private static double[] Multiply(double[] m, double[] v)
    {
        var r = new double[4];
        for (int row = 0; row < 4; row++)
        {
            r[row] = (m[row] * v[0]) + (m[4 + row] * v[1]) + (m[8 + row] * v[2]) + (m[12 + row] * v[3]);
        }

        return r;
    }

    private static WorldPoint Scale(WorldPoint p, double k) => new(p.X * k, p.Y * k, p.Z * k);

    private static WorldPoint Cross(WorldPoint a, WorldPoint b)
        => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    private static double Length(WorldPoint p) => Math.Sqrt((p.X * p.X) + (p.Y * p.Y) + (p.Z * p.Z));

    private static WorldPoint Normalize(WorldPoint p)
    {
        double length = Length(p);
        return length > 0 ? Scale(p, 1 / length) : p;
    }
}
