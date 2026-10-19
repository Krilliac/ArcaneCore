using System.Numerics;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision.VMaps;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>A double-precision 3-vector (model, vmap-internal or WMO-local space; <see cref="WorldPoint"/> is for world space).</summary>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3d From(Vector3 v) => new(v.X, v.Y, v.Z);

    public static double Dot(Vec3d a, Vec3d b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vec3d Cross(Vec3d a, Vec3d b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    public double Length => Math.Sqrt(Dot(this, this));
}

/// <summary>A 3x3 rotation matrix in double precision, column-vector convention (<c>Transform(v) = M * v</c>).</summary>
public readonly record struct Mat3d(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    public static Mat3d Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>Right-handed rotation about X.</summary>
    public static Mat3d RotationX(double radians)
    {
        (double s, double c) = Math.SinCos(radians);
        return new Mat3d(1, 0, 0, 0, c, -s, 0, s, c);
    }

    /// <summary>Right-handed rotation about Y.</summary>
    public static Mat3d RotationY(double radians)
    {
        (double s, double c) = Math.SinCos(radians);
        return new Mat3d(c, 0, s, 0, 1, 0, -s, 0, c);
    }

    /// <summary>Right-handed rotation about Z.</summary>
    public static Mat3d RotationZ(double radians)
    {
        (double s, double c) = Math.SinCos(radians);
        return new Mat3d(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    /// <summary><c>Rz(z) * Ry(y) * Rx(x)</c>, the product G3D's <c>Matrix3::fromEulerAnglesZYX(z, y, x)</c> builds.</summary>
    public static Mat3d FromEulerAnglesZyx(double zRadians, double yRadians, double xRadians)
    {
        // ref: vmangos@0e3ff01/dep/g3dlite/source/Matrix3.cpp:1664-1681 (kZMat * (kYMat * kXMat))
        return RotationZ(zRadians) * (RotationY(yRadians) * RotationX(xRadians));
    }

    /// <summary>The rotation matrix of a quaternion (x, y, z, w), normalised first.</summary>
    public static Mat3d FromQuaternion(Quaternion q)
    {
        // ref: vmangos@0e3ff01/dep/g3dlite/source/Matrix3.cpp:122-142 (Matrix3(Quat): unitize, Watt and Watt pg 362)
        double length = Math.Sqrt(((double)q.X * q.X) + ((double)q.Y * q.Y) + ((double)q.Z * q.Z) + ((double)q.W * q.W));
        if (!(length > 1e-12))
        {
            return Identity;
        }

        double x = q.X / length;
        double y = q.Y / length;
        double z = q.Z / length;
        double w = q.W / length;
        double xx = 2 * x * x;
        double xy = 2 * x * y;
        double xz = 2 * x * z;
        double xw = 2 * x * w;
        double yy = 2 * y * y;
        double yz = 2 * y * z;
        double yw = 2 * y * w;
        double zz = 2 * z * z;
        double zw = 2 * z * w;
        return new Mat3d(
            1 - yy - zz, xy - zw, xz + yw,
            xy + zw, 1 - xx - zz, yz - xw,
            xz - yw, yz + xw, 1 - xx - yy);
    }

    public static Mat3d operator *(Mat3d a, Mat3d b) => new(
        (a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20), (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21), (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22),
        (a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20), (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21), (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22),
        (a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20), (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21), (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22));

    public Vec3d Transform(Vec3d v) => new(
        (M00 * v.X) + (M01 * v.Y) + (M02 * v.Z),
        (M10 * v.X) + (M11 * v.Y) + (M12 * v.Z),
        (M20 * v.X) + (M21 * v.Y) + (M22 * v.Z));

    public Mat3d Transpose() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

    /// <summary>The angle in degrees of the rotation that takes this matrix to <paramref name="other"/> (0 when equal).</summary>
    public double AngleToDegrees(Mat3d other)
    {
        Mat3d relative = Transpose() * other;
        double cosine = (relative.M00 + relative.M11 + relative.M22 - 1) / 2;
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI;
    }
}

/// <summary>
/// Where a model instance sits: <c>internal = Rotation * (model * Scale) + Position</c> in vmap internal space (the
/// server's space for its model instances). <see cref="ModelToWorld"/> then applies the half turn back to world space.
/// </summary>
public readonly record struct InstanceTransform(Mat3d Rotation, Vec3d Position, double Scale)
{
    public Vec3d ModelToInternal(Vec3d model) => Rotation.Transform(model * Scale) + Position;

    public WorldPoint ModelToWorld(Vec3d model) => PlacementTransform.InternalToWorld(ModelToInternal(model));
}

/// <summary>
/// The client-placement to world transforms, with the vmap extractor's rules for which instances it keeps. Every
/// formula is the one the server's extracted data embodies: the world-space oracle in the tests proves it against
/// every spawn of two real vmap tiles.
/// </summary>
public static class PlacementTransform
{
    /// <summary>Half the map's extent in yards (the server's <see cref="VMapFormat.MapMid"/>).</summary>
    public static double MapMid => VMapFormat.MapMid;

    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:586-590 (a WMO at raw x == 0 and raw z == 0 is moved to 533.33333f * 32 in both)
    private const float OriginQuirkCoordinate = 533.33333f * 32;

    /// <summary>
    /// Raw ADT position (the three floats in file order: x, y, z) to vmap internal coordinates: <c>(z, x, y)</c>.
    /// </summary>
    public static Vec3d RawToInternal(WorldPoint raw)
    {
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.h:69 (fixCoords(v) = Vec3D(v.z, v.x, v.y))
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:157 (ModelInstance: pos = fixCoords(file position))
        // ref: WoW-RE-Research/formats/wmo.md:231-237 (worldX = ZEROPOINT - storedZ, worldY = ZEROPOINT - storedX, worldZ = storedY)
        return new Vec3d(raw.Z, raw.X, raw.Y);
    }

    /// <summary>vmap internal coordinates to world: <c>(MapMid - x, MapMid - y, z)</c>.</summary>
    public static WorldPoint InternalToWorld(Vec3d internalPosition)
    {
        // ref: src/ArcaneCore.Game/Maps/Collision/VMaps/VMapFormat.cs:25-31 (ToInternal / ToWorld, vmangos convertPositionToInternalRep)
        return new WorldPoint(MapMid - internalPosition.X, MapMid - internalPosition.Y, internalPosition.Z);
    }

    /// <summary>World to vmap internal coordinates (the same half turn).</summary>
    public static Vec3d WorldToInternal(WorldPoint world)
        => new(MapMid - world.X, MapMid - world.Y, world.Z);

    /// <summary>
    /// The multiplier for an M2 placement scale. A MDDF stores a u16 where 1024 means 1.0 (so any <c>u16 / 1024.0</c>
    /// multiplier is strictly below 64, while no 1.12.1 doodad has raw <c>u16 &lt; 102</c>); a value already turned into a
    /// multiplier (<c>&lt; 64</c>) is kept as is.
    /// </summary>
    public static double M2Scale(double placementScale)
    {
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:166 (sc = scale / 1024.0f; u16 max 65535 / 1024.0 < 64.0)
        if (!double.IsFinite(placementScale) || placementScale <= 0)
        {
            return 1;
        }

        return placementScale >= 64 ? placementScale / 1024.0 : placementScale;
    }

    /// <summary>
    /// The transform of an MDDF placement:
    /// <c>Rz(rot.Y) * Ry(rot.X) * Rx(rot.Z)</c> about the internal position, with the u16 scale over 1024.
    /// </summary>
    public static InstanceTransform ForM2(ClientPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        // ref: src/ArcaneCore.Game/Maps/Collision/VMaps/ModelInstance.cs:9-14 and :36-40 (internal = R * (model * scale) + position, R from rotation.Y, .X, .Z)
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:152-166 (position fixCoords, rotation written as read)
        return new InstanceTransform(RotationFromEuler(placement.Rotation), RawToInternal(placement.Position), M2Scale(placement.Scale));
    }

    /// <summary>
    /// The transform of an MODF placement (scale 1; MODF has no scale in 1.12.1). <paramref name="originQuirk"/> is true
    /// when the extractor's (0, 0) rule moved the WMO to world (0, 0).
    /// </summary>
    public static InstanceTransform ForWmo(ClientPlacement placement, out bool originQuirk)
    {
        ArgumentNullException.ThrowIfNull(placement);
        WorldPoint raw = placement.Position;
        originQuirk = raw.X == 0 && raw.Z == 0;
        if (originQuirk)
        {
            raw = new WorldPoint(OriginQuirkCoordinate, raw.Y, OriginQuirkCoordinate);
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:586-595 (quirk, then fixCoords; the WMO spawn has scale 1.0)
        return new InstanceTransform(RotationFromEuler(placement.Rotation), RawToInternal(raw), 1.0);
    }

    /// <summary>
    /// The transform of a WMO doodad, composed geometrically: the doodad's local quaternion first, then the WMO's
    /// rotation (<c>Rwmo * Rdoodad</c>), at the WMO position plus the rotated local position. This is what the client
    /// shows; the vmap extractor stores a different rotation for tilted doodads (see
    /// <see cref="ExtractorDoodadRotation"/>).
    /// </summary>
    public static InstanceTransform ForWmoDoodad(InstanceTransform wmo, WmoDoodad doodad)
    {
        ArgumentNullException.ThrowIfNull(doodad);

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:271 (position = wmoPosition + wmoRotation * doodad.Position)
        // ref: WoW-RE-Research/formats/wmo.md:129-139 (MODD position in WMO-local space, quaternion orientation, uniform scale)
        Vec3d position = wmo.Position + wmo.Rotation.Transform(Vec3d.From(doodad.Position));
        Mat3d rotation = wmo.Rotation * Mat3d.FromQuaternion(doodad.Rotation);
        double scale = float.IsFinite(doodad.Scale) && doodad.Scale > 0 ? doodad.Scale : 1.0;
        return new InstanceTransform(rotation, position, scale);
    }

    /// <summary>
    /// The Euler triple (degrees: x, y, z) the vmap extractor writes for a WMO doodad: the XYZ decomposition of
    /// <c>Rdoodad * Rwmo</c>, computed the way G3D does in single precision. The server later reads any stored triple
    /// as <c>Rz(y) * Ry(x) * Rx(z)</c>, which is a different composition, so tilted doodads end up rotated differently
    /// from <see cref="ForWmoDoodad"/>.
    /// </summary>
    public static (float X, float Y, float Z) ExtractorDoodadRotation(Quaternion doodadRotation, WorldPoint wmoRawRotation)
    {
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:229 (wmoRotation = fromEulerAnglesZYX(toRadians(rot.y), toRadians(rot.x), toRadians(rot.z)))
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:273-280 (Quat.toRotationMatrix() * wmoRotation, toEulerAnglesXYZ(rotation.z, rotation.x, rotation.y), toDegrees)
        // ref: vmangos@0e3ff01/dep/g3dlite/source/Matrix3.cpp:1383-1409 (toEulerAnglesXYZ with its two gimbal-lock branches)
        float[] wmo = SingleEulerZyx(
            ToRadians((float)wmoRawRotation.Y),
            ToRadians((float)wmoRawRotation.X),
            ToRadians((float)wmoRawRotation.Z));
        float[] doodad = SingleFromQuaternion(doodadRotation);
        float[] m = SingleMultiply(doodad, wmo);

        float ax;
        float ay;
        float az;
        if (m[2] < 1.0f)
        {
            if (m[2] > -1.0f)
            {
                ax = (float)Math.Atan2(-m[5], m[8]);
                ay = (float)SingleAsin(m[2]);
                az = (float)Math.Atan2(-m[1], m[0]);
            }
            else
            {
                ax = -(float)Math.Atan2(m[3], m[4]);
                ay = -(float)HalfPi;
                az = 0.0f;
            }
        }
        else
        {
            ax = (float)Math.Atan2(m[3], m[4]);
            ay = (float)HalfPi;
            az = 0.0f;
        }

        // toEulerAnglesXYZ(rotation.z, rotation.x, rotation.y): x angle -> rotation.z, y angle -> rotation.x, z angle -> rotation.y
        return (ToDegrees(ay), ToDegrees(az), ToDegrees(ax));
    }

    /// <summary>How the server turns a stored rotation triple (degrees as read from a spawn) into a rotation.</summary>
    public static Mat3d ServerRotation(double rotationXDegrees, double rotationYDegrees, double rotationZDegrees)
    {
        // ref: src/ArcaneCore.Game/Maps/Collision/VMaps/ModelInstance.cs:26-30 (FromEulerAnglesZyx(rotation.Y, rotation.X, rotation.Z) in radians)
        return Mat3d.FromEulerAnglesZyx(Radians(rotationYDegrees), Radians(rotationXDegrees), Radians(rotationZDegrees));
    }

    /// <summary>The rotation of a raw (x, y, z) degrees triple from an MDDF or MODF record.</summary>
    public static Mat3d RotationFromEuler(WorldPoint rawRotationDegrees)
        => ServerRotation(rawRotationDegrees.X, rawRotationDegrees.Y, rawRotationDegrees.Z);

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private const double HalfPi = Math.PI / 2;

    // G3D::toRadians(float) / toDegrees(float): float arithmetic with pi cast to float (g3dmath.h:807-813).
    // ref: vmangos@0e3ff01/dep/g3dlite/include/G3D/g3dmath.h:807-813
    private static float ToRadians(float degrees) => degrees * (float)3.1415926535898 / 180.0f;

    private static float ToDegrees(float radians) => radians * 180.0f / (float)3.1415926535898;

    // G3D::aSin clamps outside (-1, 1) to +-halfPi.
    private static double SingleAsin(float value)
    {
        if (value > -1.0f)
        {
            return value < 1.0f ? Math.Asin(value) : HalfPi;
        }

        return -HalfPi;
    }

    private static float[] SingleEulerZyx(float yaw, float pitch, float roll)
    {
        float cz = MathF.Cos(yaw);
        float sz = MathF.Sin(yaw);
        float cy = MathF.Cos(pitch);
        float sy = MathF.Sin(pitch);
        float cx = MathF.Cos(roll);
        float sx = MathF.Sin(roll);
        float[] z = [cz, -sz, 0, sz, cz, 0, 0, 0, 1];
        float[] y = [cy, 0, sy, 0, 1, 0, -sy, 0, cy];
        float[] x = [1, 0, 0, 0, cx, -sx, 0, sx, cx];
        return SingleMultiply(z, SingleMultiply(y, x));
    }

    private static float[] SingleFromQuaternion(Quaternion q)
    {
        float dot = (q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W);
        if (!(dot > 1e-24f))
        {
            return [1, 0, 0, 0, 1, 0, 0, 0, 1];
        }

        float scale = 1.0f / MathF.Sqrt(dot);
        float x = q.X * scale;
        float y = q.Y * scale;
        float z = q.Z * scale;
        float w = q.W * scale;
        float xx = 2.0f * x * x;
        float xy = 2.0f * x * y;
        float xz = 2.0f * x * z;
        float xw = 2.0f * x * w;
        float yy = 2.0f * y * y;
        float yz = 2.0f * y * z;
        float yw = 2.0f * y * w;
        float zz = 2.0f * z * z;
        float zw = 2.0f * z * w;
        return [1.0f - yy - zz, xy - zw, xz + yw, xy + zw, 1.0f - xx - zz, yz - xw, xz - yw, yz + xw, 1.0f - xx - yy];
    }

    private static float[] SingleMultiply(float[] a, float[] b)
    {
        var result = new float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                result[(row * 3) + col] = (a[row * 3] * b[col]) + (a[(row * 3) + 1] * b[3 + col]) + (a[(row * 3) + 2] * b[6 + col]);
            }
        }

        return result;
    }
}
