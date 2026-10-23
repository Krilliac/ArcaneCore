using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// Characterisation of the atomic QA file write (sibling <c>.tmp</c>, then overwriting move; IO exceptions surface to the caller that
/// logs and retries) and of the <c>CreatureSpawn</c> finiteness extension the destination planners filter with.
/// </summary>
public sealed class QaFilesWriteAtomicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-files-" + Guid.NewGuid().ToString("N"));

    public QaFilesWriteAtomicTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WriteAtomic_CreatesTheFile_AndLeavesNoTempFile()
    {
        string path = Path.Combine(_dir, "report.md");
        QaFiles.WriteAtomic(path, "hello");
        Assert.Equal("hello", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void WriteAtomic_OverwritesAnExistingFile()
    {
        string path = Path.Combine(_dir, "report.json");
        File.WriteAllText(path, "old and much longer content");
        QaFiles.WriteAtomic(path, "new");
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
    }

    [Fact]
    public void WriteAtomic_EmptyText_WritesAnEmptyFile()
    {
        string path = Path.Combine(_dir, "empty.md");
        QaFiles.WriteAtomic(path, string.Empty);
        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void WriteAtomic_MissingDirectory_SurfacesDirectoryNotFoundException()
        => Assert.Throws<DirectoryNotFoundException>(() => QaFiles.WriteAtomic(Path.Combine(_dir, "absent", "x.md"), "t"));

    [Fact]
    public void WriteAtomic_TargetIsADirectory_SurfacesAnIoOrAccessException()
    {
        string path = Path.Combine(_dir, "taken");
        Directory.CreateDirectory(path);
        Exception ex = Assert.ThrowsAny<Exception>(() => QaFiles.WriteAtomic(path, "t"));
        Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().FullName);
    }

    private static CreatureSpawn Spawn(float x, float y, float z)
        => new() { Guid = 1, Entry = 1, MapId = 0, X = x, Y = y, Z = z };

    [Fact]
    public void CreatureSpawnIsFinite_AcceptsFiniteCoordinates()
        => Assert.True(Spawn(-1000f, float.MaxValue, 0f).IsFinite());

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void CreatureSpawnIsFinite_RejectsAnyNonFiniteCoordinate(float x, float y, float z)
        => Assert.False(Spawn(x, y, z).IsFinite());
}
