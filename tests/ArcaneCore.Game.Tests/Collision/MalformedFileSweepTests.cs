using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Sweep for AC-PI-001's bug class: every collision/terrain file reader must turn ANY corrupt
/// input into <see cref="InvalidDataException"/> (the only type its callers catch). Each reader is
/// fed a valid file with every byte, and every 4-byte window, overwritten by extreme values, and
/// every truncation; any other exception type (overflow, out-of-range, OOM...) fails the test.
/// </summary>
public sealed class MalformedFileSweepTests
{
    private static readonly byte[] ByteValues = [0x00, 0x01, 0x7F, 0x80, 0xFF];

    private static readonly uint[] WordValues = [0u, 0x7FFFFFFFu, 0x80000000u, 0x40000000u, 0xFFFFFFFFu, 0x20000000u];

    private static List<string> Probe(byte[] valid, Action<byte[]> parse, Func<int, bool>? include = null)
    {
        parse(valid); // the unmutated file must parse
        var failures = new List<string>();

        void Try(byte[] data, string what)
        {
            try
            {
                parse(data);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                failures.Add($"{what}: {ex.GetType().Name}");
            }
        }

        for (int i = 0; i < valid.Length; i++)
        {
            Try(valid[..i], $"truncated to {i}");
            if (include is not null && !include(i))
            {
                continue;
            }

            foreach (byte b in ByteValues)
            {
                byte[] copy = (byte[])valid.Clone();
                copy[i] = b;
                Try(copy, $"byte {i} = 0x{b:x2}");
            }

            if (i + 4 <= valid.Length)
            {
                foreach (uint w in WordValues)
                {
                    byte[] copy = (byte[])valid.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(i), w);
                    Try(copy, $"u32 at {i} = 0x{w:x8}");
                }
            }
        }

        return failures;
    }

    private static string Report(List<string> failures)
        => failures.Count == 0 ? string.Empty : $"{failures.Count} escapes, first: {string.Join("; ", failures.Distinct().Take(8))}";

    [Fact]
    public void VmoModel_OnlyThrowsInvalidData()
    {
        var liquid = new WmoLiquid(1, 1, Vector3.Zero, 4, [1, 2, 3, 4], [0]);
        WorldModel box = VMapFixture.Box(Vector3.Zero, Vector3.One);
        var group = new GroupModel(Vector3.Zero, Vector3.One, 0, 1, [.. box.Groups[0].Vertices], [.. box.Groups[0].Triangles], liquid: liquid);
        byte[] bytes = new WorldModel(9, [group]).ToBytes();

        Assert.Equal(string.Empty, Report(Probe(bytes, b => WorldModel.Parse(b))));
    }

    [Fact]
    public void VmapTreeAndTile_OnlyThrowInvalidData()
    {
        using var fixture = new VMapFixture();
        WorldModel box = VMapFixture.Box(new Vector3(-1, -1, 0), new Vector3(1, 1, 2));
        fixture.Place("Box", box, new Vector3(100, 100, 0));
        fixture.Place("Box2", box, new Vector3(110, 100, 0));
        fixture.Write(0, tiled: false);
        byte[] globalTree = File.ReadAllBytes(Path.Combine(fixture.Directory, VMapFormat.TreeFileName(0)));
        Assert.Equal(string.Empty, Report(Probe(globalTree, b => VMapTree.Parse(0, b))));

        using var tiled = new VMapFixture();
        tiled.Place("Box", box, new Vector3(100, 100, 0));
        tiled.Write(0, tiled: true);
        string tileFile = Directory.GetFiles(tiled.Directory, "*.vmtile").Single();
        Assert.Equal(string.Empty, Report(Probe(File.ReadAllBytes(tileFile), b => VMapTree.ParseTile(b))));
        Assert.Equal(string.Empty, Report(Probe(File.ReadAllBytes(Path.Combine(tiled.Directory, VMapFormat.TreeFileName(0))), b => VMapTree.Parse(0, b))));
    }

    [Fact]
    public void MmapTile_OnlyThrowsInvalidData()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteTile(0, 31, 31, new CellTile(0, 0, 0, 0, 3, 3));
        byte[] bytes = File.ReadAllBytes(fixture.PathOf(NavMeshFormat.TileFileName(0, 31, 31)));

        Assert.Equal(string.Empty, Report(Probe(bytes, b => NavMeshTile.ParseFile(b))));

        fixture.WriteParams(0);
        byte[] meshParams = File.ReadAllBytes(fixture.PathOf(NavMeshFormat.ParamsFileName(0)));
        Assert.Equal(string.Empty, Report(Probe(meshParams, b => NavMeshParams.Parse(b))));
    }

    [Theory]
    [InlineData(8)]  // area section offset
    [InlineData(16)] // height section offset
    [InlineData(24)] // liquid section offset
    [InlineData(32)] // holes section offset
    public void TerrainMapFile_SectionOffsetBeyondIntRange_IsInvalidData(int headerField)
    {
        byte[] bytes = new MapFileBuilder { GridHeight = 5, HasLiquid = true, LiquidFlags = new byte[256], LiquidHeights = new float[16], LiquidWidth = 4, LiquidHeight = 4 }.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerField), 0xFFFFFFF0u);

        Assert.Throws<InvalidDataException>(() => TerrainTile.Parse(bytes));
    }

    [Fact]
    public void TerrainMapFile_OnlyThrowsInvalidData()
    {
        byte[] bytes = new MapFileBuilder
        {
            GridHeight = 5,
            AreaFlags = new ushort[256],
            HasLiquid = true,
            LiquidFlags = new byte[256],
            LiquidWidth = 4,
            LiquidHeight = 4,
            LiquidHeights = new float[16],
        }.Build();

        Assert.Equal(string.Empty, Report(Probe(bytes, b => TerrainTile.Parse(b))));
    }
}
