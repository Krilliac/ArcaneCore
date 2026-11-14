using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// The resume rule of <see cref="MmapExtractor"/>: a tile on disk is kept only when it was built against the parameter file
/// that is on disk now. A tile's header X and Y are its slot counted from the origin of the run that built it, so a tile
/// left from another origin must never survive next to a parameter file that no longer describes it.
/// </summary>
public sealed class MmapResumeTests(MmapWorld world) : IClassFixture<MmapWorld>
{
    private const string Parameters = "000.mmap";
    private const string OldTile = "0004931.mmtile";

    /// <summary>A private copy of the shared world's maps, vmaps and mmaps.</summary>
    private TestDirectory Copy()
    {
        var copy = new TestDirectory();
        foreach (string folder in new[] { "maps", "vmaps", "mmaps" })
        {
            string to = Path.Combine(copy.Path, folder);
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(Path.Combine(world.DataRoot, folder)))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
        }

        return copy;
    }

    /// <summary>
    /// A new terrain tile (server 50, 30) that sorts before the tile on disk (49, 31) and moves Azeroth's origin: the tile on
    /// disk was built against the old origin.
    /// </summary>
    private static void MoveTheOrigin(TestDirectory copy)
        => File.Copy(Path.Combine(copy.Path, "maps", "0004931.map"), Path.Combine(copy.Path, "maps", "0005030.map"));

    private static Dictionary<string, byte[]> MapFiles(string directory)
        => Directory.EnumerateFiles(directory, "000*").ToDictionary(f => Path.GetFileName(f)!, File.ReadAllBytes, StringComparer.Ordinal);

    [Fact]
    public void AnInterruptedStaleRebuild_LeavesNoOldOriginTile_ForTheResumeToKeep()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        NavMeshTile old = NavMeshTile.ParseFile(File.ReadAllBytes(Path.Combine(mmaps, OldTile)));
        MoveTheOrigin(copy);
        string unrelated = Path.Combine(mmaps, "0004931.txt");
        File.WriteAllBytes(unrelated, [7]);
        string otherMap = Path.Combine(mmaps, "0363131.mmtile");
        byte[] otherMapBytes = File.ReadAllBytes(otherMap);

        // Stop after the first tile is written: the new tile (50, 30) is built first, the old (49, 31) would be second.
        using var cancel = new CancellationTokenSource();
        var interrupted = new MmapExtractor(copy.Path) { TileWritten = _ => cancel.Cancel() };
        Assert.ThrowsAny<OperationCanceledException>(() => interrupted.Extract([0], overwrite: false, quick: false, threads: 1, cancel.Token));

        MmapExtractReport resumed = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 1, CancellationToken.None);

        // Only the tile the interrupted run wrote is kept; the old-origin tile was deleted and is built again.
        MmapExtractMapReport azeroth = Assert.Single(resumed.Maps);
        Assert.Equal((1, 1), (azeroth.Existing, azeroth.Written));
        using var fresh = new TestDirectory();
        new MmapExtractor(copy.Path, fresh.Path).Extract([0], overwrite: true, quick: false, threads: 1, CancellationToken.None);
        Dictionary<string, byte[]> expected = MapFiles(fresh.Path);
        Dictionary<string, byte[]> actual = MapFiles(mmaps);
        Assert.Equal(expected.Keys.Append("0004931.txt").Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach ((string name, byte[] bytes) in expected)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(actual[name]), $"{name} is not what a clean run writes");
        }

        NavMeshTile rebuilt = NavMeshTile.ParseFile(actual[OldTile]);
        Assert.NotEqual((old.X, old.Y), (rebuilt.X, rebuilt.Y));

        // Only this map's tile files go: a file with another name and another map's tile stay.
        Assert.True(File.Exists(unrelated));
        Assert.Equal(otherMapBytes, File.ReadAllBytes(otherMap));
    }

    [Fact]
    public void TheStaleRebuildsProblem_CountsTheDeletedAndTheRebuiltTiles()
    {
        using TestDirectory copy = Copy();
        MoveTheOrigin(copy);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 2, CancellationToken.None);

        string problem = Assert.Single(report.Problems);
        Assert.Contains("000.mmap differs", problem, StringComparison.Ordinal);
        Assert.Contains("1 existing tile file(s) were deleted and 2 of the map's 3 tile(s) rebuilt", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneTile_AgainstChangedParameters_IsRefused_AndWritesAndDeletesNothing(bool overwrite)
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        MoveTheOrigin(copy);
        Dictionary<string, byte[]> before = MapFiles(mmaps);

        ArgumentException e = Assert.Throws<ArgumentException>(() => new MmapExtractor(copy.Path).Extract([0], overwrite, quick: false, threads: 1, CancellationToken.None, onlyTile: (50, 30)));

        Assert.Contains("navmesh parameters changed since its tiles were built", e.Message, StringComparison.Ordinal);
        Assert.Contains("build the whole map without tile=", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("overwrite=true", e.Message, StringComparison.Ordinal);
        Dictionary<string, byte[]> after = MapFiles(mmaps);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, pair => Assert.Equal(pair.Value, after[pair.Key]));
    }

    [Fact]
    public void OneTile_WithUnchangedParameters_StillBuildsThatTileAlone()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        byte[] tile = File.ReadAllBytes(Path.Combine(mmaps, OldTile));
        File.Delete(Path.Combine(mmaps, OldTile));

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 1, CancellationToken.None, onlyTile: (49, 31));

        Assert.Equal(1, Assert.Single(report.Maps).Written);
        Assert.Empty(report.Problems);
        Assert.Equal(tile, File.ReadAllBytes(Path.Combine(mmaps, OldTile)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInvalidTile_IsRefusedBeforeAnythingIsWritten(bool overwrite)
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        MoveTheOrigin(copy);
        Dictionary<string, byte[]> before = MapFiles(mmaps);

        Assert.Throws<ArgumentException>(() => new MmapExtractor(copy.Path).Extract([0], overwrite, quick: false, threads: 1, CancellationToken.None, onlyTile: (10, 10)));

        Dictionary<string, byte[]> after = MapFiles(mmaps);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, pair => Assert.Equal(pair.Value, after[pair.Key]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStaleTileThatRebuildsEmpty_IsDeleted_WithTheBulkDeletionOrTheEmptyRule(bool overwrite)
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");

        // Tile (50, 30) has a terrain file that stores no heights, so its mesh is empty, but an old tile file is there.
        File.WriteAllBytes(Path.Combine(copy.Path, "maps", "0005030.map"), MmapMapFile.Build(noHeights: true));
        string planted = Path.Combine(mmaps, "0005030.mmtile");
        File.Copy(Path.Combine(mmaps, OldTile), planted);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite, quick: false, threads: 1, CancellationToken.None);

        MmapExtractMapReport azeroth = Assert.Single(report.Maps);
        Assert.Equal(2, azeroth.Empty);
        Assert.False(File.Exists(planted));
        Assert.True(File.Exists(Path.Combine(mmaps, OldTile)));
    }

    [Fact]
    public void ATileThatIsRebuiltEmpty_HasItsOldFileDeleted_WhenTheParametersDidNotChange()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        MoveTheOrigin(copy);
        new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 1, CancellationToken.None);
        string built = Path.Combine(mmaps, "0005030.mmtile");
        Assert.True(File.Exists(built));

        // The terrain file of the tile now stores no heights; the parameter file is the one on disk, so only the empty rule applies.
        File.WriteAllBytes(Path.Combine(copy.Path, "maps", "0005030.map"), MmapMapFile.Build(noHeights: true));
        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: true, quick: false, threads: 1, CancellationToken.None);

        Assert.Equal(2, Assert.Single(report.Maps).Empty);
        Assert.False(File.Exists(built));
        Assert.True(File.Exists(Path.Combine(mmaps, OldTile)));
    }

    [Fact]
    public void ATileThatFailsToRebuild_HasItsOldFileDeleted()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        string map = Path.Combine(copy.Path, "maps", "0004931.map");
        File.WriteAllBytes(map, File.ReadAllBytes(map)[..50]);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: true, quick: false, threads: 1, CancellationToken.None);

        Assert.True(Assert.Single(report.Maps).Failed >= 1);
        Assert.False(File.Exists(Path.Combine(mmaps, OldTile)));
    }

    [Fact]
    public void ATileThatIsNotCurrentAndRebuildsEmpty_HasItsOldFileDeleted_ByTheEmptyRuleAlone_WithoutOverwrite()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");

        // A tile of the map's own tile set that has no tile file (its mesh is empty), and a file of that name that is not a tile file.
        // The tile set and so the parameter file are untouched, so only the empty rule can remove it.
        string[] fromMaps = [.. Directory.EnumerateFiles(Path.Combine(copy.Path, "maps"), "000*.map").Select(f => Path.GetFileNameWithoutExtension(f)! + ".mmtile")];
        string[] fromVmaps = [.. Directory.EnumerateFiles(Path.Combine(copy.Path, "vmaps"), "000_*.vmtile").Select(f => "000" + Path.GetFileName(f)[7..9] + Path.GetFileName(f)[4..6] + ".mmtile")];
        string? emptyTile = fromMaps.Concat(fromVmaps).Distinct().Order(StringComparer.Ordinal).FirstOrDefault(n => !File.Exists(Path.Combine(mmaps, n)));
        Assert.NotNull(emptyTile);
        byte[] parametersBefore = File.ReadAllBytes(Path.Combine(mmaps, Parameters));
        string planted = Path.Combine(mmaps, emptyTile);
        File.WriteAllBytes(planted, [1, 2, 3]);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 1, CancellationToken.None);

        Assert.Empty(report.Problems);
        Assert.False(File.Exists(planted));
        Assert.True(File.Exists(Path.Combine(mmaps, OldTile)));
        Assert.Equal(parametersBefore, File.ReadAllBytes(Path.Combine(mmaps, Parameters)));
    }

    [Fact]
    public void OverwriteWithChangedParameters_DeletesEveryOldTile_RebuildsAll_AndReportsIt()
    {
        using TestDirectory copy = Copy();
        MoveTheOrigin(copy);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: true, quick: false, threads: 1, CancellationToken.None);

        Assert.Equal(2, Assert.Single(report.Maps).Written);
        string problem = Assert.Single(report.Problems);
        Assert.Contains("000.mmap differs", problem, StringComparison.Ordinal);
        Assert.Contains("1 existing tile file(s) were deleted and 2 of the map's 3 tile(s) rebuilt", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStaleDeletion_OnlyTouchesTheMapsOwnTileNames_LookAlikesSurvive()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        MoveTheOrigin(copy);

        // The first three pass the "000*.mmtile" search but are not names this map's tiles have; the last two are other maps' tiles.
        string[] fileLookAlikes = ["000ab31.mmtile", "0004931x.mmtile", "0001.mmtile", "0010101.mmtile", "1000101.mmtile"];
        foreach (string name in fileLookAlikes)
        {
            File.WriteAllBytes(Path.Combine(mmaps, name), [9, 9]);
        }

        string folder = Path.Combine(mmaps, "0000101.mmtile");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "inside.bin"), [5]);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 1, CancellationToken.None);

        Assert.Contains("1 existing tile file(s) were deleted", Assert.Single(report.Problems), StringComparison.Ordinal);
        foreach (string name in fileLookAlikes)
        {
            Assert.Equal<byte>([9, 9], File.ReadAllBytes(Path.Combine(mmaps, name)));
        }

        Assert.True(File.Exists(Path.Combine(folder, "inside.bin")));
    }

    [WindowsFact]
    public void ADifferentlyCasedStaleTile_IsFoundAndDeleted_WhereTheFileSystemIgnoresCase()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        File.Move(Path.Combine(mmaps, OldTile), Path.Combine(mmaps, "0004931.MMTILE"));
        Assert.True(File.Exists(Path.Combine(mmaps, OldTile)), "The test needs a file system that ignores case (a default NTFS folder does).");
        MoveTheOrigin(copy);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 1, CancellationToken.None);

        Assert.Contains("1 existing tile file(s) were deleted", Assert.Single(report.Problems), StringComparison.Ordinal);
        Assert.Equal(2, Assert.Single(report.Maps).Written);
    }

    [WindowsFact]
    public void WhenAnOldTileCannotBeDeleted_TheMapIsReported_AndTheParameterFileIsNotWritten()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        MoveTheOrigin(copy);
        byte[] parametersBefore = File.ReadAllBytes(Path.Combine(mmaps, Parameters));

        MmapExtractReport report;
        using (new FileStream(Path.Combine(mmaps, OldTile), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 1, CancellationToken.None);
        }

        MmapExtractMapReport map = Assert.Single(report.Maps);
        Assert.Equal((0, 1), (map.Written, map.Failed));
        Assert.Contains("could not all be deleted (0 of 1 were)", Assert.Single(report.Problems), StringComparison.Ordinal);
        Assert.Equal(parametersBefore, File.ReadAllBytes(Path.Combine(mmaps, Parameters)));
        Assert.False(File.Exists(Path.Combine(mmaps, "0005030.mmtile")));
    }
}

/// <summary>A fact that is skipped, with its reason, off Windows: the case needs the file system or a file lock that Windows has.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only: it needs a case-insensitive file system, a file held open against deletion, or mklink /J.";
        }
    }
}
