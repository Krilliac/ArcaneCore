using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;

namespace ArcaneCore.Atlas.Tests.Web;

/// <summary>
/// Writes the synthetic "client folder" the browser-side client-file reader (<c>Web/js/clientfs</c>) is tested against:
/// MPQ archives built by <see cref="MpqTestWriter"/>, DBC tables, ADT tiles from <see cref="SyntheticTerrain"/>, PE files
/// carrying a version resource, malformed archives and ADTs, and the exact ATG1 bytes the C# core produces for each tile.
/// Everything is generated at test time into a temp directory (nothing is committed, no client byte is involved), and
/// every expected value is either computed here from the source data or produced by the C# reference implementation.
/// </summary>
internal static class ClientFsFixtures
{
    /// <summary>The environment variable the JS tests read for the fixture directory.</summary>
    public const string Variable = "ARCANECORE_ATLAS_CLIENTFS";

    /// <summary>The environment variable the real-data JS tests read for the sampled-file manifest directory.</summary>
    public const string RealVariable = "ARCANECORE_ATLAS_CLIENTFS_REAL";

    /// <summary>A text canary placed in an MPQ member name.</summary>
    public const string CanaryMpqName = "CANARY_MPQ_NAME_4b2e91";

    /// <summary>A text canary placed in a DBC string block.</summary>
    public const string CanaryDbcString = "CANARY_DBC_STRING_7f3a91c2";

    /// <summary>A text canary placed in the MTEX / MMDX names of the ADT tiles.</summary>
    public const string CanaryAdtString = "CANARY_ADT_PAYLOAD_5d0c17ab";

    /// <summary>A text canary placed in <c>WTF/Config.wtf</c>.</summary>
    public const string CanaryConfig = "CANARY_CONFIG_WTF_c3e8d2f0";

    /// <summary>A fake account folder name under <c>WTF/Account/</c>.</summary>
    public const string CanaryAccount = "CANARYACC";

    /// <summary>A binary canary placed in a BLP payload (not text, so only a byte-level match finds it).</summary>
    public static readonly byte[] CanaryBlpBytes = [0xC0, 0xFF, 0xEE, 0x15, 0xBA, 0xD5, 0xEE, 0xD1, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x71];

    /// <summary>Tiles written as ADTs, all of map 0 ("Azeroth"), each with holes and liquid.</summary>
    public static readonly TileKey[] LiquidTiles = [new(0, 33, 31), new(0, 33, 32), new(0, 34, 31)];

    /// <summary>A tile with holes but no visible liquid.</summary>
    public static readonly TileKey DryTile = new(0, 35, 31);

    /// <summary>A tile the WDT does not list (so no ADT exists for it).</summary>
    public static readonly TileKey AbsentTile = new(0, 36, 31);

    /// <summary>The area id that AreaTable.dbc names.</summary>
    public const uint NamedArea = 12;

    /// <summary>The area name AreaTable.dbc gives <see cref="NamedArea"/>.</summary>
    public const string NamedAreaName = "Elwynn Forest";

    private const string MapDirectoryName = "Azeroth";

    /// <summary>Writes everything into <paramref name="directory"/> and returns the manifest (also written as manifest.json).</summary>
    public static JsonObject Write(string directory)
    {
        foreach (string sub in new[] { "archives", "exe", "wtf", "adt", "expected", "malformed", "pkware" })
        {
            Directory.CreateDirectory(Path.Combine(directory, sub));
        }

        var manifest = new JsonObject
        {
            ["layers"] = new JsonObject { ["terrain"] = LayerIds.RefTerrain, ["liquid"] = LayerIds.RefLiquid },
            ["mapDirectory"] = MapDirectoryName,
        };

        // Terrain: build every ADT, its expected ATG1 bytes from the C# core, and the WDT that lists them.
        var tiles = new JsonArray();
        var adtFiles = new List<TestMpqFile>();
        var spec = TileSpecs();
        var directoryLookup = new Func<uint, string?>(id => id == NamedArea ? NamedAreaName : null);
        int index = 0;
        foreach (TileKey tile in spec.Keys)
        {
            BuiltAdt built = SyntheticTerrain.Adt(spec[tile]);
            AdtFile parsed = AdtFile.Parse(built.Bytes);
            GeometryChunk terrain = ClientTerrainGeometry.BuildTerrain(tile, parsed, directoryLookup, CancellationToken.None);
            GeometryChunk? liquid = ClientTerrainGeometry.BuildLiquid(tile, parsed, CancellationToken.None);
            string name = $"{tile.MapId}-{tile.X}-{tile.Y}";
            File.WriteAllBytes(Path.Combine(directory, "adt", name + ".adt"), built.Bytes);
            File.WriteAllBytes(Path.Combine(directory, "expected", "terrain-" + name + ".bin"), GeometryWire.Write(terrain));
            if (liquid is not null)
            {
                File.WriteAllBytes(Path.Combine(directory, "expected", "liquid-" + name + ".bin"), GeometryWire.Write(liquid));
            }

            tiles.Add(new JsonObject
            {
                ["map"] = tile.MapId,
                ["x"] = tile.X,
                ["y"] = tile.Y,
                ["adt"] = "adt/" + name + ".adt",
                ["terrain"] = "expected/terrain-" + name + ".bin",
                ["liquid"] = liquid is null ? null : "expected/liquid-" + name + ".bin",
                ["terrainVertices"] = terrain.VertexCount,
                ["terrainTriangles"] = terrain.TriangleCount,
                ["liquidTriangles"] = liquid?.TriangleCount ?? 0,
                ["holeChunks"] = spec[tile].Holes.Count,
            });

            // Four different storage forms across the tiles: zlib multi-sector, encrypted + key-adjusted, stored, single unit.
            adtFiles.Add(new TestMpqFile(ClientMapPaths.AdtPath(MapDirectoryName, tile), built.Bytes)
            {
                Zlib = index % 4 is 0 or 3,
                Encrypted = index % 4 == 1,
                FixKey = index % 4 == 1,
                SingleUnit = index % 4 == 3,
            });
            index++;
        }

        manifest["tiles"] = tiles;
        manifest["absentTile"] = new JsonObject { ["map"] = AbsentTile.MapId, ["x"] = AbsentTile.X, ["y"] = AbsentTile.Y };

        byte[] wdt = SyntheticTerrain.Wdt([.. spec.Keys.Select(t => (t.X, t.Y))]);
        adtFiles.Add(new TestMpqFile(ClientMapPaths.WdtPath(MapDirectoryName), wdt) { Zlib = true });

        // Archives. Names are the 1.12.1 set; patch.MPQ and patch-2.MPQ override and delete.
        byte[] cdi = CreatureDisplayInfo(12, out JsonObject cdiExpect);
        byte[] mapDbc = MapTable(42, out JsonObject mapExpect);
        byte[] areaDbc = AreaTable(25, out JsonObject areaExpect);
        manifest["dbc"] = new JsonObject { ["creatureDisplayInfo"] = cdiExpect, ["map"] = mapExpect, ["area"] = areaExpect };

        var archives = new JsonObject();
        void AddArchive(string name, IReadOnlyList<TestMpqFile> files, TestMpqOptions? options = null)
        {
            byte[] bytes = MpqTestWriter.Build(files, options ?? new TestMpqOptions { SectorShift = 3 });
            File.WriteAllBytes(Path.Combine(directory, "archives", name), bytes);
            var members = new JsonArray();
            foreach (TestMpqFile f in files)
            {
                members.Add(new JsonObject
                {
                    ["path"] = f.Name,
                    ["deleted"] = f.Delete,
                    ["size"] = f.Data.Length,
                    ["sha256"] = f.Delete ? null : Convert.ToHexStringLower(SHA256.HashData(f.Data)),
                });
            }

            archives[name] = new JsonObject { ["file"] = "archives/" + name, ["size"] = bytes.Length, ["members"] = members };
        }

        TestMpqFile Text(string path, string text, bool zlib = true) => new(path, Encoding.UTF8.GetBytes(text)) { Zlib = zlib };

        AddArchive("dbc.MPQ",
        [
            new("DBFilesClient\\CreatureDisplayInfo.dbc", cdi) { Zlib = true },
            new("DBFilesClient\\Map.dbc", mapDbc) { Zlib = true },
            new("DBFilesClient\\AreaTable.dbc", areaDbc),
            Text("Text\\Chain.txt", "base copy"),
            Text("Text\\Gone.txt", "present in the base archive only"),
            Text("Text\\OnlyBase.txt", "only the base archive has this"),
            Text("Text\\Case\\MixedCase.TXT", "case-insensitive lookup target"),
        ]);
        AddArchive("model.MPQ", [new("Creature\\Canary\\" + CanaryMpqName + ".m2", Encoding.ASCII.GetBytes("CANARY_M2_PAYLOAD_00aa11bb")) { Zlib = true }]);
        AddArchive("texture.MPQ", [new("Textures\\Canary\\CanaryTexture.blp", [.. Encoding.ASCII.GetBytes("BLP2"), .. CanaryBlpBytes, .. new byte[600]]) { Zlib = true }]);
        AddArchive("terrain.MPQ", adtFiles);
        AddArchive("wmo.MPQ", [Text("World\\wmo\\Canary\\CanaryBuilding.wmo", "CANARY_WMO_PAYLOAD_deadbeef")]);
        AddArchive("patch.MPQ",
        [
            Text("Text\\Chain.txt", "patch one copy"),
            new("Text\\Gone.txt", []) { Delete = true },
        ]);
        AddArchive("patch-2.MPQ", [Text("Text\\Chain.txt", "patch two copy")]);
        // A patch that replaces the first tile's ADT. It is not in the good tree: tests add it as Data/patch-3.MPQ and stage the folder in any order.
        TileKey patchedTile = LiquidTiles[0];
        BuiltAdt patchedBuilt = SyntheticTerrain.Adt(SyntheticTerrain.Plane(patchedTile, 300f, 0.03f, 0.04f));
        GeometryChunk patchedTerrain = ClientTerrainGeometry.BuildTerrain(patchedTile, AdtFile.Parse(patchedBuilt.Bytes), directoryLookup, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(directory, "expected", "terrain-patched.bin"), GeometryWire.Write(patchedTerrain));
        AddArchive("patch-3.MPQ", [new TestMpqFile(ClientMapPaths.AdtPath(MapDirectoryName, patchedTile), patchedBuilt.Bytes) { Zlib = true }]);
        manifest["patchedTile"] = new JsonObject
        {
            ["map"] = patchedTile.MapId,
            ["x"] = patchedTile.X,
            ["y"] = patchedTile.Y,
            ["archive"] = "archives/patch-3.MPQ",
            ["terrain"] = "expected/terrain-patched.bin",
        };
        manifest["archives"] = archives;
        manifest["chain"] = new JsonArray
        {
            new JsonObject { ["path"] = "Text\\Chain.txt", ["text"] = "patch two copy", ["archive"] = "patch-2.MPQ" },
            new JsonObject { ["path"] = "Text\\Gone.txt", ["text"] = null, ["archive"] = null },
            new JsonObject { ["path"] = "Text\\OnlyBase.txt", ["text"] = "only the base archive has this", ["archive"] = "dbc.MPQ" },
            new JsonObject { ["path"] = "text/case/mixedcase.txt", ["text"] = "case-insensitive lookup target", ["archive"] = "dbc.MPQ" },
            new JsonObject { ["path"] = "Text\\Absent.txt", ["text"] = null, ["archive"] = null },
        };

        // The same dbc.MPQ with a wrong CreatureDisplayInfo layout, a wrong Map.dbc layout and a damaged header.
        AddArchive("dbc-badcdi.MPQ",
        [
            new("DBFilesClient\\CreatureDisplayInfo.dbc", CreatureDisplayInfo(14, out _)) { Zlib = true },
            new("DBFilesClient\\Map.dbc", mapDbc) { Zlib = true },
            new("DBFilesClient\\AreaTable.dbc", areaDbc),
        ]);
        AddArchive("dbc-badmap.MPQ",
        [
            new("DBFilesClient\\CreatureDisplayInfo.dbc", cdi) { Zlib = true },
            new("DBFilesClient\\Map.dbc", MapTable(40, out _)) { Zlib = true },
            new("DBFilesClient\\AreaTable.dbc", areaDbc),
        ]);
        byte[] damaged = File.ReadAllBytes(Path.Combine(directory, "archives", "dbc.MPQ"));
        damaged.AsSpan(0, 4).Clear();
        File.WriteAllBytes(Path.Combine(directory, "archives", "dbc-damaged.MPQ"), damaged);

        // PE files with a VS_VERSION_INFO resource.
        var exes = new JsonObject();
        foreach ((string key, ushort a, ushort b, ushort c, ushort d) in new[]
        {
            ("1.12.1.5875", (ushort)1, (ushort)12, (ushort)1, (ushort)5875),
            ("1.12.2.6005", (ushort)1, (ushort)12, (ushort)2, (ushort)6005),
            ("2.4.3.8606", (ushort)2, (ushort)4, (ushort)3, (ushort)8606),
        })
        {
            File.WriteAllBytes(Path.Combine(directory, "exe", $"WoW-{key}.exe"), PeWithVersion(a, b, c, d));
            exes[key] = $"exe/WoW-{key}.exe";
        }

        File.WriteAllBytes(Path.Combine(directory, "exe", "not-a-pe.exe"), Encoding.ASCII.GetBytes("MZ this is not a portable executable"));
        File.WriteAllBytes(Path.Combine(directory, "exe", "pe-without-resources.exe"), PeWithoutResources());
        exes["notPe"] = "exe/not-a-pe.exe";
        exes["noResources"] = "exe/pe-without-resources.exe";
        manifest["exe"] = exes;

        // Files that sit beside Data/ and must never be read: account names and settings.
        File.WriteAllText(Path.Combine(directory, "wtf", "Config.wtf"), $"SET realmName \"{CanaryConfig}\"\nSET accountName \"{CanaryAccount}\"\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(directory, "wtf", "SavedVariables.lua"), $"CanaryVar = \"{CanaryAccount}_saved\"\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(directory, "wtf", "Errors.txt"), $"{CanaryConfig} log line\n", Encoding.ASCII);
        manifest["tree"] = new JsonObject
        {
            ["WoW.exe"] = "exe/WoW-1.12.1.5875.exe",
            ["Data/dbc.MPQ"] = "archives/dbc.MPQ",
            ["Data/model.MPQ"] = "archives/model.MPQ",
            ["Data/texture.MPQ"] = "archives/texture.MPQ",
            ["Data/terrain.MPQ"] = "archives/terrain.MPQ",
            ["Data/wmo.MPQ"] = "archives/wmo.MPQ",
            ["Data/patch.MPQ"] = "archives/patch.MPQ",
            ["Data/patch-2.MPQ"] = "archives/patch-2.MPQ",
            [$"WTF/Account/{CanaryAccount}/SavedVariables.lua"] = "wtf/SavedVariables.lua",
            ["WTF/Config.wtf"] = "wtf/Config.wtf",
            ["Logs/Errors.txt"] = "wtf/Errors.txt",
            ["Interface/AddOns/Canary/Canary.toc"] = "wtf/Config.wtf",
        };

        manifest["canaries"] = new JsonArray
        {
            Canary("mpq-member-name", Encoding.ASCII.GetBytes(CanaryMpqName)),
            Canary("dbc-string", Encoding.ASCII.GetBytes(CanaryDbcString)),
            Canary("adt-string", Encoding.ASCII.GetBytes(CanaryAdtString)),
            Canary("config-wtf", Encoding.ASCII.GetBytes(CanaryConfig)),
            Canary("account-folder", Encoding.ASCII.GetBytes(CanaryAccount)),
            Canary("blp-bytes", CanaryBlpBytes),
        };

        WriteMalformed(directory, manifest);
        WritePkware(directory, manifest);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return manifest;
    }

    private static JsonObject Canary(string label, byte[] bytes) => new() { ["label"] = label, ["hex"] = Convert.ToHexStringLower(bytes) };

    // ---------------------------------------------------------------------------------------------------------------
    // Terrain

    private static Dictionary<TileKey, AdtSpec> TileSpecs()
    {
        var specs = new Dictionary<TileKey, AdtSpec>();

        // Tile 1: a sloped plane with a partial hole, a fully cut chunk, a chunk without heights and one river.
        AdtSpec a = SyntheticTerrain.Plane(LiquidTiles[0], 100f, 0.01f, -0.02f);
        a.Holes[5] = 0x0033;
        a.Holes[100] = 0xFFFF;
        a.WithoutHeights.Add(17);
        a.Areas[3] = 12;
        a.Areas[4] = 99; // an area AreaTable.dbc does not list
        a.Liquid[40] = [new LiquidLayerSpec(AdtLiquidKind.River, 5, 6, (y, x) => 5f + (y * 0.5f), (y, x) => y < 2 && x < 2 ? (byte)0 : AdtLiquidLayer.HiddenSquare)];
        a.Textures.Add("Tileset\\Elwynn\\" + CanaryAdtString + ".blp");
        specs[LiquidTiles[0]] = a;

        // Tile 2: rolling, non-planar heights with a base height per chunk, corner holes and two stacked liquid kinds.
        var b = new AdtSpec
        {
            Tile = LiquidTiles[1],
            BaseHeight = (row, col) => 50f + row,
            Outer = (row, col, y, x) => (float)((Math.Sin(((row * 8) + y) * 0.1) * 10) + (col * 0.5) + (x * 0.125)),
            Inner = (row, col, y, x) => (float)((Math.Sin(((row * 8) + y + 0.5) * 0.1) * 10) + (col * 0.5) + (x * 0.125) + 0.0625),
        };
        b.Holes[0] = 0x0001;
        b.Holes[255] = 0x8000;
        b.Holes[128] = 0xFFFF;
        b.Areas[5] = 12;
        b.Liquid[77] =
        [
            new LiquidLayerSpec(AdtLiquidKind.Ocean, 20, 25, (y, x) => 20f + (x * 0.25f), (y, x) => y == 7 ? AdtLiquidLayer.HiddenSquare : (byte)(y < 4 ? 0x80 : 0)),
            new LiquidLayerSpec(AdtLiquidKind.Magma, 3, 4, (y, x) => 3f + (y * 0.125f), (y, x) => y < 4 ? AdtLiquidLayer.HiddenSquare : (byte)0),
        ];
        b.Doodads.Add(new DoodadSpec("World\\Doodads\\" + CanaryAdtString + ".m2", 1, new WorldPoint(0, 0, 0), new WorldPoint(0, 0, 0), 1024, 0));
        specs[LiquidTiles[1]] = b;

        // Tile 3: negative base heights, a hole on an edge chunk, slime and river on separate chunks.
        var c = new AdtSpec
        {
            Tile = LiquidTiles[2],
            BaseHeight = (row, col) => -30f - col,
            Outer = (row, col, y, x) => ((y * 3) + x) * 0.25f,
            Inner = (row, col, y, x) => (((y * 3) + x) * 0.25f) + 0.125f,
        };
        c.Holes[15] = 0x00FF;
        c.Holes[240] = 0xFF00;
        c.Liquid[200] = [new LiquidLayerSpec(AdtLiquidKind.Slime, -20, -19, (y, x) => -20f + (x * 0.125f), (y, x) => (byte)0)];
        c.Liquid[201] = [new LiquidLayerSpec(AdtLiquidKind.River, -25, -24, (y, x) => -25f, (y, x) => x == 0 ? AdtLiquidLayer.HiddenSquare : (byte)0)];
        specs[LiquidTiles[2]] = c;

        // Tile 4: holes only; BuildLiquid answers null and the provider must report an empty tile.
        AdtSpec d = SyntheticTerrain.Plane(DryTile, 10f, 0f, 0f);
        d.Holes[9] = 0x0F0F;
        specs[DryTile] = d;
        return specs;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // DBC

    private sealed class Strings
    {
        private readonly List<byte> _bytes = [0];

        public uint Add(string text)
        {
            uint offset = (uint)_bytes.Count;
            _bytes.AddRange(Encoding.UTF8.GetBytes(text));
            _bytes.Add(0);
            return offset;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    private static byte[] Dbc(int fields, IReadOnlyList<uint[]> records, byte[] strings)
    {
        var bytes = new byte[20 + (records.Count * fields * 4) + strings.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457); // "WDBC"
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)(fields * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)strings.Length);
        for (int r = 0; r < records.Count; r++)
        {
            for (int f = 0; f < fields; f++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + (((r * fields) + f) * 4)), records[r][f]);
            }
        }

        strings.CopyTo(bytes, 20 + (records.Count * fields * 4));
        return bytes;
    }

    private static byte[] CreatureDisplayInfo(int fields, out JsonObject expect)
    {
        var s = new Strings();
        uint canary = s.Add(CanaryDbcString);
        uint plain = s.Add("Creature\\Plain\\Skin");
        var records = new List<uint[]>();
        for (uint r = 0; r < 3; r++)
        {
            var cells = new uint[fields];
            cells[0] = r + 1;
            cells[1] = 100 + r;
            cells[4] = BitConverter.SingleToUInt32Bits(1.0f + (r * 0.5f));
            cells[5] = 255;
            cells[6] = r == 0 ? canary : plain;
            records.Add(cells);
        }

        byte[] strings = s.ToArray();
        expect = new JsonObject
        {
            ["records"] = records.Count,
            ["fields"] = fields,
            ["recordSize"] = fields * 4,
            ["stringBlockSize"] = strings.Length,
            ["cells"] = new JsonArray([.. records.Select(r => (JsonNode)new JsonArray([.. r.Select(v => (JsonNode)JsonValue.Create(v)!)]))]),
            ["strings"] = new JsonObject { [canary.ToString()] = CanaryDbcString, [plain.ToString()] = "Creature\\Plain\\Skin", ["0"] = string.Empty },
        };
        return Dbc(fields, records, strings);
    }

    private static byte[] MapTable(int fields, out JsonObject expect)
    {
        var s = new Strings();
        uint directory = s.Add(MapDirectoryName);
        uint name = s.Add("Eastern Kingdoms");
        var cells = new uint[fields];
        cells[0] = 0;
        cells[1] = directory;
        cells[4] = name;
        expect = new JsonObject { ["records"] = 1, ["fields"] = fields, ["directory"] = MapDirectoryName, ["name"] = "Eastern Kingdoms" };
        return Dbc(fields, [cells], s.ToArray());
    }

    private static byte[] AreaTable(int fields, out JsonObject expect)
    {
        var s = new Strings();
        uint name = s.Add(NamedAreaName);
        var cells = new uint[fields];
        cells[0] = NamedArea;
        cells[11] = name;
        expect = new JsonObject { ["records"] = 1, ["fields"] = fields, ["areaId"] = NamedArea, ["name"] = NamedAreaName };
        return Dbc(fields, [cells], s.ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // PE with a version resource (Microsoft PE format: DOS header, PE signature, COFF header, PE32 optional header with
    // 16 data directories, one .rsrc section holding RT_VERSION / id 1 / language 0x0409 -> VS_VERSIONINFO with a
    // VS_FIXEDFILEINFO, signature 0xFEEF04BD).

    private const int PeHeaderOffset = 0x80;
    private const int SectionRva = 0x1000;
    private const int RawOffset = 0x200;

    private static byte[] PeWithVersion(ushort major, ushort minor, ushort patch, ushort build)
    {
        // VS_VERSIONINFO: wLength, wValueLength (52), wType (0), "VS_VERSION_INFO\0", pad to 4, VS_FIXEDFILEINFO.
        byte[] key = Encoding.Unicode.GetBytes("VS_VERSION_INFO\0");
        int fixedOffset = (6 + key.Length + 3) & ~3;
        var info = new byte[fixedOffset + 52];
        BinaryPrimitives.WriteUInt16LittleEndian(info, (ushort)info.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(2), 52);
        key.CopyTo(info, 6);
        Span<byte> fixedInfo = info.AsSpan(fixedOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo, 0xFEEF04BD);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo[4..], 0x00010000);
        uint ms = ((uint)major << 16) | minor;
        uint ls = ((uint)patch << 16) | build;
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo[8..], ms);  // dwFileVersionMS
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo[12..], ls); // dwFileVersionLS
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo[16..], ms); // dwProductVersionMS
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo[20..], ls); // dwProductVersionLS

        // Resource section: three directories of one entry, the data entry, then the blob.
        const int dirSize = 16 + 8;
        int level2 = dirSize;
        int level3 = dirSize * 2;
        int dataEntry = dirSize * 3;
        int blob = dataEntry + 16;
        var rsrc = new byte[blob + info.Length];
        void Dir(int at, uint id, int childOffset, bool subdirectory)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(at + 14), 1); // NumberOfIdEntries
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(at + 16), id);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(at + 20), (uint)childOffset | (subdirectory ? 0x80000000u : 0));
        }

        Dir(0, 16, level2, true);       // RT_VERSION
        Dir(level2, 1, level3, true);   // resource id 1
        Dir(level3, 0x0409, dataEntry, false); // en-US
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(dataEntry), (uint)(SectionRva + blob));
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(dataEntry + 4), (uint)info.Length);
        info.CopyTo(rsrc, blob);
        return Pe(rsrc);
    }

    private static byte[] PeWithoutResources() => Pe(null);

    private static byte[] Pe(byte[]? rsrc)
    {
        int rawSize = rsrc is null ? 0x200 : (rsrc.Length + 0x1FF) & ~0x1FF;
        var file = new byte[RawOffset + rawSize];
        file[0] = (byte)'M';
        file[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x3C), PeHeaderOffset);
        Span<byte> pe = file.AsSpan(PeHeaderOffset);
        pe[0] = (byte)'P';
        pe[1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(pe[4..], 0x014C);   // Machine: i386
        BinaryPrimitives.WriteUInt16LittleEndian(pe[6..], 1);        // NumberOfSections
        BinaryPrimitives.WriteUInt16LittleEndian(pe[20..], 0xE0);    // SizeOfOptionalHeader (PE32)
        BinaryPrimitives.WriteUInt16LittleEndian(pe[22..], 0x0102);  // Characteristics
        Span<byte> opt = pe[24..];
        BinaryPrimitives.WriteUInt16LittleEndian(opt, 0x010B);       // PE32 magic
        BinaryPrimitives.WriteUInt32LittleEndian(opt[32..], 0x1000); // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(opt[36..], 0x200);  // FileAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(opt[56..], (uint)(SectionRva + 0x1000)); // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(opt[60..], RawOffset); // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(opt[92..], 16);     // NumberOfRvaAndSizes
        if (rsrc is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(opt[(96 + (2 * 8))..], SectionRva);           // resource directory RVA
            BinaryPrimitives.WriteUInt32LittleEndian(opt[(96 + (2 * 8) + 4)..], (uint)rsrc.Length); // size
        }

        Span<byte> section = pe[(24 + 0xE0)..];
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(section);
        BinaryPrimitives.WriteUInt32LittleEndian(section[8..], (uint)(rsrc?.Length ?? 0x10)); // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(section[12..], SectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(section[16..], (uint)rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(section[20..], RawOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(section[36..], 0x40000040); // initialized data, readable
        rsrc?.CopyTo(file, RawOffset);
        return file;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Malformed inputs

    private static byte[] Zeros(int count) => new byte[count];

    private static byte[] ZlibOf(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] BaseMalformedSource()
    {
        var data = new byte[3 * 4096 - 100]; // three 4 KiB sectors at shift 3, the last one short
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31) + (i >> 8));
        }

        return data;
    }

    private static byte[] BaseMalformed() => MpqTestWriter.Build(
        [new TestMpqFile("Control\\Data.bin", BaseMalformedSource()) { Zlib = true }],
        new TestMpqOptions { SectorShift = 3, ListFile = false, HashSize = 16 });

    private static void WriteMalformed(string directory, JsonObject manifest)
    {
        byte[] source = BaseMalformedSource();
        byte[] good = BaseMalformed();
        uint hashOffset = BinaryPrimitives.ReadUInt32LittleEndian(good.AsSpan(16));
        uint blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(good.AsSpan(20));
        var cases = new JsonArray();
        var coded = new Dictionary<string, (byte[] Bytes, string[] Codes, string Limit)>();

        byte[] Header(int at, uint value, bool u16 = false)
        {
            byte[] copy = (byte[])good.Clone();
            if (u16)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(at), (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(at), value);
            }

            return copy;
        }

        byte[] Block(int field, uint value)
        {
            byte[] copy = (byte[])good.Clone();
            Span<byte> table = copy.AsSpan((int)blockOffset, 16); // one file, one block
            MpqCrypto.Decrypt(table, MpqCrypto.BlockTableKey);
            BinaryPrimitives.WriteUInt32LittleEndian(table[(field * 4)..], value);
            MpqTestWriter.Encrypt(table, MpqCrypto.BlockTableKey);
            return copy;
        }

        byte[] SectorTable(int entry, uint value)
        {
            byte[] copy = (byte[])good.Clone();
            // The file starts right after the 0x20-byte header; its sector offset table is the first thing in it.
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(0x20 + (entry * 4)), value);
            return copy;
        }

        string[] archive = ["bad-archive"];
        string[] either = ["bad-archive", "bad-file"];
        string[] file = ["bad-file"];
        coded["archive-size-beyond-file"] = (Header(8, (uint)good.Length + 0x1000), archive, "archiveSize inside the real file size");
        coded["archive-size-before-tables"] = (Header(8, hashOffset + 16), archive, "tables inside the declared archive size");
        coded["hash-offset-beyond-file"] = (Header(16, (uint)good.Length + 0x10000), archive, "hash table offset inside the file");
        coded["block-offset-beyond-file"] = (Header(20, (uint)good.Length + 0x10000), archive, "block table offset inside the file");
        coded["hash-table-overruns-file"] = (Header(24, 0x40000), archive, "entries * 16 inside the file");
        coded["hash-count-over-2^20"] = (Header(24, 0x200000), archive, "hash entry count <= 2^20");
        coded["block-count-over-2^20"] = (Header(28, 0x100001), archive, "block entry count <= 2^20");
        coded["hash-count-not-power-of-two"] = (Header(24, 12), archive, "hash count a power of two");
        coded["sector-shift-16"] = (Header(14, 16, u16: true), archive, "sector shift <= 15");
        coded["truncated-header"] = (good[..16], archive, "header inside the file");
        coded["empty"] = ([], archive, "non-empty file");
        coded["not-an-mpq"] = (Enumerable.Repeat((byte)0x5A, 4096).ToArray(), archive, "magic");
        coded["block-size-beyond-archive"] = (Block(1, 0x7FFFFF00), either, "block offset + compressedSize inside the archive");
        coded["block-offset-beyond-archive"] = (Block(0, (uint)good.Length + 0x1000), either, "block offset inside the archive");
        coded["file-size-over-64MiB"] = (Block(2, (64u << 20) + 1), either, "fileSize <= 64 MiB");
        coded["file-size-4GiB-1"] = (Block(2, 0xFFFFFFFF), either, "fileSize <= 64 MiB");
        coded["sector-offsets-not-monotonic"] = (SectorTable(2, BinaryPrimitives.ReadUInt32LittleEndian(good.AsSpan(0x20 + 4)) - 1), file, "sector offsets monotonic");
        coded["sector-offset-past-block"] = (SectorTable(3, 0x00FFFFFF), file, "sector offsets inside the block");
        coded["sector-table-too-small"] = (Block(1, 8), either, "count + 1 sector offsets inside the block");

        // A zlib sector that expands to 16 MiB where 4096 bytes are expected, and one that stops short.
        coded["zlib-bomb"] = (MpqTestWriter.Build(
            [new TestMpqFile("Bomb.bin", new byte[8192]) { StoredSectors = [[0x02, .. ZlibOf(Zeros(16 << 20))], [0x02, .. ZlibOf(Zeros(4096))]], CompressFlag = true }],
            new TestMpqOptions { SectorShift = 3, ListFile = false, HashSize = 16 }), file, "decompressed sector exactly its expected size (cancel past it)");
        coded["zlib-short"] = (MpqTestWriter.Build(
            [new TestMpqFile("Short.bin", new byte[8192]) { StoredSectors = [[0x02, .. ZlibOf(Zeros(100))], [0x02, .. ZlibOf(Zeros(4096))]], CompressFlag = true }],
            new TestMpqOptions { SectorShift = 3, ListFile = false, HashSize = 16 }), file, "decompressed sector exactly its expected size (short)");

        // A PKWARE stream (the V2 vector, 6001 bytes) in a file declared 3000 bytes long.
        PkwareVector v2 = PkwareVectors.All.First(v => v.Source == PlainText.V2);
        coded["explode-bomb"] = (MpqTestWriter.Build(
            [new TestMpqFile("Explode.bin", new byte[3000]) { StoredSectors = [v2.Stream], ImplodeFlag = true }],
            new TestMpqOptions { SectorShift = 3, ListFile = false, HashSize = 16 }), file, "explode into a preallocated buffer of the expected size");

        // bzip2 (mask 0x10) stays refused until the census has run.
        coded["bzip2-refused"] = (MpqTestWriter.Build(
            [new TestMpqFile("Bzip.bin", new byte[4096]) { StoredSectors = [[0x10, 0x42, 0x5A, 0x68, 0x39, .. new byte[20]]], CompressFlag = true }],
            new TestMpqOptions { SectorShift = 3, ListFile = false, HashSize = 16 }), ["unsupported-compression"], "bzip2 refused");

        foreach ((string name, (byte[] bytes, string[] codes, string limit)) in coded)
        {
            string relative = $"malformed/{name}.MPQ";
            File.WriteAllBytes(Path.Combine(directory, relative), bytes);
            cases.Add(new JsonObject
            {
                ["name"] = name,
                ["file"] = relative,
                ["limit"] = limit,
                ["codes"] = new JsonArray([.. codes.Select(c => (JsonNode)JsonValue.Create(c)!)]),
                ["read"] = name switch
                {
                    "zlib-bomb" => "Bomb.bin",
                    "zlib-short" => "Short.bin",
                    "explode-bomb" => "Explode.bin",
                    "bzip2-refused" => "Bzip.bin",
                    _ => "Control\\Data.bin",
                },
            });
        }

        // The unmodified archive: the control that proves the mutated copies fail because of their mutation.
        File.WriteAllBytes(Path.Combine(directory, "malformed", "control.MPQ"), good);
        manifest["malformedControl"] = new JsonObject
        {
            ["file"] = "malformed/control.MPQ",
            ["path"] = "Control\\Data.bin",
            ["size"] = source.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(source)),
        };
        manifest["malformedArchives"] = cases;

        // Malformed ADTs: each is also refused by the C# parser (checked in the fixture tests).
        BuiltAdt plane = SyntheticTerrain.Adt(SyntheticTerrain.Plane(LiquidTiles[0], 100f, 0.01f, -0.02f));
        var adts = new JsonArray();
        void Adt(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(directory, "adt", "bad-" + name + ".adt"), bytes);
            adts.Add(new JsonObject { ["name"] = name, ["file"] = "adt/bad-" + name + ".adt" });
        }

        Adt("empty", []);
        Adt("truncated-half", plane.Bytes[..(plane.Bytes.Length / 2)]);
        Adt("mver-17", plane.WithU32(8, 17));
        Adt("mcin-offset-past-eof", plane.WithU32(plane.McinData, 0xFFFFFFF0));
        Adt("mcnk-size-huge", plane.WithU32(plane.McnkStarts[0] + 4, 0xFFFFFFFF));
        Adt("mcvt-offset-past-file", plane.WithU32(plane.McnkData(0) + 20, 0x7FFFFFF0));
        manifest["malformedAdts"] = adts;
    }

    private static void WritePkware(string directory, JsonObject manifest)
    {
        var vectors = new JsonArray();
        for (int i = 0; i < PkwareVectors.All.Length; i++)
        {
            PkwareVector v = PkwareVectors.All[i];
            byte[] plain = PkwareVectors.Plain(v.Source);
            File.WriteAllBytes(Path.Combine(directory, "pkware", $"v{i}.stream"), v.Stream);
            File.WriteAllBytes(Path.Combine(directory, "pkware", $"v{i}.plain"), plain);
            vectors.Add(new JsonObject
            {
                ["name"] = $"{v.Source}/{v.DictionaryBytes}",
                ["stream"] = $"pkware/v{i}.stream",
                ["plain"] = $"pkware/v{i}.plain",
                ["plainLength"] = v.PlainLength,
            });
        }

        manifest["pkware"] = vectors;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Real client (gated)

    /// <summary>
    /// Samples the real client through the C# reader: every DBC, up to 24 ADTs spread over the map folders, the DBCs patch.MPQ
    /// deletes, and the ordered list of archives. The JS side must read the same bytes from the same archives.
    /// </summary>
    public static JsonObject WriteRealSample(string clientRoot, string directory)
    {
        Directory.CreateDirectory(directory);
        using MpqFileSystem files = MpqFileSystem.Open(Path.Combine(clientRoot, "Data"));
        var samples = new JsonArray();
        List<string> paths = [.. files.List("DBFilesClient\\").Where(p => p.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))];
        List<string> adts = [.. files.List("World\\Maps\\").Where(p => p.EndsWith(".adt", StringComparison.OrdinalIgnoreCase))];
        int step = Math.Max(1, adts.Count / 24);
        for (int i = 0; i < adts.Count; i += step)
        {
            paths.Add(adts[i]);
        }

        foreach (string path in paths)
        {
            byte[]? bytes = files.Read(path);
            if (bytes is null)
            {
                continue;
            }

            samples.Add(new JsonObject
            {
                ["path"] = path,
                ["size"] = bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                ["archive"] = files.ArchiveOf(path),
            });
        }

        var deleted = new JsonArray();
        foreach (string name in new[] { "CharacterCreateCameras.dbc", "SoundCharacterMacroLines.dbc", "SpellAuraNames.dbc", "SpellEffectNames.dbc" })
        {
            deleted.Add("DBFilesClient\\" + name);
        }

        var root = new JsonObject
        {
            ["samples"] = samples,
            ["deleted"] = deleted,
            ["order"] = new JsonArray([.. files.Archives.Select(a => (JsonNode)JsonValue.Create(a.Name)!)]),
        };
        File.WriteAllText(Path.Combine(directory, "real.json"), root.ToJsonString());
        return root;
    }
}
