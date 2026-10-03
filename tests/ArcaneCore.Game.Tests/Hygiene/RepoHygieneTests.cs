using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;

namespace ArcaneCore.Game.Tests.Hygiene;

/// <summary>
/// Guards the repository against proprietary client data (DBC/MAPS/VMAP/MMAP) and GPL-derived
/// world database dumps. The rule logic is exercised on planted files in temp directories
/// (never in the repo); the repository test then scans the real tree.
/// </summary>
public sealed class RepoHygieneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanecore-hygiene-" + Guid.NewGuid().ToString("N"));

    public RepoHygieneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort temp cleanup */ }
    }

    private string Plant(string relative, byte[] content)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return relative;
    }

    private static byte[] Header(byte[] magic, int total = 64)
    {
        byte[] bytes = new byte[total];
        magic.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] LeU32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private IReadOnlyList<HygieneViolation> ScanOne(string relative) => RepoHygieneScanner.Scan(_root, [relative]);

    [Theory]
    [InlineData("data/Spell.dbc")]
    [InlineData("src/x/Map000.map")]
    [InlineData("a/b/001.vmtree")]
    [InlineData("a/b/001_02_03.vmtile")]
    [InlineData("a/b/model.vmo")]
    [InlineData("a/b/0010101.mmtile")]
    [InlineData("a/common.MPQ")]
    public void ForbiddenExtension_IsRefused(string path)
    {
        Plant(path, [1, 2, 3]);
        Assert.Contains(ScanOne(path), v => v.Path == path);
    }

    [Fact]
    public void WdbcHeader_IsRefusedUnderAnyName() => AssertMagicRefused(Encoding.ASCII.GetBytes("WDBC"));

    [Fact]
    public void MapsHeader_ProductionMagic_IsRefusedUnderAnyName()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("MAPS"), LeU32(TerrainTile.MapMagic));
        AssertMagicRefused(LeU32(TerrainTile.MapMagic));
    }

    [Fact]
    public void VMapHeader_IsRefusedUnderAnyName() => AssertMagicRefused(Encoding.ASCII.GetBytes(VMapFormat.Magic));

    [Fact]
    public void MMapHeader_ProductionMagic_IsRefusedUnderAnyName() => AssertMagicRefused(LeU32(NavMeshFormat.MmapMagic));

    private void AssertMagicRefused(byte[] magic)
    {
        string path = Plant("misc/innocent.bin", Header(magic));
        Assert.Contains(ScanOne(path), v => v.Path == path);
    }

    [Fact]
    public void LargeSqlDump_IsRefused()
    {
        string path = Plant("docs/world.sql", new byte[200 * 1024]);
        Assert.Contains(ScanOne(path), v => v.Path == path);
    }

    [Fact]
    public void SmallSqlWithDumpBanner_IsRefused()
    {
        string path = Plant("docs/tiny.sql", Encoding.ASCII.GetBytes("-- MySQL dump 10.13  Distrib 5.7\nCREATE TABLE t (a int);\n"));
        Assert.Contains(ScanOne(path), v => v.Path == path);
    }

    [Fact]
    public void LargeSqlUnderBaselines_IsAllowed()
    {
        string path = Plant("tests/ArcaneCore.Data.Tests/Baselines/candidate.ddl.sql", Encoding.ASCII.GetBytes(new string('x', 200 * 1024)));
        Assert.Empty(ScanOne(path));
    }

    [Fact]
    public void LargeSqlInBaselinesLookalikeDirectory_IsRefused()
    {
        string path = Plant("src/Baselines/world.sql", new byte[200 * 1024]);
        Assert.Contains(ScanOne(path), v => v.Path == path);
    }

    [Fact]
    public void BinaryMagicUnderBaselines_IsStillRefused()
    {
        string path = Plant("tests/ArcaneCore.Data.Tests/Baselines/x.sql", Header(Encoding.ASCII.GetBytes("WDBC")));
        Assert.Contains(ScanOne(path), v => v.Path == path);
    }

    [Theory]
    [InlineData("data/anything.txt")]
    [InlineData("local-data/maps/readme.txt")]
    public void LocalDataDirectories_AreRefused(string path)
    {
        Plant(path, Encoding.ASCII.GetBytes("hello"));
        Assert.Contains(ScanOne(path), v => v.Path == path);
    }

    [Fact]
    public void OrdinarySourceAndSmallSql_AreAllowed()
    {
        string cs = Plant("src/A.cs", Encoding.ASCII.GetBytes("class A {}"));
        string sql = Plant("docs/note.sql", Encoding.ASCII.GetBytes("SELECT 1;"));
        string nested = Plant("src/data/Notes.txt", Encoding.ASCII.GetBytes("a nested data directory is fine"));
        Assert.Empty(RepoHygieneScanner.Scan(_root, [cs, sql, nested]));
    }

    [Fact]
    public void GitIgnore_CoversLocalDataAndClientBinaries()
    {
        string root = RepoHygieneScanner.FindRepoRoot();
        string[] lines = File.ReadAllLines(Path.Combine(root, ".gitignore")).Select(l => l.Trim()).ToArray();
        foreach (string required in new[] { "/data/", "/local-data/", "*.dbc", "*.mpq", "*.map", "*.vmtree", "*.vmtile", "*.vmo", "*.mmtile" })
        {
            Assert.Contains(required, lines);
        }
    }

    [Fact]
    public void Repository_ContainsNoProprietaryOrGplDerivedData()
    {
        string root = RepoHygieneScanner.FindRepoRoot();
        IReadOnlyList<string> files = RepoHygieneScanner.EnumerateRepoFiles(root);

        // A scan that enumerates nothing would pass vacuously.
        Assert.Contains("ArcaneCore.slnx", files);
        Assert.True(files.Count > 100, $"hygiene scan saw only {files.Count} files; enumeration is broken");

        IReadOnlyList<HygieneViolation> violations = RepoHygieneScanner.Scan(root, files);
        Assert.True(violations.Count == 0, "Proprietary or GPL-derived data in the repo:\n" + string.Join("\n", violations.Select(v => $"  {v.Path}: {v.Rule}")));
    }
}
