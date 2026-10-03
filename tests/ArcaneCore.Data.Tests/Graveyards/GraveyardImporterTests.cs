using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Graveyards;

/// <summary>
/// <c>world_safe_locs</c> and <c>game_graveyard_zone</c> import (vmangos ObjectMgr::LoadGraveyardZones,
/// ObjectMgr.cpp:7453-7510, and LoadWorldSafeLocsFacing, ObjectMgr.cpp:7669-7704). The dumps are hand-written; nothing
/// depends on D:\refs (the real classic-db z2815 has 122 safe locations and 191 links, checked by hand when this was written).
/// </summary>
public sealed class GraveyardImporterTests
{
    // cmangos classic-db: `o` is the facing, links have ghost_loc and link_kind.
    private const string CMangos = """
        CREATE TABLE `world_safe_locs` (`id` int unsigned NOT NULL, `map` int unsigned NOT NULL DEFAULT '0', `x` float NOT NULL DEFAULT '0', `y` float NOT NULL DEFAULT '0', `z` float NOT NULL DEFAULT '0', `o` float NOT NULL DEFAULT '0', `name` varchar(50) NOT NULL DEFAULT '', PRIMARY KEY (`id`));
        INSERT INTO `world_safe_locs` VALUES (1,0,-9100.5,400.25,93.5,3.14,'Elwynn Graveyard'),(2,1,10.5,20.5,30.5,0,'Durotar Graveyard'),(3,0,1,2,3,0.5,'AAAAAAAAAABBBBBBBBBBCCCCCCCCCCDDDDDDDDDDEEEEEEEEEEFFFFFFFFFF');
        CREATE TABLE `game_graveyard_zone` (`id` int unsigned NOT NULL, `ghost_loc` int unsigned NOT NULL, `link_kind` tinyint unsigned NOT NULL DEFAULT '0', `faction` int unsigned NOT NULL DEFAULT '0', PRIMARY KEY (`id`,`ghost_loc`,`link_kind`));
        INSERT INTO `game_graveyard_zone` VALUES (1,12,0,469),(2,14,0,0),(2,14,1,0),(1,40,0,12345),(1,12,0,67),(2,17,0,67);
        """;

    // vmangos: ghost_zone, patch_min/patch_max rows, no safe locations and a separate facing table.
    private const string VMangos = """
        CREATE TABLE `game_graveyard_zone` (`id` int unsigned NOT NULL, `ghost_zone` int unsigned NOT NULL, `faction` int unsigned NOT NULL DEFAULT '0', `patch_min` tinyint unsigned NOT NULL DEFAULT '0', `patch_max` tinyint unsigned NOT NULL DEFAULT '10', PRIMARY KEY (`id`,`ghost_zone`,`patch_min`));
        INSERT INTO `game_graveyard_zone` VALUES (1,12,469,0,10),(1,13,469,11,255),(2,12,0,0,5),(3,14,67,6,10);
        CREATE TABLE `world_safe_locs_facing` (`id` int unsigned NOT NULL, `orientation` float NOT NULL DEFAULT '0', PRIMARY KEY (`id`));
        INSERT INTO `world_safe_locs_facing` VALUES (3,3.83972),(9,1);
        """;

    [Fact]
    public void ClassicDb_RowsMapByName_TheFacingIsO()
    {
        GraveyardDumpImporter importer = Import(CMangos);

        WorldSafeLocRow elwynn = importer.Snapshot().SafeLocs.Single(l => l.Id == 1);

        Assert.Equal((0u, -9100.5f, 400.25f, 93.5f, 3.14f, "Elwynn Graveyard"), (elwynn.MapId, elwynn.X, elwynn.Y, elwynn.Z, elwynn.Orientation, elwynn.Name));
        Assert.Equal(0f, importer.Snapshot().SafeLocs.Single(l => l.Id == 2).Orientation);
    }

    [Fact]
    public void ClassicDb_LinkRules_KindTeamAndDuplicate()
    {
        GraveyardDumpImporter importer = Import(CMangos);

        (uint, uint, uint)[] links = [.. importer.Snapshot().Links.Select(l => (l.Id, l.GhostZone, l.Team))];

        // Kept: (1,12,469), (2,14,0), (2,17,67). Skipped: link_kind 1, faction 12345 (not 0/67/469), the second (1,12).
        Assert.Equal([(1u, 12u, 469u), (2u, 14u, 0u), (2u, 17u, 67u)], links);
        GraveyardImportReport report = importer.BuildReport();
        Assert.Equal((3, 3, 3), (report.SafeLocs, report.Links, report.SkippedLinks));
        Assert.Contains(report.Warnings, w => w.Contains("link_kind", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("faction other than", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("first one is kept", StringComparison.Ordinal));
    }

    [Fact]
    public void ADuplicateLink_KeepsTheFirstRow_LikeVmangosAddGraveYardLink()
    {
        GraveyardDumpImporter importer = Import(CMangos);

        Assert.Equal(469u, importer.Snapshot().Links.Single(l => l.Id == 1 && l.GhostZone == 12).Team);
    }

    [Fact]
    public void ANameOverFiftyCharacters_IsCutAndReported()
    {
        GraveyardDumpImporter importer = Import(CMangos);

        string name = importer.Snapshot().SafeLocs.Single(l => l.Id == 3).Name;

        // The fixture's 60-character name does not fit a varchar(50) on MariaDB or PostgreSQL.
        Assert.Equal(GraveyardDataModule.NameLength, name.Length);
        Assert.Contains(importer.BuildReport().Warnings, w => w.Contains("cut to 50", StringComparison.Ordinal));
    }

    [Fact]
    public void VMangos_PatchRangeMustCoverPatchTen()
    {
        GraveyardDumpImporter importer = Import(VMangos);

        // (1,13): 11..255 and (2,12): 0..5 do not cover patch 10; (3,14): 6..10 does.
        Assert.Equal([(1u, 12u, 469u), (3u, 14u, 67u)], importer.Snapshot().Links.Select(l => (l.Id, l.GhostZone, l.Team)));
        Assert.Equal(2, importer.BuildReport().SkippedLinks);
    }

    [Fact]
    public void VMangos_SafeLocationsComeFromTheDbc_AndTheFacingFromTheFacingTable()
    {
        GraveyardDumpImporter importer = Import(VMangos);
        importer.ReadSafeLocs(WorldSafeLocsDbcReader.Read(DbcFile.Parse(SafeLocsDbc((1, 0, 1f, 2f, 3f, "One"), (3, 1, 4f, 5f, 6f, "Three")))));

        WorldSafeLocRow[] locs = [.. importer.Snapshot().SafeLocs];

        Assert.Equal(2, locs.Length);
        Assert.Equal((0f, "One"), (locs[0].Orientation, locs[0].Name)); // no facing row: 0 (GetWorldSafeLocFacing)
        Assert.Equal((1u, 4f, 5f, 6f, "Three"), (locs[1].MapId, locs[1].X, locs[1].Y, locs[1].Z, locs[1].Name));
        Assert.Equal(3.83972f, locs[1].Orientation);                    // facing row 3
    }

    [Fact]
    public void TheDbcReader_RefusesAnotherLayout_AndADuplicateId()
    {
        byte[] narrow = Dbc(fields: 3, [[1, 2, 3]], []);
        Assert.Throws<InvalidDataException>(() => WorldSafeLocsDbcReader.Read(DbcFile.Parse(narrow)));
        Assert.Throws<InvalidDataException>(() => WorldSafeLocsDbcReader.Read(DbcFile.Parse(SafeLocsDbc((1, 0, 1f, 1f, 1f, "A"), (1, 0, 2f, 2f, 2f, "B")))));
    }

    [Fact]
    public void ARenamedKeyColumn_IsASchemaError()
    {
        var importer = new GraveyardDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `game_graveyard_zone` (`id`,`zone`,`faction`) VALUES (1,12,0);")));

        Assert.Equal("game_graveyard_zone", ex.Table);
        Assert.Equal("ghost_zone", ex.Column);
    }

    [Fact]
    public void AMissingCoordinateColumn_IsASchemaError_NotAZero()
    {
        var importer = new GraveyardDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `world_safe_locs` (`id`,`map`,`x`,`y`) VALUES (1,0,1,2);")));

        Assert.Equal("z", ex.Column);
    }

    [Fact]
    public void TheTableSpecs_ResolveTheKeyOfBothDialects_AndTellThemApart()
    {
        TableSpec spec = ContentTableSpecs.Find("game_graveyard_zone")!;

        Assert.Equal([0, 2], spec.ResolveKey(["id", "x", "ghost_loc", "link_kind", "faction"]));
        Assert.Equal([0, 1], spec.ResolveKey(["id", "ghost_zone", "faction", "patch_min", "patch_max"]));
        var cmangos = new HashSet<string>(["id", "ghost_loc", "link_kind", "faction"], StringComparer.OrdinalIgnoreCase);
        var vmangos = new HashSet<string>(["id", "ghost_zone", "faction", "patch_min", "patch_max"], StringComparer.OrdinalIgnoreCase);
        Assert.Contains(spec.Signatures, s => s.Dialect == ContentDialect.CMangos && s.Matches(cmangos) && !s.Matches(vmangos));
        Assert.Contains(spec.Signatures, s => s.Dialect == ContentDialect.VMangos && s.Matches(vmangos) && !s.Matches(cmangos));
        Assert.True(ContentTableSpecs.Find("world_safe_locs")!.IsMapped("o"));
        Assert.False(ContentTableSpecs.Find("world_safe_locs")!.IsMapped("script_name"));
        Assert.True(ContentTableSpecs.Find("world_safe_locs_facing")!.IsMapped("orientation"));
    }

    private static GraveyardDumpImporter Import(string dump)
    {
        var importer = new GraveyardDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    /// <summary>A 14-field WorldSafeLocs.dbc (vmangos DBCfmt.h:90): id, map, x, y, z, enUS name, the other seven names and the flags empty.</summary>
    private static byte[] SafeLocsDbc(params (uint Id, uint Map, float X, float Y, float Z, string Name)[] rows)
    {
        var strings = new List<byte> { 0 };
        var records = new List<uint[]>();
        foreach ((uint id, uint map, float x, float y, float z, string name) in rows)
        {
            uint offset = (uint)strings.Count;
            strings.AddRange(Encoding.UTF8.GetBytes(name));
            strings.Add(0);
            var record = new uint[14];
            record[0] = id;
            record[1] = map;
            record[2] = BitConverter.SingleToUInt32Bits(x);
            record[3] = BitConverter.SingleToUInt32Bits(y);
            record[4] = BitConverter.SingleToUInt32Bits(z);
            record[5] = offset;
            records.Add(record);
        }

        return Dbc(14, records, strings);
    }

    private static byte[] Dbc(int fields, IEnumerable<uint[]> records, IEnumerable<byte> strings)
    {
        uint[][] rows = [.. records];
        byte[] block = [.. strings];
        var data = new byte[20 + (rows.Length * fields * 4) + block.Length];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)(fields * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)block.Length);
        int offset = 20;
        foreach (uint[] row in rows)
        {
            foreach (uint value in row)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
                offset += 4;
            }
        }

        block.CopyTo(data, offset);
        return data;
    }
}
