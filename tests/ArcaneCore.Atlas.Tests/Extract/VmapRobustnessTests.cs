using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>Runs work on the pool and fails the test, instead of hanging it, when it does not finish.</summary>
internal static class VmapWithin
{
    public static async Task<T> RunAsync<T>(Func<T> work, int seconds = 60)
        => await Task.Run(work).WaitAsync(TimeSpan.FromSeconds(seconds));
}

/// <summary><see cref="VmapBih"/> against input the reference tool loops on for ever.</summary>
public sealed class VmapBihNonFiniteTests
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task Build_NonFiniteBound_IsRefusedInsteadOfLoopingForEver(float bad)
    {
        var boxes = new List<(Vector3 Low, Vector3 High)>();
        for (int i = 0; i < 12; i++)
        {
            boxes.Add((new Vector3(10 * i, 0, 0), new Vector3((10 * i) + 1, 1, 1)));
        }

        boxes[5] = (new Vector3(bad, 0, 0), new Vector3(51, 1, 1));

        Exception? thrown = await VmapWithin.RunAsync(() =>
        {
            try
            {
                VmapBih.Build(boxes, leafSize: 3);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }, seconds: 20);

        Assert.IsType<InvalidDataException>(thrown);
        Assert.Contains("not finite", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FiniteBoundsAreUnchanged_ByTheFiniteCheck()
    {
        BihTree tree = VmapBih.Build([(new Vector3(0), new Vector3(1)), (new Vector3(10), new Vector3(11)), (new Vector3(20), new Vector3(21)), (new Vector3(30), new Vector3(31))], leafSize: 1);

        Assert.Equal(4, tree.Objects.Count);
    }
}

/// <summary>Placements and models the server's readers would refuse are dropped or reported by the extractor.</summary>
public sealed class VmapNonFinitePlacementTests
{
    private const string NanWmoPath = "World\\wmo\\Test\\Nan.wmo";
    private const string ScalyWmoPath = "World\\wmo\\Test\\Scaly.wmo";
    private static readonly TileKey KalimdorTile = new(1, 40, 30);

    private static void AddKalimdor(VmapFixture fixture, AdtSpec tile)
        => SyntheticTerrain.AddMap(fixture.Files, "Kalimdor", SyntheticTerrain.Wdt([(KalimdorTile.X, KalimdorTile.Y)]), (KalimdorTile, SyntheticTerrain.Adt(tile).Bytes));

    private static IEnumerable<ModelSpawn> TileSpawns(string directory)
        => Directory.EnumerateFiles(directory, "001_*.vmtile").Order(StringComparer.Ordinal).SelectMany(f => VMapTree.ParseTile(File.ReadAllBytes(f))).Select(s => s.Spawn);

    [Fact]
    public async Task Extract_WmoWithANonFiniteGroupBound_IsReportedAndNotPlaced_InsteadOfHanging()
    {
        var fixture = new VmapFixture();

        // Eight groups, so the group tree has to split; one group's first bound corner is NaN (MOGP header at 20, bound at +12).
        fixture.Files.Add(NanWmoPath, ModelFixtures.WmoRoot(new SyntheticWmoRoot { GroupCount = 8 }));
        for (int g = 0; g < 8; g++)
        {
            byte[] group = ModelFixtures.WmoGroup(ModelFixtures.WallGroup(0));
            if (g == 3)
            {
                BinaryPrimitives.WriteSingleLittleEndian(group.AsSpan(32), float.NaN);
            }

            fixture.Files.Add($"World\\wmo\\Test\\Nan_{g:D3}.wmo", group);
        }

        var tile = new AdtSpec { Tile = KalimdorTile };
        tile.Wmos.Add(VmapFixture.WallAt(9100, NanWmoPath, new Vector3(-9000, 100, 50)));
        tile.Wmos.Add(VmapFixture.WallAt(9101, VmapFixture.WallPath, new Vector3(-9100, 100, 50)));
        AddKalimdor(fixture, tile);
        using var output = new TestDirectory();

        VmapExtractReport report = await VmapWithin.RunAsync(() => fixture.Extractor().Extract(output.Path, [1], threads: 2, CancellationToken.None, Path.Combine(output.Path, VmapExtractor.VmapsFolder)));

        Assert.Contains(report.Problems, p => p.Contains("Nan.wmo", StringComparison.Ordinal) && p.Contains("not finite", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(report.VmapsDirectory), f => f.Contains("Nan.wmo", StringComparison.Ordinal));
        Assert.Contains(TileSpawns(report.VmapsDirectory), s => s.Name == "Wall.wmo");
        Assert.DoesNotContain(TileSpawns(report.VmapsDirectory), s => s.Name == "Nan.wmo");
    }

    [Fact]
    public async Task Extract_DropsPlacementsTheServerWouldRefuse_AndReportsEachKindAndNameOnce()
    {
        var fixture = new VmapFixture();

        // A WMO with one doodad whose scale is NaN.
        var scaly = new SyntheticWmoRoot { GroupCount = 1 };
        scaly.DoodadSets.Add(("Set", 0, 1));
        scaly.Doodads.Add((VmapFixture.CubeStoredPath, new Vector3(3, 0, 0), Quaternion.Identity, float.NaN));
        fixture.Files.Add(ScalyWmoPath, ModelFixtures.WmoRoot(scaly));
        SyntheticWmoGroup scalyGroup = ModelFixtures.WallGroup(0);
        scalyGroup.DoodadReferences.Add(0);
        fixture.Files.Add("World\\wmo\\Test\\Scaly_000.wmo", ModelFixtures.WmoGroup(scalyGroup));

        var tile = new AdtSpec { Tile = KalimdorTile };
        WorldPoint raw = ModelWorld.Raw(-9300, 200, 60);
        tile.Doodads.Add(new DoodadSpec(VmapFixture.CubeStoredPath, 7101, raw, new WorldPoint(0, 0, 0), 0, 0));
        tile.Doodads.Add(new DoodadSpec(VmapFixture.CubeStoredPath, 7102, new WorldPoint(double.NaN, 0, 0), new WorldPoint(0, 0, 0), 1024, 0));
        tile.Doodads.Add(new DoodadSpec(VmapFixture.CubeStoredPath, 7103, new WorldPoint(0, 0, 0), new WorldPoint(0, double.PositiveInfinity, 0), 1024, 0));
        tile.Doodads.Add(new DoodadSpec(VmapFixture.CubeStoredPath, 7104, ModelWorld.Raw(-9310, 210, 60), new WorldPoint(0, 0, 0), 1024, 0));
        tile.Wmos.Add(VmapFixture.WallAt(9102, VmapFixture.WallPath, new Vector3(-9400, 100, 50)) with { Position = new WorldPoint(double.NaN, 0, 0) });
        tile.Wmos.Add(VmapFixture.WallAt(9103, ScalyWmoPath, new Vector3(-9500, 100, 50)));
        AddKalimdor(fixture, tile);
        using var output = new TestDirectory();

        VmapExtractReport report = await VmapWithin.RunAsync(() => fixture.Extractor().Extract(output.Path, [1], threads: 2, CancellationToken.None, Path.Combine(output.Path, VmapExtractor.VmapsFolder)));

        // The tile parses with the server's own reader, which refuses a non-finite or non-positive transform.
        ModelSpawn[] spawns = [.. TileSpawns(report.VmapsDirectory)];
        Assert.Equal(new[] { "Cube.m2", "Scaly.wmo" }, spawns.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, report.Maps[0].Spawns);
        Assert.Single(report.Problems, p => p.Contains("M2 placements of 'Cube.m2' were dropped", StringComparison.Ordinal));
        Assert.Single(report.Problems, p => p.Contains("WMO placements of 'Wall.wmo' were dropped", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Problems, p => p.Contains("'Scaly.wmo'", StringComparison.Ordinal));

        var server = new VMapManager(report.VmapsDirectory);
        Assert.True(server.LoadTile(1, KalimdorTile.X, KalimdorTile.Y));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Extract_MapEndingWithNoPlacements_DeletesItsOldTreeAndTiles_AndNothingElse(bool allPlacementsDropped)
    {
        var fixture = new VmapFixture();
        var tile = new AdtSpec { Tile = KalimdorTile };
        if (allPlacementsDropped)
        {
            tile.Doodads.Add(new DoodadSpec(VmapFixture.CubeStoredPath, 7101, ModelWorld.Raw(-9300, 200, 60), new WorldPoint(0, 0, 0), 0, 0));
            AddKalimdor(fixture, tile);
        }
        else
        {
            SyntheticTerrain.AddMap(fixture.Files, "Kalimdor", SyntheticTerrain.Wdt([]));
        }

        using var output = new TestDirectory();
        string tree = output.Write(Path.Combine("vmaps", "001.vmtree"), [9]);
        string staleTile = output.Write(Path.Combine("vmaps", "001_05_05.vmtile"), [9]);
        string otherTree = output.Write(Path.Combine("vmaps", "002.vmtree"), [9]);
        string otherTile = output.Write(Path.Combine("vmaps", "002_05_05.vmtile"), [9]);
        string model = output.Write(Path.Combine("vmaps", "Cube.m2.vmo"), [9]);
        string unrelated = output.Write(Path.Combine("vmaps", "001_notes.txt"), [9]);

        VmapExtractReport report = await VmapWithin.RunAsync(() => fixture.Extractor().Extract(output.Path, [1], threads: 1, CancellationToken.None, Path.Combine(output.Path, VmapExtractor.VmapsFolder)));

        Assert.Equal(0, report.Maps[0].Spawns);
        Assert.False(File.Exists(tree));
        Assert.False(File.Exists(staleTile));
        Assert.True(File.Exists(otherTree));
        Assert.True(File.Exists(otherTile));
        Assert.True(File.Exists(model));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Extract_DoodadNamedByAWmoAndByATile_ReportsTheNameCollision_AndTheWmosPathWins()
    {
        var fixture = new VmapFixture();
        fixture.Files.Add("World\\Doodads\\Two\\Thing.m2", ModelFixtures.CubeM2(3f));
        fixture.Files.Add("World\\Doodads\\Three\\Thing.m2", ModelFixtures.CubeM2(2f));
        var holder = new SyntheticWmoRoot { GroupCount = 1 };
        holder.DoodadSets.Add(("Set", 0, 1));
        holder.Doodads.Add(("World\\Doodads\\Two\\Thing.mdx", Vector3.Zero, Quaternion.Identity, 1f));
        fixture.Files.Add("World\\wmo\\Test\\Holder.wmo", ModelFixtures.WmoRoot(holder));

        var tile = new AdtSpec { Tile = KalimdorTile };
        tile.Doodads.Add(new DoodadSpec("World\\Doodads\\Three\\Thing.mdx", 7300, ModelWorld.Raw(-9300, 200, 60), new WorldPoint(0, 0, 0), 1024, 0));
        AddKalimdor(fixture, tile);
        using var output = new TestDirectory();

        VmapExtractReport report = await VmapWithin.RunAsync(() => fixture.Extractor().Extract(output.Path, [1], threads: 1, CancellationToken.None, Path.Combine(output.Path, VmapExtractor.VmapsFolder)));

        Assert.Single(report.Problems, p => p.Contains("M2 name 'Thing.m2' is used by both 'World\\Doodads\\Two\\Thing.m2' and 'World\\Doodads\\Three\\Thing.m2'", StringComparison.Ordinal));
        string written = Path.Combine(report.VmapsDirectory, "Thing.m2.vmo");
        var library = new VmapModelLibrary(fixture.Files);
        VmapModelInfo expected = library.Doodad("World\\Doodads\\Two\\Thing.mdx").Model!;
        Assert.Equal(library.Load(expected).ToWorldModel().ToBytes(), File.ReadAllBytes(written));
    }
}

/// <summary><see cref="VmapModelLibrary"/>'s M2 name resolution against the reference's.</summary>
public sealed class VmapModelNameTests
{
    private const string One = "World\\Doodads\\One\\Thing.mdx";
    private const string Two = "World\\Doodads\\Two\\Thing.mdx";
    private const string Three = "World\\Doodads\\Three\\Thing.mdx";

    private static VmapFixture Fixture()
    {
        var fixture = new VmapFixture();
        fixture.Files.Add("World\\Doodads\\One\\Thing.m2", ModelFixtures.CubeM2(withHull: false));
        fixture.Files.Add("World\\Doodads\\Two\\Thing.m2", ModelFixtures.CubeM2(3f));
        fixture.Files.Add("World\\Doodads\\Three\\Thing.m2", ModelFixtures.CubeM2(2f));
        return fixture;
    }

    [Fact]
    public void Doodad_PathThatFails_DoesNotBlockALaterPathWithTheSamePlainName()
    {
        var library = new VmapModelLibrary(Fixture().Files);

        Assert.Null(library.Doodad(One).Model);
        (string name, VmapModelInfo? later) = library.Doodad(Two);

        Assert.Equal("Thing.m2", name);
        Assert.Equal("World\\Doodads\\Two\\Thing.m2", later?.ClientPath);
    }

    [Fact]
    public void Doodad_PathThatFailed_IsNotReadAgain()
    {
        var counting = new CountingFiles(Fixture().Files);
        var library = new VmapModelLibrary(counting);

        library.Doodad(One);
        int afterFirst = counting.Reads;
        library.Doodad(One);
        library.Doodad(One);

        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, counting.Reads);
    }

    [Fact]
    public void Doodad_TwoPathsThatBothConvert_ReportTheCollisionOnce_AndTheFirstWins()
    {
        var library = new VmapModelLibrary(Fixture().Files);

        VmapModelInfo? first = library.Doodad(Two).Model;
        VmapModelInfo? second = library.Doodad(Three).Model;
        library.Doodad(Three);
        library.Doodad(Two);

        Assert.Same(first, second);
        Assert.Equal("World\\Doodads\\Two\\Thing.m2", second?.ClientPath);
        Assert.Single(library.Problems, p => p == "M2 name 'Thing.m2' is used by both 'World\\Doodads\\Two\\Thing.m2' and 'World\\Doodads\\Three\\Thing.m2'; placements get the first.");
    }

    [Fact]
    public void Doodad_LaterPathThatDoesNotConvert_OrIsTheSameFile_IsNotACollision()
    {
        var library = new VmapModelLibrary(Fixture().Files);

        library.Doodad(Two);
        library.Doodad(One);
        library.Doodad("WORLD\\DOODADS\\TWO\\THING.MDX");
        library.Doodad("World\\Doodads\\Two\\Thing.m2");

        Assert.Empty(library.Problems);
    }

    [Fact]
    public void ExtractWmoDoodadModels_ResolvesEveryRootWmosDoodadNames_BeforeAnyPlacement()
    {
        VmapFixture fixture = Fixture();
        var holder = new SyntheticWmoRoot { GroupCount = 1 };
        holder.DoodadSets.Add(("Set", 0, 1));
        holder.Doodads.Add((Two, Vector3.Zero, Quaternion.Identity, 1f));
        fixture.Files.Add("World\\wmo\\Test\\Holder.wmo", ModelFixtures.WmoRoot(holder));
        var library = new VmapModelLibrary(fixture.Files);

        library.ExtractWmoDoodadModels(CancellationToken.None);

        Assert.Equal("World\\Doodads\\Two\\Thing.m2", library.Doodad(Three).Model?.ClientPath);
        Assert.Single(library.Problems, p => p.Contains("M2 name 'Thing.m2' is used by both", StringComparison.Ordinal));
    }

    private sealed class CountingFiles(IClientFileSource inner) : IClientFileSource
    {
        public int Reads { get; private set; }

        public string Description => inner.Description;

        public bool Exists(string path) => inner.Exists(path);

        public byte[]? Read(string path)
        {
            if (path.Contains("One", StringComparison.Ordinal))
            {
                Reads++;
            }

            return inner.Read(path);
        }

        public IReadOnlyList<string> List(string prefix) => inner.List(prefix);

        public string? ArchiveOf(string path) => inner.ArchiveOf(path);
    }
}
