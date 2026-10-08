using ArcaneCore.Kernel.WorldData.Transports;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Elevators and trams (GAMEOBJECT_TYPE_TRANSPORT, 11; vmangos ElevatorTransport, Transports/Transport.cpp:383-430): the object is created as a
/// transport (<see cref="GameObject.CreateUpdateFlags"/>, HIGHGUID_TRANSPORT, level, state and flags) and its create block carries the
/// milliseconds into its TransportAnimation.dbc cycle, from which the client animates it; the progress is the time since the object was
/// created modulo the cycle length, as vmangos computes it every update. Without the DBC (or a row for the entry) the progress stays 0, as in
/// vmangos without animation info. The server keeps the object at its spawn (stationary) position: vmangos moves its copy along the nodes
/// for the passengers it carries, and nothing boards an elevator here (a player on one keeps the position its client reports).
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>The elevator and tram animations (TransportAnimation.dbc), shared by every map; empty without the DBC.</summary>
    public TransportAnimationCatalog ElevatorAnimations { get; set; } = TransportAnimationCatalog.Empty;

    private void UpdateElevators()
    {
        if (ElevatorAnimations.Count == 0)
        {
            return;
        }

        foreach (GameObject go in _objects.Values)
        {
            if (go.Type == GameObjectType.Transport && ElevatorAnimations.TotalTime(go.Entry) is var total and not 0)
            {
                go.PathProgress = (uint)((_clockMs - go.CreatedAtMs) % total);
            }
        }
    }

    /// <summary>
    /// Where the animation puts an elevator now (vmangos ElevatorTransport::Update, Transport.cpp:403-427): the node offset at its progress, turned
    /// by the object's rotation the way the reference does it (the row vector times the rotation matrix of GAMEOBJECT_ROTATION), its Y negated
    /// ("magical sign flip but it works - vanilla/tbc only"), plus the stationary position. Null without an animation for the entry.
    /// </summary>
    public (float X, float Y, float Z)? ElevatorPosition(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (go.Type != GameObjectType.Transport || ElevatorAnimations.Offset(go.Entry, go.PathProgress) is not { } offset)
        {
            return null;
        }

        float qx = go.GetFloat(UpdateFields.GameobjectRotation), qy = go.GetFloat(UpdateFields.GameobjectRotation + 1);
        float qz = go.GetFloat(UpdateFields.GameobjectRotation + 2), qw = go.GetFloat(UpdateFields.GameobjectRotation + 3);
        float length = MathF.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
        if (length > 0)
        {
            // Matrix3(Quat) unitizes a copy first.
            (qx, qy, qz, qw) = (qx / length, qy / length, qz / length, qw / length);
        }

        // G3D Matrix3(Quat) (dep/g3dlite Matrix3.cpp): the usual rotation matrix of a unit quaternion.
        float xx = 2 * qx * qx, yy = 2 * qy * qy, zz = 2 * qz * qz;
        float xy = 2 * qx * qy, xz = 2 * qx * qz, yz = 2 * qy * qz;
        float wx = 2 * qw * qx, wy = 2 * qw * qy, wz = 2 * qw * qz;
        float[,] m =
        {
            { 1 - (yy + zz), xy - wz, xz + wy },
            { xy + wz, 1 - (xx + zz), yz - wx },
            { xz - wy, yz + wx, 1 - (xx + yy) },
        };

        // Vector3 * Matrix3 (G3D: the row vector on the left).
        float rx = (offset.X * m[0, 0]) + (offset.Y * m[1, 0]) + (offset.Z * m[2, 0]);
        float ry = (offset.X * m[0, 1]) + (offset.Y * m[1, 1]) + (offset.Z * m[2, 1]);
        float rz = (offset.X * m[0, 2]) + (offset.Y * m[1, 2]) + (offset.Z * m[2, 2]);
        return (go.X + rx, go.Y - ry, go.Z + rz);
    }
}
