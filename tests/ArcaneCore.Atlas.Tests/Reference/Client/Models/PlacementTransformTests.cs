using System.Numerics;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>A small deterministic generator (SplitMix64) so tests do not depend on a platform pseudo-random generator.</summary>
internal sealed class SplitMix(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A double in [min, max).</summary>
    public double Range(double min, double max) => min + (((Next() >> 11) / (double)(1UL << 53)) * (max - min));
}

public sealed class PlacementTransformTests
{
    private static ClientPlacement M2Placement(WorldPoint position, WorldPoint rotation, double scale)
        => new(PlacementKind.M2, "x.m2", 1, position, rotation, scale, 0, 0, new TileKey(0, 49, 31));

    private static ClientPlacement WmoPlacement(WorldPoint position, WorldPoint rotation, ushort flags = 0, ushort set = 0)
        => new(PlacementKind.Wmo, "x.wmo", 1, position, rotation, 1, flags, set, new TileKey(0, 49, 31));

    [Fact]
    public void RawToInternalToWorld_FollowsTheDocumentedAxes()
    {
        // The Goldshire Inn's MODF position as stored in Azeroth_31_49.adt (x, y, z in file order).
        var raw = new WorldPoint(17042.267578125, 56.526336669921875, 26530.9140625);

        Vec3d internalPosition = PlacementTransform.RawToInternal(raw);
        WorldPoint world = PlacementTransform.InternalToWorld(internalPosition);

        Assert.Equal(new Vec3d(26530.9140625, 17042.267578125, 56.526336669921875), internalPosition);
        Assert.Equal(VMapFormat.MapMid - 26530.9140625, world.X, 6);
        Assert.Equal(VMapFormat.MapMid - 17042.267578125, world.Y, 6);
        Assert.Equal(56.526336669921875, world.Z, 6);
        Assert.Equal<TileKey?>(new TileKey(0, 49, 31), WorldGrid.TileOf(0, world.X, world.Y));
        Vec3d roundTrip = PlacementTransform.WorldToInternal(world);
        Assert.Equal(26530.9140625, roundTrip.X, 6);
        Assert.Equal(17042.267578125, roundTrip.Y, 6);
        Assert.Equal(56.526336669921875, roundTrip.Z, 6);
    }

    [Theory]
    [InlineData(1024, 1.0)]
    [InlineData(2048, 2.0)]
    [InlineData(512, 0.5)]
    [InlineData(24186, 23.619140625)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(2.5, 2.5)]
    [InlineData(17.623046875, 17.623046875)]
    [InlineData(23.619140625, 23.619140625)]
    [InlineData(0.0, 1.0)]
    [InlineData(-3.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    public void M2Scale_AcceptsTheRawU16OrAMultiplier(double stored, double expected)
    {
        Assert.Equal(expected, PlacementTransform.M2Scale(stored), 9);
    }

    [Fact]
    public void ForM2_MatchesTheServersModelInstance()
    {
        var model = new WorldModel(1, [], null);
        var random = new SplitMix(20261018);
        for (int i = 0; i < 300; i++)
        {
            var position = new WorldPoint(random.Range(0, 34000), random.Range(-500, 1500), random.Range(0, 34000));
            var rotation = new WorldPoint(random.Range(-180, 180), random.Range(-180, 180), random.Range(-180, 180));
            double rawScale = random.Range(256, 8192);
            Vec3d local = new(random.Range(-40, 40), random.Range(-40, 40), random.Range(-40, 40));

            InstanceTransform mine = PlacementTransform.ForM2(M2Placement(position, rotation, rawScale));
            var spawn = new ModelSpawn(
                VMapFormat.ModM2,
                0,
                1,
                new Vector3((float)position.Z, (float)position.X, (float)position.Y),
                new Vector3((float)rotation.X, (float)rotation.Y, (float)rotation.Z),
                (float)(rawScale / 1024.0),
                Vector3.Zero,
                Vector3.Zero,
                "x.m2");
            Vector3 server = new ModelInstance(spawn, model).FromModel(new Vector3((float)local.X, (float)local.Y, (float)local.Z));
            Vec3d expected = mine.ModelToInternal(local);

            Assert.True(Math.Abs(expected.X - server.X) < 0.02 && Math.Abs(expected.Y - server.Y) < 0.02 && Math.Abs(expected.Z - server.Z) < 0.02,
                $"case {i}: mine {expected} server {server}");
        }
    }

    [Fact]
    public void ForWmo_AppliesTheOriginQuirkOnlyWhenRawXAndZAreBothZero()
    {
        InstanceTransform quirk = PlacementTransform.ForWmo(WmoPlacement(new WorldPoint(0, 12.5, 0), new WorldPoint(0, 0, 0)), out bool quirked);
        WorldPoint world = PlacementTransform.InternalToWorld(quirk.Position);

        Assert.True(quirked);
        Assert.Equal(0, world.X, 2);
        Assert.Equal(0, world.Y, 2);
        Assert.Equal(12.5, world.Z, 6);

        PlacementTransform.ForWmo(WmoPlacement(new WorldPoint(0, 12.5, 1), new WorldPoint(0, 0, 0)), out bool xOnly);
        PlacementTransform.ForWmo(WmoPlacement(new WorldPoint(1, 12.5, 0), new WorldPoint(0, 0, 0)), out bool zOnly);
        Assert.False(xOnly);
        Assert.False(zOnly);
    }

    [Fact]
    public void ForWmo_HasUnitScale()
    {
        InstanceTransform transform = PlacementTransform.ForWmo(WmoPlacement(new WorldPoint(1, 2, 3), new WorldPoint(0, 45, 0)), out _);

        Assert.Equal(1.0, transform.Scale);
    }

    [Fact]
    public void ForWmoDoodad_RotatesTheLocalPositionByTheWmoRotation()
    {
        // rot.Y = 90 turns WMO-local +X into +Y (right-handed yaw about Z).
        InstanceTransform wmo = PlacementTransform.ForWmo(WmoPlacement(new WorldPoint(100, 5, 200), new WorldPoint(0, 90, 0)), out _);
        var doodad = new WmoDoodad(0, 0, "d.mdx", new Vector3(1, 2, 3), Quaternion.Identity, 2f, 0);

        InstanceTransform placed = PlacementTransform.ForWmoDoodad(wmo, doodad);

        Assert.Equal(200 - 2, placed.Position.X, 6);
        Assert.Equal(100 + 1, placed.Position.Y, 6);
        Assert.Equal(5 + 3, placed.Position.Z, 6);
        Assert.Equal(2.0, placed.Scale);
    }

    [Fact]
    public void ForWmoDoodad_ComposesTheWmoRotationAfterTheDoodadRotation()
    {
        InstanceTransform wmo = PlacementTransform.ForWmo(WmoPlacement(new WorldPoint(0, 0, 1), new WorldPoint(0, 90, 0)), out _);
        // Doodad quaternion: 90 degrees about X; its +Y axis goes to +Z, then the WMO yaw leaves +Z alone.
        var doodad = new WmoDoodad(0, 0, "d.mdx", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), 1f, 0);

        InstanceTransform placed = PlacementTransform.ForWmoDoodad(wmo, doodad);
        Vec3d up = placed.Rotation.Transform(new Vec3d(0, 1, 0));
        Vec3d forward = placed.Rotation.Transform(new Vec3d(1, 0, 0));

        Assert.Equal(0, up.X, 6);
        Assert.Equal(0, up.Y, 6);
        Assert.Equal(1, up.Z, 6);
        // The doodad's +X is untouched by its own X rotation, then yawed to +Y by the WMO.
        Assert.Equal(0, forward.X, 6);
        Assert.Equal(1, forward.Y, 6);
    }

    [Fact]
    public void FromQuaternion_AgreesWithSystemNumerics()
    {
        var random = new SplitMix(7);
        for (int i = 0; i < 100; i++)
        {
            var q = Quaternion.Normalize(new Quaternion((float)random.Range(-1, 1), (float)random.Range(-1, 1), (float)random.Range(-1, 1), (float)random.Range(-1, 1)));
            Mat3d mine = Mat3d.FromQuaternion(q);
            Matrix4x4 reference = Matrix4x4.CreateFromQuaternion(q);
            var v = new Vector3((float)random.Range(-5, 5), (float)random.Range(-5, 5), (float)random.Range(-5, 5));

            Vec3d rotated = mine.Transform(Vec3d.From(v));
            Vector3 expected = Vector3.Transform(v, q);

            Assert.True(Math.Abs(rotated.X - expected.X) < 1e-4 && Math.Abs(rotated.Y - expected.Y) < 1e-4 && Math.Abs(rotated.Z - expected.Z) < 1e-4, $"case {i}");
            Assert.Equal(reference.M11, mine.M00, 4);
            Assert.Equal(reference.M12, mine.M10, 4);
        }
    }

    [Fact]
    public void FromQuaternion_FallsBackToIdentityForAZeroQuaternion()
    {
        Assert.Equal(Mat3d.Identity, Mat3d.FromQuaternion(new Quaternion(0, 0, 0, 0)));
    }

    [Fact]
    public void AngleToDegrees_MeasuresTheRelativeRotation()
    {
        Mat3d a = Mat3d.RotationZ(0.3);
        Mat3d b = Mat3d.RotationZ(0.3 + (20 * Math.PI / 180));

        Assert.Equal(0, a.AngleToDegrees(a), 6);
        Assert.Equal(20, a.AngleToDegrees(b), 6);
        Assert.Equal(180, Mat3d.Identity.AngleToDegrees(Mat3d.RotationX(Math.PI)), 6);
    }

    [Fact]
    public void ExtractorDoodadRotation_StoresTheXyzDecompositionOfDoodadTimesWmo()
    {
        // WMO yawed 90 degrees (rot.Y); doodad tilted 30 degrees about X. Rq * Rw = Rx(30) * Rz(90), whose XYZ
        // decomposition is x = 30, y = 0, z = 90; the extractor stores them as (rot.x, rot.y, rot.z) = (y, z, x).
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6);

        (double x, double y, double z) = PlacementTransform.ExtractorDoodadRotation(tilt, new WorldPoint(0, 90, 0));

        Assert.Equal(0, x, 3);
        Assert.Equal(90, y, 3);
        Assert.Equal(30, z, 3);
    }

    [Fact]
    public void ServerRotation_OfThatTriple_EqualsTheComposedRotationForYawedWmos()
    {
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6);
        InstanceTransform wmo = PlacementTransform.ForWmo(WmoPlacement(new WorldPoint(1, 2, 3), new WorldPoint(0, 90, 0)), out _);
        InstanceTransform doodad = PlacementTransform.ForWmoDoodad(wmo, new WmoDoodad(0, 0, "d", Vector3.Zero, tilt, 1f, 0));
        (double x, double y, double z) = PlacementTransform.ExtractorDoodadRotation(tilt, new WorldPoint(0, 90, 0));

        Mat3d stored = PlacementTransform.ServerRotation(x, y, z);

        Assert.True(doodad.Rotation.AngleToDegrees(stored) < 0.01);
    }

    [Fact]
    public void ServerRotation_DiffersFromTheComposedRotationWhenTheWmoIsRolled()
    {
        // Hand-derived: WMO rot.Z = 30 (a roll), doodad yawed 90 degrees about Z. The stored triple decodes to
        // Rz(90) * Ry(30) while the geometric composition is Rx(30) * Rz(90); the two are 60 degrees apart.
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var wmoRotation = new WorldPoint(0, 0, 30);
        InstanceTransform wmo = PlacementTransform.ForWmo(WmoPlacement(new WorldPoint(1, 2, 3), wmoRotation), out _);
        InstanceTransform composed = PlacementTransform.ForWmoDoodad(wmo, new WmoDoodad(0, 0, "d", Vector3.Zero, yaw, 1f, 0));

        (double x, double y, double z) = PlacementTransform.ExtractorDoodadRotation(yaw, wmoRotation);
        Mat3d stored = PlacementTransform.ServerRotation(x, y, z);

        Assert.Equal(30, x, 3);
        Assert.Equal(90, y, 3);
        Assert.Equal(0, z, 3);
        Assert.Equal(60, composed.Rotation.AngleToDegrees(stored), 2);
    }

    [Fact]
    public void ExtractorDoodadRotation_TakesTheGimbalLockBranch()
    {
        // Rq * Rw with rot.x = 90 makes M[0][2] = 1: G3D returns (x = atan2(m10, m11), y = pi/2, z = 0).
        Quaternion identity = Quaternion.Identity;
        (double x, double y, double z) = PlacementTransform.ExtractorDoodadRotation(identity, new WorldPoint(90, 0, 0));

        // rotation.x holds the y angle (90), rotation.y the z angle (0), rotation.z the x angle (atan2).
        Assert.Equal(90, x, 3);
        Assert.Equal(0, y, 3);
        Assert.True(double.IsFinite(z));
    }

    [Fact]
    public void InstanceTransform_RoundTripsThroughTheServersWorldConversion()
    {
        InstanceTransform transform = PlacementTransform.ForM2(M2Placement(new WorldPoint(17000, 60, 26000), new WorldPoint(10, 20, 30), 1024));
        Vec3d model = new(1, 2, 3);

        WorldPoint world = transform.ModelToWorld(model);
        Vec3d back = PlacementTransform.WorldToInternal(world);
        Vec3d expected = transform.ModelToInternal(model);

        Assert.Equal(expected.X, back.X, 6);
        Assert.Equal(expected.Y, back.Y, 6);
        Assert.Equal(expected.Z, back.Z, 6);
    }
}
