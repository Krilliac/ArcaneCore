using System.Numerics;
using ArcaneCore.Atlas.Reference.Client.Models;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

public sealed class WowChunksTests
{
    [Fact]
    public void Scan_ReversesTheTag_AndWalksBySize()
    {
        byte[] data = ModelFixtures.Concat(
            ModelFixtures.Chunk("MVER", [17, 0, 0, 0]),
            ModelFixtures.Chunk("MOHD", new byte[10]));

        List<WowChunk> chunks = WowChunks.Scan(data, 0, data.Length);

        Assert.Equal(["MVER", "MOHD"], chunks.Select(c => c.Tag));
        Assert.Equal((8, 4), (chunks[0].Offset, chunks[0].Size));
        Assert.Equal((20, 10), (chunks[1].Offset, chunks[1].Size));
        Assert.All(chunks, c => Assert.False(c.Truncated));
        Assert.Equal((byte)'R', data[0]);
    }

    [Fact]
    public void Scan_ClampsAChunkThatOverrunsTheFile_AndFlagsIt()
    {
        byte[] data = ModelFixtures.Chunk("MOCV", new byte[16]);
        byte[] shortFile = data[..^3];

        WowChunk chunk = Assert.Single(WowChunks.Scan(shortFile, 0, shortFile.Length));

        Assert.True(chunk.Truncated);
        Assert.Equal(13, chunk.Size);
        Assert.Throws<InvalidDataException>(() => WowChunks.Required(shortFile, chunk, "test"));
    }

    [Fact]
    public void Scan_IgnoresFewerThanEightTrailingBytes()
    {
        byte[] data = ModelFixtures.Concat(ModelFixtures.Chunk("MVER", [17, 0, 0, 0]), [1, 2, 3]);

        Assert.Single(WowChunks.Scan(data, 0, data.Length));
    }

    [Fact]
    public void Scan_RejectsARangeOutsideTheFile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WowChunks.Scan(new byte[8], 0, 9));
    }

    [Fact]
    public void StringAt_ReadsUpToTheNul_AndRefusesOffsetsOutsideTheBlob()
    {
        byte[] blob = "one\0two\0"u8.ToArray();

        Assert.Equal("one", WowChunks.StringAt(blob, 0));
        Assert.Equal("two", WowChunks.StringAt(blob, 4));
        Assert.Equal("ne", WowChunks.StringAt(blob, 1));
        Assert.Null(WowChunks.StringAt(blob, blob.Length));
        Assert.Null(WowChunks.StringAt(blob, -1));
        Assert.Equal("tail", WowChunks.StringAt("tail"u8.ToArray(), 0));
    }
}

public sealed class WmoReaderTests
{
    private static SyntheticWmoRoot SampleRoot()
    {
        var root = new SyntheticWmoRoot { GroupCount = 2 };
        root.GroupNames.AddRange(["main", "antiportal"]);
        root.DoodadSets.Add(("Set_$DefaultGlobal", 0, 1));
        root.DoodadSets.Add(("Set_A", 1, 2));
        root.Doodads.Add(("World\\Doodads\\Chair.mdx", new Vector3(1, 2, 3), Quaternion.Identity, 1.5f));
        root.Doodads.Add(("World\\Doodads\\Table.mdx", new Vector3(4, 5, 6), new Quaternion(0, 0, 0.7071068f, 0.7071068f), 2f));
        root.Doodads.Add(("World\\Doodads\\Chair.mdx", new Vector3(7, 8, 9), Quaternion.Identity, 1f));
        return root;
    }

    [Fact]
    public void Root_ParsesEveryChunkWeConsume()
    {
        WmoRootFile root = WmoRootFile.Parse(ModelFixtures.WmoRoot(SampleRoot()));

        Assert.Equal(17u, root.Version);
        Assert.Equal(2u, root.Header.GroupCount);
        Assert.Equal(77u, root.Header.WmoId);
        Assert.Equal(new Vector3(-1, -2, -3), root.Header.BoundsMin);
        Assert.Equal(new Vector3(4, 5, 6), root.Header.BoundsMax);
        Assert.Equal(0x0101u, root.Header.Flags);
        Assert.Equal(2, root.Groups.Count);
        Assert.Equal(0x2001u, root.Groups[1].Flags);
        Assert.Equal("main", root.GroupNameAt(root.Groups[0].NameOffset));
        Assert.Equal("antiportal", root.GroupNameAt(root.Groups[1].NameOffset));
        Assert.Equal(["Set_$DefaultGlobal", "Set_A"], root.DoodadSets.Select(s => s.Name));
        Assert.Equal((1u, 2u), (root.DoodadSets[1].FirstDoodad, root.DoodadSets[1].DoodadCount));
        Assert.Equal(3, root.Doodads.Count);
        Assert.Equal("World\\Doodads\\Table.mdx", root.Doodads[1].ModelPath);
        Assert.Equal((byte)1, root.Doodads[1].Flags);
        Assert.Equal(new Vector3(4, 5, 6), root.Doodads[1].Position);
        Assert.Equal(0.7071068f, root.Doodads[1].Rotation.W);
        Assert.Equal(2f, root.Doodads[1].Scale);
        Assert.Equal(0xFF336699u, root.Doodads[1].Color);
        Assert.Equal(root.Doodads[0].NameOffset, root.Doodads[2].NameOffset);
    }

    [Theory]
    [InlineData("World\\wmo\\X\\Building.wmo", 7, "World\\wmo\\X\\Building_007.wmo")]
    [InlineData("World\\wmo\\X\\Building.WMO", 123, "World\\wmo\\X\\Building_123.wmo")]
    [InlineData("Building", 0, "Building_000.wmo")]
    public void GroupFilePath_AppendsAThreeDigitIndex(string root, int index, string expected)
    {
        Assert.Equal(expected, WmoRootFile.GroupFilePath(root, index));
    }

    [Fact]
    public void Root_RefusesWhatItCannotTrust()
    {
        var ancient = SampleRoot();
        ancient.Version = 16;
        var noHeader = SampleRoot();
        noHeader.OmitMohd = true;
        byte[] good = ModelFixtures.WmoRoot(SampleRoot());

        Assert.Throws<InvalidDataException>(() => WmoRootFile.Parse([]));
        Assert.Throws<InvalidDataException>(() => WmoRootFile.Parse(ModelFixtures.WmoRoot(ancient)));
        Assert.Throws<InvalidDataException>(() => WmoRootFile.Parse(ModelFixtures.WmoRoot(noHeader)));
        Assert.Throws<InvalidDataException>(() => WmoRootFile.Parse(ModelFixtures.Chunk("MOHD", new byte[64])));
        Assert.Throws<InvalidDataException>(() => WmoRootFile.Parse(good[..30]));
        Assert.Throws<InvalidDataException>(() => WmoRootFile.Parse(ModelFixtures.Concat(ModelFixtures.Chunk("MVER", [17, 0, 0, 0]), ModelFixtures.Chunk("MOHD", new byte[40]))));
    }

    [Fact]
    public void Root_ModdCountIsTheChunkSize_NotTheHeaderCount()
    {
        SyntheticWmoRoot synthetic = SampleRoot();
        byte[] data = ModelFixtures.WmoRoot(synthetic);
        // MOHD.nDoodadDefs (offset 20 of the payload) is nominal; claim a different number.
        int mohd = data.AsSpan().IndexOf("DHOM"u8) + 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(mohd + 20), 99);

        WmoRootFile root = WmoRootFile.Parse(data);

        Assert.Equal(99u, root.Header.DoodadDefinitionCount);
        Assert.Equal(3, root.Doodads.Count);
    }

    private static SyntheticWmoGroup SampleGroup()
    {
        var group = new SyntheticWmoGroup { NameOffset = 5, Flags = 0x4008, GroupId = 4242 };
        group.Vertices.AddRange([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 2)]);
        group.Triangles.Add(new SyntheticTriangle(0, 1, 2, 0x08, 3));
        group.Triangles.Add(new SyntheticTriangle(0, 2, 3, 0x24, 0xFF));
        group.DoodadReferences.AddRange([1, 2]);
        return group;
    }

    [Fact]
    public void Group_ParsesHeaderAndGeometry()
    {
        WmoGroupFile group = WmoGroupFile.Parse(ModelFixtures.WmoGroup(SampleGroup()));

        Assert.Equal(5u, group.NameOffset);
        Assert.Equal(0x4008u, group.Flags);
        Assert.Equal(4242u, group.GroupId);
        Assert.Equal(new Vector3(-10, -10, -10), group.BoundsMin);
        Assert.Equal(new Vector3(10, 10, 10), group.BoundsMax);
        Assert.Equal(3, group.PortalStart);
        Assert.Equal(2, group.PortalCount);
        Assert.Equal(1, group.TransitionBatchCount);
        Assert.Equal(2, group.InteriorBatchCount);
        Assert.Equal(3, group.ExteriorBatchCount);
        Assert.Equal(0x04030201u, group.FogIds);
        Assert.Equal(9u, group.LiquidType);
        Assert.Equal(2, group.TriangleCount);
        Assert.Equal<byte>([0x08, 0x24], group.TriangleFlags);
        Assert.Equal<byte>([3, 0xFF], group.TriangleMaterials);
        Assert.Equal<ushort>([0, 1, 2, 0, 2, 3], group.Indices);
        Assert.Equal(4, group.Vertices.Length);
        Assert.Equal(new Vector3(0, 1, 2), group.Vertices[3]);
        Assert.Equal<ushort>([1, 2], group.DoodadReferences);
        Assert.Null(group.Liquid);
        Assert.Equal(new Vector3(0, 0, 0), group.VertexBoundsMin);
        Assert.Equal(new Vector3(1, 1, 2), group.VertexBoundsMax);
    }

    [Fact]
    public void Group_ParsesLiquid()
    {
        SyntheticWmoGroup synthetic = SampleGroup();
        synthetic.LiquidHeights = [10f, 11f, 12f, 13f];

        WmoLiquidGrid liquid = Assert.IsType<WmoLiquidGrid>(WmoGroupFile.Parse(ModelFixtures.WmoGroup(synthetic)).Liquid);

        Assert.Equal<int>([2, 2, 1, 1], [liquid.VerticesX, liquid.VerticesY, liquid.TilesX, liquid.TilesY]);
        Assert.Equal(new Vector3(1, 2, 3), liquid.Corner);
        Assert.Equal((ushort)5, liquid.MaterialId);
        Assert.Equal<float>([10f, 11f, 12f, 13f], liquid.Heights);
        Assert.Equal<byte>([0x0F], liquid.TileFlags);
    }

    [Fact]
    public void Group_ReadsSubChunksToTheEndOfFile_EvenWhenMogpUnderstatesItsSize()
    {
        // The extractor forces MOGP's size to 68 and reads the sub-chunks as top-level chunks.
        SyntheticWmoGroup synthetic = SampleGroup();
        synthetic.MogpSizeError = -100;

        WmoGroupFile group = WmoGroupFile.Parse(ModelFixtures.WmoGroup(synthetic));

        Assert.Equal(2, group.TriangleCount);
        Assert.Equal(4, group.Vertices.Length);
    }

    [Fact]
    public void Group_ToleratesATrailingOptionalChunkCutOffByTheFile()
    {
        // Undercity_144.wmo in the real client: its last chunk (MOCV) claims one byte more than the file holds.
        byte[] data = ModelFixtures.Concat(ModelFixtures.WmoGroup(SampleGroup()), ModelFixtures.Chunk("MOCV", new byte[20]));

        WmoGroupFile group = WmoGroupFile.Parse(data[..^1]);

        Assert.Equal(2, group.TriangleCount);
    }

    [Fact]
    public void Group_RefusesInconsistentOrTruncatedData()
    {
        SyntheticWmoGroup shortMovi = SampleGroup();
        shortMovi.MoviIndexCount = 4;
        SyntheticWmoGroup badIndex = SampleGroup();
        badIndex.Triangles[0] = new SyntheticTriangle(0, 1, 9);
        byte[] good = ModelFixtures.WmoGroup(SampleGroup());

        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse([]));
        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse(ModelFixtures.Chunk("MVER", [17, 0, 0, 0])));
        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse(ModelFixtures.WmoGroup(shortMovi)));
        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse(ModelFixtures.WmoGroup(badIndex)));
        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse(good[..60]));
        SyntheticWmoGroup noDoodads = SampleGroup();
        noDoodads.DoodadReferences.Clear();
        byte[] cutInVertices = ModelFixtures.WmoGroup(noDoodads);
        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse(cutInVertices[..^10]));
    }

    [Theory]
    [InlineData(0x08, true)]
    [InlineData(0x20, true)]
    [InlineData(0x24, false)]
    [InlineData(0x28, true)]
    [InlineData(0x04, false)]
    [InlineData(0x60, true)]
    [InlineData(0x00, false)]
    [InlineData(0x40, false)]
    public void MaterialFlags_FollowTheExtractorsCollisionRule(int flags, bool collidable)
    {
        Assert.Equal(collidable, WmoMaterialFlags.IsCollidable((byte)flags));
    }

    [Fact]
    public void Model_AppliesTheExtractorsGroupSkipRule()
    {
        var root = new SyntheticWmoRoot { GroupCount = 4 };
        root.GroupNames.AddRange(["ok", "antiportal", "dead", "wall"]);
        var files = new ArcaneCore.Atlas.Core.MemoryClientFiles();
        files.Add("W\\B.wmo", ModelFixtures.WmoRoot(root));
        files.Add("W\\B_000.wmo", ModelFixtures.WmoGroup(GroupWith(0, 0)));
        files.Add("W\\B_001.wmo", ModelFixtures.WmoGroup(GroupWith(0, 3)));
        files.Add("W\\B_002.wmo", ModelFixtures.WmoGroup(GroupWith(0x80, 14)));
        files.Add("W\\B_003.wmo", ModelFixtures.WmoGroup(GroupWith(0x4000000, 19)));

        ModelLoad<WmoModel> load = new ClientModelCache().GetWmo(files, "W\\B.wmo");

        WmoModel model = Assert.IsType<WmoModel>(load.Model);
        Assert.Null(model.SkipReason(0));
        Assert.Equal("antiportal-name", model.SkipReason(1));
        Assert.Equal("unreachable", model.SkipReason(2));
        Assert.Equal("antiportal", model.SkipReason(3));
        Assert.Equal(model.Groups[0].TriangleCount, model.ExtractorTriangleCount);
    }

    private static SyntheticWmoGroup GroupWith(uint flags, uint nameOffset)
    {
        SyntheticWmoGroup group = ModelFixtures.WallGroup(0, 0x20, flags);
        group.NameOffset = nameOffset;
        return group;
    }
}

public sealed class M2ReaderTests
{
    [Fact]
    public void Parse_ReadsVerticesViewAndBoundingHull()
    {
        byte[] data = ModelFixtures.CubeM2(2f);

        M2ModelFile model = M2ModelFile.Parse(data);

        Assert.Equal("synthetic", model.Name);
        Assert.Equal(8, model.Vertices.Length);
        Assert.Equal(new Vector3(-2, -2, -2), model.Vertices[0]);
        Assert.Equal(1, model.ViewCount);
        Assert.Equal(12, model.RenderTriangleCount);
        Assert.Equal(12, model.BoundingTriangleCount);
        Assert.Equal(8, model.BoundingVertices.Length);
        Assert.True(model.HasCollisionHull);
        Assert.Equal((new Vector3(-2, -2, -2), new Vector3(2, 2, 2)), model.BoundingBox);
    }

    [Fact]
    public void Parse_ViewTrianglesGoThroughTheVertexLookup()
    {
        Vector3[] vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(5, 5, 5)];
        byte[] data = ModelFixtures.M2(vertices, [3, 1, 2], [0, 1, 2], [], []);

        M2ModelFile model = M2ModelFile.Parse(data);

        Assert.Equal<int>([3, 1, 2], model.RenderIndices);
        Assert.False(model.HasCollisionHull);
    }

    [Fact]
    public void Parse_HullNeedsBothTrianglesAndVertices()
    {
        Vector3[] vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        byte[] indicesOnly = ModelFixtures.M2(vertices, [0, 1, 2], [0, 1, 2], [], [0, 1, 2]);
        byte[] verticesOnly = ModelFixtures.M2(vertices, [0, 1, 2], [0, 1, 2], vertices, []);

        Assert.False(M2ModelFile.Parse(indicesOnly).HasCollisionHull);
        Assert.False(M2ModelFile.Parse(verticesOnly).HasCollisionHull);
    }

    [Fact]
    public void Parse_RefusesWhatItCannotTrust()
    {
        byte[] good = ModelFixtures.CubeM2();
        byte[] wrongMagic = [.. good];
        wrongMagic[0] = (byte)'X';
        byte[] outOfRangeHull = ModelFixtures.M2([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)], [0, 1, 2], [0, 1, 2], [new Vector3(0, 0, 0)], [0, 0, 7]);
        byte[] badLookup = ModelFixtures.M2([new Vector3(0, 0, 0)], [0, 9], [0, 1, 1], [], []);
        byte[] oddTriangles = ModelFixtures.M2([new Vector3(0, 0, 0)], [0, 0], [0, 1], [], []);

        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse([]));
        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse(good[..100]));
        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse(wrongMagic));
        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse(ModelFixtures.M2([], [], [], [], [], version: 264)));
        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse(outOfRangeHull));
        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse(badLookup));
        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse(oddTriangles));
        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse(good[..^20]));
    }
}

public sealed class ClientModelNamesTests
{
    [Theory]
    [InlineData("World\\Azeroth\\Elwynn\\Tree.mdx", "World\\Azeroth\\Elwynn\\Tree.m2")]
    [InlineData("World/Azeroth/Elwynn/Tree.MDX", "World\\Azeroth\\Elwynn\\Tree.M2")]
    [InlineData("World\\Foo\\Bar.mdl", "World\\Foo\\Bar.m2")]
    [InlineData("World\\Foo\\Bar.m2", "World\\Foo\\Bar.m2")]
    [InlineData("\\World\\wmo\\X.wmo", "World\\wmo\\X.wmo")]
    public void ClientFilePath_MapsMdxToM2(string stored, string expected)
    {
        Assert.Equal(expected, ClientModelNames.ClientFilePath(stored));
    }

    [Theory]
    [InlineData("World\\wmo\\Azeroth\\Buildings\\GoldshireInn\\GoldshireInn.wmo", "Goldshireinn.wmo")]
    [InlineData("World\\Generic\\NightElf\\Passive Doodads\\Beds\\ElfBed01.mdx", "Elfbed01.m2")]
    [InlineData("World\\Khaz\\IronForgeBanner_Ornate01.m2", "Ironforgebanner_Ornate01.m2")]
    [InlineData("World\\X\\Some Thing 02.mdx", "Some_Thing_02.m2")]
    [InlineData("World\\X\\AB.m2", "Ab.m2")]
    public void ServerModelName_MatchesTheNamesInTheVmapDirectory(string stored, string expected)
    {
        Assert.Equal(expected, ClientModelNames.ServerModelName(stored));
    }

    [Fact]
    public void KindHelpers_RecogniseExtensions()
    {
        Assert.True(ClientModelNames.IsWmo("a\\b.WMO"));
        Assert.False(ClientModelNames.IsWmo("a\\b.m2"));
        Assert.True(ClientModelNames.IsModel("a\\b.MDX"));
        Assert.True(ClientModelNames.IsModel("a\\b.m2"));
        Assert.False(ClientModelNames.IsModel("a\\b.wmo"));
        Assert.Equal("b.m2", ClientModelNames.PlainName("a\\b.m2"));
        Assert.Equal("b.m2", ClientModelNames.PlainName("b.m2"));
    }
}
