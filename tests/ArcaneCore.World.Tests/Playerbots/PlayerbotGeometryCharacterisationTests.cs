using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Characterisation of the shared Playerbot helpers the de-duplication converges on: the 3D distance of two world objects, the finiteness
/// of a point, the flat projection, the single-CString payload and the atomic QA file writer. Expected values are exact squares/roots
/// (3-4-12 is 13, 1-2-2 is 3, 3-4-0 is 5), so they hold for any correct evaluation order.
/// </summary>
public sealed class PlayerbotGeometryCharacterisationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "playerbot-geometry-" + Guid.NewGuid().ToString("N"));

    public PlayerbotGeometryCharacterisationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Creature At(uint guid, float x, float y, float z)
    {
        var template = new CreatureTemplate { Entry = guid, Name = "Probe", MinLevel = 1, MaxLevel = 1, MinLevelHealth = 1, MaxLevelHealth = 1, Faction = 35 };
        var spawn = new CreatureSpawn { Guid = guid, Entry = guid, MapId = 0, X = x, Y = y, Z = z };
        return new Creature(guid, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
    }

    [Fact]
    public void Distance_IsTheExactThreeDimensionalEuclideanDistance()
    {
        Assert.Equal(13f, PlayerbotGeometry.Distance(At(990701, 0, 0, 0), At(990702, 3, 4, 12)));
        Assert.Equal(3f, PlayerbotGeometry.Distance(At(990701, 0, 0, 0), At(990702, 1, 2, 2)));
    }

    [Fact]
    public void Distance_IsSymmetric_AndZeroForTheSamePoint()
    {
        Creature a = At(990703, -10, 20, 5), b = At(990704, 20, -20, 5);
        Assert.Equal(50f, PlayerbotGeometry.Distance(a, b));
        Assert.Equal(PlayerbotGeometry.Distance(a, b), PlayerbotGeometry.Distance(b, a));
        Assert.Equal(0f, PlayerbotGeometry.Distance(a, a));
    }

    [Fact]
    public void MotionDistance_ForNonPlayers_UsesTheStoredPositions()
    {
        Assert.Equal(5f, PlayerbotGeometry.MotionDistance(At(990705, 0, 0, 7), At(990706, 3, 4, 7)));
        Assert.Equal(13f, PlayerbotGeometry.MotionDistance(At(990705, 0, 0, 0), At(990706, 3, 4, 12)));
    }

    [Fact]
    public void MotionPosition_ForNonPlayers_IsTheStoredXyz()
        => Assert.Equal(new Vector3(1.5f, -2.5f, 3.5f), PlayerbotGeometry.MotionPosition(At(990707, 1.5f, -2.5f, 3.5f)));

    [Theory]
    [InlineData(0f, 0f, 0f, true)]
    [InlineData(-1e30f, float.MaxValue, float.MinValue, true)]
    [InlineData(float.NaN, 0f, 0f, false)]
    [InlineData(0f, float.NaN, 0f, false)]
    [InlineData(0f, 0f, float.NaN, false)]
    [InlineData(float.PositiveInfinity, 0f, 0f, false)]
    [InlineData(0f, float.NegativeInfinity, 0f, false)]
    [InlineData(0f, 0f, float.PositiveInfinity, false)]
    public void Vector3IsFinite_RequiresEveryComponentFinite(float x, float y, float z, bool expected)
        => Assert.Equal(expected, new Vector3(x, y, z).IsFinite());

    [Fact]
    public void Flat_DropsZ()
    {
        Assert.Equal(new Vector2(1f, 2f), PlayerbotGeometry.Flat(new Vector3(1f, 2f, 3f)));
        Assert.Equal(new Vector2(-4.5f, 0f), PlayerbotGeometry.Flat(new Vector3(-4.5f, 0f, float.NaN)));
    }

    [Fact]
    public void OneString_IsTheNameThenOneNulTerminator()
    {
        Assert.Equal("World\0"u8.ToArray(), PlayerbotSocialPackets.OneString("World"));
        Assert.Equal(new byte[] { 0 }, PlayerbotSocialPackets.OneString(string.Empty));
    }

    [Fact]
    public void WriteAtomic_CreatesThenOverwrites_AndLeavesNoTempFile()
    {
        string path = Path.Combine(_dir, "report.md");
        QaFiles.WriteAtomic(path, "first");
        Assert.Equal("first", File.ReadAllText(path));
        QaFiles.WriteAtomic(path, "second");
        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void WriteAtomic_MissingDirectory_ThrowsDirectoryNotFoundException()
        => Assert.Throws<DirectoryNotFoundException>(() => QaFiles.WriteAtomic(Path.Combine(_dir, "absent", "x.md"), "text"));
}
