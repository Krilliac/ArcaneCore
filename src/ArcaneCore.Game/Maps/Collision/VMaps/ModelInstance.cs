using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.VMaps;

/// <summary>
/// A placed model (vmangos <c>ModelInstance</c>): a <see cref="ModelSpawn"/> with its loaded
/// <see cref="WorldModel"/> and the transform between vmap internal space and model space.
/// <para>
/// The rotation is the spawn's Euler angles in degrees, applied as Z by <c>rotation.Y</c>, then
/// Y by <c>rotation.X</c>, then X by <c>rotation.Z</c> (G3D <c>fromEulerAnglesZYX</c> argument
/// order used by the format): internal = R · (model · scale) + position, and back
/// model = Rᵀ · (internal − position) / scale.
/// </para>
/// </summary>
public sealed class ModelInstance
{
    private readonly Matrix3 _rotation;
    private readonly Matrix3 _inverseRotation;
    private readonly float _inverseScale;

    public ModelInstance(ModelSpawn spawn, WorldModel model)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        ArgumentNullException.ThrowIfNull(model);
        Spawn = spawn;
        Model = model;
        const float toRadians = MathF.PI / 180.0f;
        _rotation = Matrix3.FromEulerAnglesZyx(spawn.Rotation.Y * toRadians, spawn.Rotation.X * toRadians, spawn.Rotation.Z * toRadians);
        _inverseRotation = _rotation.Transpose();
        _inverseScale = 1.0f / spawn.Scale;
    }

    public ModelSpawn Spawn { get; }

    public WorldModel Model { get; }

    /// <summary>Internal-space point → model space.</summary>
    public Vector3 ToModel(Vector3 point) => _inverseRotation.Transform(point - Spawn.Position) * _inverseScale;

    /// <summary>Model-space point → internal space.</summary>
    public Vector3 FromModel(Vector3 point) => _rotation.Transform(point * Spawn.Scale) + Spawn.Position;

    /// <summary>
    /// vmangos <c>ModelInstance::intersectRay</c>: the ray must meet the spawn's bound, then the
    /// model is tested in model space; <paramref name="distance"/> is internal-space length.
    /// </summary>
    public bool IntersectRay(Vector3 origin, Vector3 direction, ref float distance, bool stopAtFirstHit, bool ignoreM2)
    {
        if (ignoreM2 && Spawn.IsM2)
        {
            return false;
        }

        if (!BihTree.ClipToBox(origin, direction, Spawn.BoundLow, Spawn.BoundHigh, 0, distance, out _, out _))
        {
            return false;
        }

        Vector3 modelOrigin = ToModel(origin);
        Vector3 modelDirection = _inverseRotation.Transform(direction);
        float modelDistance = distance * _inverseScale;
        if (!Model.IntersectRay(modelOrigin, modelDirection, ref modelDistance, stopAtFirstHit))
        {
            return false;
        }

        distance = modelDistance * Spawn.Scale;
        return true;
    }

    /// <summary>
    /// vmangos <c>ModelInstance::intersectPoint</c>: the WMO group enclosing the point (doodads
    /// never count) and the internal-space height of its floor below the point.
    /// </summary>
    public bool TryGetGroupFloor(Vector3 point, out GroupModel? group, out float floorZ)
    {
        group = null;
        floorZ = float.NegativeInfinity;
        if (Spawn.IsM2 || !BihTree.Contains(Spawn.BoundLow, Spawn.BoundHigh, point))
        {
            return false;
        }

        Vector3 modelPoint = ToModel(point);
        Vector3 down = _inverseRotation.Transform(-Vector3.UnitZ);
        if (!Model.TryFindGroup(modelPoint, down, out group, out float distance))
        {
            return false;
        }

        floorZ = FromModel(modelPoint + (down * distance)).Z;
        return true;
    }
}

/// <summary>A 3×3 rotation (rows), column-vector convention: <c>Transform(v) = M · v</c>.</summary>
internal readonly record struct Matrix3(Vector3 Row0, Vector3 Row1, Vector3 Row2)
{
    /// <summary>Rz(z) · Ry(y) · Rx(x), right-handed rotations.</summary>
    public static Matrix3 FromEulerAnglesZyx(float z, float y, float x)
    {
        (float sz, float cz) = MathF.SinCos(z);
        (float sy, float cy) = MathF.SinCos(y);
        (float sx, float cx) = MathF.SinCos(x);
        var rz = new Matrix3(new Vector3(cz, -sz, 0), new Vector3(sz, cz, 0), new Vector3(0, 0, 1));
        var ry = new Matrix3(new Vector3(cy, 0, sy), new Vector3(0, 1, 0), new Vector3(-sy, 0, cy));
        var rx = new Matrix3(new Vector3(1, 0, 0), new Vector3(0, cx, -sx), new Vector3(0, sx, cx));
        return rz.Multiply(ry.Multiply(rx));
    }

    public Vector3 Transform(Vector3 v) => new(Vector3.Dot(Row0, v), Vector3.Dot(Row1, v), Vector3.Dot(Row2, v));

    public Matrix3 Transpose() => new(
        new Vector3(Row0.X, Row1.X, Row2.X),
        new Vector3(Row0.Y, Row1.Y, Row2.Y),
        new Vector3(Row0.Z, Row1.Z, Row2.Z));

    public Matrix3 Multiply(Matrix3 other)
    {
        Matrix3 t = other.Transpose();
        return new Matrix3(
            new Vector3(Vector3.Dot(Row0, t.Row0), Vector3.Dot(Row0, t.Row1), Vector3.Dot(Row0, t.Row2)),
            new Vector3(Vector3.Dot(Row1, t.Row0), Vector3.Dot(Row1, t.Row1), Vector3.Dot(Row1, t.Row2)),
            new Vector3(Vector3.Dot(Row2, t.Row0), Vector3.Dot(Row2, t.Row1), Vector3.Dot(Row2, t.Row2)));
    }
}
