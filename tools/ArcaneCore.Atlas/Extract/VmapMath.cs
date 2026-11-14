using System.Numerics;

namespace ArcaneCore.Atlas.Extract;

/// <summary>
/// A single-precision 3x3 rotation, with the element order and the operation order of the math library the reference
/// extractor and assembler are built on, so that spawn positions, rotations and bounds computed here follow theirs as
/// closely as two different sine and cosine implementations allow. Column-vector convention: <c>Transform(v) = M * v</c>.
/// </summary>
internal readonly record struct VmapRotation(
    float M00, float M01, float M02,
    float M10, float M11, float M12,
    float M20, float M21, float M22)
{
    // ref: vmangos@0e3ff01 dep/g3dlite/include/G3D/g3dmath.h:148-150 (pi() is this double) and :807-809 (toRadians(float): deg * (float)pi() / 180.0f)
    private const double Pi = 3.1415926535898;

    /// <summary>Degrees to radians in single precision, the way the extractor converts a WMO's stored rotation.</summary>
    public static float ToRadians(float degrees) => degrees * (float)Pi / 180.0f;

    /// <summary>Radians to degrees in single precision.</summary>
    // ref: vmangos@0e3ff01 dep/g3dlite/include/G3D/g3dmath.h:811-813
    public static float ToDegrees(float radians) => radians * 180.0f / (float)Pi;

    /// <summary>Degrees to radians the way the assembler converts a spawn's rotation: in double precision, then narrowed.</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.h:49 (G3D::pi() * iDir.y / 180.f)
    public static float SpawnRadians(float degrees) => (float)(Pi * degrees / 180.0f);

    /// <summary><c>Rz(z) * (Ry(y) * Rx(x))</c>.</summary>
    // ref: vmangos@0e3ff01 dep/g3dlite/source/Matrix3.cpp:1664-1681 (fromEulerAnglesZYX)
    public static VmapRotation FromEulerAnglesZyx(float z, float y, float x)
    {
        float c = MathF.Cos(z);
        float s = MathF.Sin(z);
        var zMat = new VmapRotation(c, -s, 0, s, c, 0, 0, 0, 1);
        c = MathF.Cos(y);
        s = MathF.Sin(y);
        var yMat = new VmapRotation(c, 0, s, 0, 1, 0, -s, 0, c);
        c = MathF.Cos(x);
        s = MathF.Sin(x);
        var xMat = new VmapRotation(1, 0, 0, 0, c, -s, 0, s, c);
        return zMat * (yMat * xMat);
    }

    /// <summary>The rotation of a quaternion, which is normalised first.</summary>
    // ref: vmangos@0e3ff01 dep/g3dlite/source/Matrix3.cpp:122-142 and include/G3D/Quat.h:343-345 (unitize: multiply by the reciprocal square root of the dot product)
    public static VmapRotation FromQuaternion(Quaternion quaternion)
    {
        float scale = 1.0f / MathF.Sqrt((quaternion.X * quaternion.X) + (quaternion.Y * quaternion.Y) + (quaternion.Z * quaternion.Z) + (quaternion.W * quaternion.W));
        float qx = quaternion.X * scale;
        float qy = quaternion.Y * scale;
        float qz = quaternion.Z * scale;
        float qw = quaternion.W * scale;
        float xx = 2.0f * qx * qx;
        float xy = 2.0f * qx * qy;
        float xz = 2.0f * qx * qz;
        float xw = 2.0f * qx * qw;
        float yy = 2.0f * qy * qy;
        float yz = 2.0f * qy * qz;
        float yw = 2.0f * qy * qw;
        float zz = 2.0f * qz * qz;
        float zw = 2.0f * qz * qw;
        return new VmapRotation(
            1.0f - yy - zz, xy - zw, xz + yw,
            xy + zw, 1.0f - xx - zz, yz - xw,
            xz - yw, yz + xw, 1.0f - xx - yy);
    }

    // ref: vmangos@0e3ff01 dep/g3dlite/source/Matrix3.cpp:278-291 (row times column, the three products added left to right)
    public static VmapRotation operator *(VmapRotation a, VmapRotation b) => new(
        (a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20),
        (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21),
        (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22),
        (a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20),
        (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21),
        (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22),
        (a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20),
        (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21),
        (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22));

    /// <summary><c>M * v</c>.</summary>
    public Vector3 Transform(Vector3 v) => new(
        (M00 * v.X) + (M01 * v.Y) + (M02 * v.Z),
        (M10 * v.X) + (M11 * v.Y) + (M12 * v.Z),
        (M20 * v.X) + (M21 * v.Y) + (M22 * v.Z));

    /// <summary>
    /// The angles (x, y, z) with <c>M = Rx(x) * Ry(y) * Rz(z)</c>; at the two poles (where the decomposition is not unique)
    /// z is zero.
    /// </summary>
    // ref: vmangos@0e3ff01 dep/g3dlite/source/Matrix3.cpp:1383-1409 (toEulerAnglesXYZ; the inverse sine is clamped to [-1, 1])
    public (float X, float Y, float Z) ToEulerAnglesXyz()
    {
        const double halfPi = 1.57079633;
        if (M02 < 1.0f)
        {
            if (M02 > -1.0f)
            {
                return ((float)Math.Atan2(-M12, M22), (float)Math.Asin(M02), (float)Math.Atan2(-M01, M00));
            }

            return (-(float)Math.Atan2(M10, M11), -(float)halfPi, 0.0f);
        }

        return ((float)Math.Atan2(M10, M11), (float)halfPi, 0.0f);
    }
}
