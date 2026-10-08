using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// ArcaneCore's terrain, vmap and navmesh readers on a real extraction from the 1.12.1 client
/// (vmangos MapExtractor / VMapExtractor + VMapAssembler / MoveMapGenerator; recipe in
/// docs/integration/maps-vmaps-mmaps.md). The data is derived from Blizzard's assets and is never
/// committed, so these tests run only when <c>ARCANECORE_TEST_TERRAIN_DIR</c> names the data root
/// (the directory holding <c>maps/</c>, <c>vmaps/</c> and <c>mmaps/</c>, i.e. the value of
/// <c>World:Maps:DataDirectory</c>) and are skipped, visibly, otherwise.
/// <para>
/// Every reader is fail-soft (missing or unreadable data means "open, no height, straight line"),
/// so each check asserts something that is only true when the files were actually read: a real
/// ground height near a known spawn, a ray that a building blocks, a navmesh path with corners.
/// Expected values are world-database positions (classic-db <c>playercreateinfo</c> and
/// <c>creature</c> rows), not values read back from the files. The model floors were also checked
/// against vmangos' own <c>VMapManager2</c> on the same files (2026-10-07: abbey 82.125366, inn
/// 56.962559, crypt 121.670341; ArcaneCore reads the same to within 1e-4).
/// </para>
/// </summary>
public sealed class RealTerrainDataTests(ITestOutputHelper output)
{
    private const uint EasternKingdoms = 0;
    private const uint Kalimdor = 1;

    /// <summary>
    /// Race start positions on open ground (classic-db <c>playercreateinfo</c>): map, x, y, z. The
    /// undead start is not here: it lies in the Deathknell crypt, below the terrain surface (see
    /// <see cref="UndeadStart_IsInsideTheCrypt_BelowTheTerrain"/>).
    /// </summary>
    public static TheoryData<string, uint, float, float, float> StartPositions => new()
    {
        { "Northshire (human)", EasternKingdoms, -8949.95f, -132.493f, 83.5312f },
        { "Coldridge Valley (dwarf, gnome)", EasternKingdoms, -6240.32f, 331.033f, 382.758f },
        { "Valley of Trials (orc, troll)", Kalimdor, -618.518f, -4251.67f, 38.718f },
        { "Shadowglen (night elf)", Kalimdor, 10311.3f, 831.463f, 1326.41f },
        { "Camp Narache (tauren)", Kalimdor, -2917.58f, -257.98f, 52.9968f },
    };

    // Undead start, inside the Deathknell crypt (playercreateinfo race 5).
    private static readonly Vector3 UndeadStart = new(1676.35f, 1677.45f, 121.67f);

    // Llane Beshere, warrior trainer inside Northshire Abbey (classic-db creature guid 79964).
    private static readonly Vector3 InsideAbbey = new(-8918.36f, -208.411f, 82.3088f);

    // Marshal McBride, outside in front of the abbey (guid 79970).
    private static readonly Vector3 InFrontOfAbbey = new(-8902.59f, -162.606f, 82.0223f);

    // Farley, innkeeper inside the Lion's Pride Inn, Goldshire (guid 80346).
    private static readonly Vector3 InsideGoldshireInn = new(-9462.66f, 16.1915f, 57.0459f);

    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    [RealTerrainFact]
    public void EveryContinentMapFile_ParsesAsZ14()
    {
        string maps = Path.Combine(Root, "maps");
        foreach (uint mapId in new[] { EasternKingdoms, Kalimdor })
        {
            string[] files = Directory.GetFiles(maps, $"{mapId:D3}????.map");
            Assert.NotEmpty(files);
            foreach (string file in files)
            {
                TerrainTile tile = TerrainTile.Parse(File.ReadAllBytes(file));
                Assert.True(tile.HasData, file);
            }

            output.WriteLine($"map {mapId}: {files.Length} .map files parsed");
        }
    }

    [RealTerrainTheory]
    [MemberData(nameof(StartPositions))]
    public void StartPositions_StandOnTheExtractedGround(string name, uint mapId, float x, float y, float z)
    {
        var terrain = new TerrainManager(Root);
        float ground = terrain.For(mapId).GetHeight(x, y, z);
        ushort areaFlag = terrain.For(mapId).GetAreaFlag(x, y, z);
        output.WriteLine($"{name}: spawn z {z}, ground {ground}, area flag {areaFlag}");

        Assert.Equal(1, terrain.FilesLoaded);
        Assert.InRange(ground, z - 2.5f, z + 2.5f);
        Assert.NotEqual(0, areaFlag);
    }

    [RealTerrainFact]
    public void VMaps_TheAbbeyFloorIsAModel_AndItsWallsBlockSight()
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        VMapTree? tree = vmaps.GetTree(EasternKingdoms);
        Assert.NotNull(tree);
        Assert.True(tree.IsTiled);
        (int tx, int ty) = TerrainTile.TileOf(InsideAbbey.X, InsideAbbey.Y)!.Value;
        Assert.True(vmaps.LoadTile(EasternKingdoms, tx, ty));

        float? floor = vmaps.GetModelHeight(EasternKingdoms, InsideAbbey.X, InsideAbbey.Y, InsideAbbey.Z + 2, 10);
        output.WriteLine($"abbey floor model height {floor?.ToString() ?? "none"} (trainer z {InsideAbbey.Z}); {vmaps.ModelFilesLoaded} model files read");
        Assert.NotNull(floor);
        Assert.InRange(floor.Value, InsideAbbey.Z - 1.5f, InsideAbbey.Z + 1.5f);

        Assert.True(vmaps.TryGetAreaInfo(EasternKingdoms, InsideAbbey.X, InsideAbbey.Y, InsideAbbey.Z + 1, out ModelAreaInfo area));
        output.WriteLine($"abbey area info: mogp 0x{area.MogpFlags:X}, root {area.RootId}, group {area.GroupId}, ground {area.GroundZ}, outdoors {area.IsOutdoors}");
        Assert.False(area.IsOutdoors);

        Vector3 eye = new(0, 0, 2);
        Assert.False(vmaps.IsInLineOfSight(EasternKingdoms, InsideAbbey + eye, InFrontOfAbbey + eye));

        // The same two points seen from well above the roof: nothing in between.
        Vector3 sky = new(0, 0, 120);
        Assert.True(vmaps.IsInLineOfSight(EasternKingdoms, InsideAbbey + sky, InFrontOfAbbey + sky));
    }

    [RealTerrainFact]
    public void UndeadStart_IsInsideTheCrypt_BelowTheTerrain()
    {
        var terrain = new TerrainManager(Root);
        float surface = terrain.For(EasternKingdoms).GetHeight(UndeadStart.X, UndeadStart.Y, UndeadStart.Z);
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        (int tx, int ty) = TerrainTile.TileOf(UndeadStart.X, UndeadStart.Y)!.Value;
        Assert.True(vmaps.LoadTile(EasternKingdoms, tx, ty));
        float? floor = vmaps.GetModelHeight(EasternKingdoms, UndeadStart.X, UndeadStart.Y, UndeadStart.Z + 2, 10);
        output.WriteLine($"Deathknell: spawn z {UndeadStart.Z}, terrain surface {surface}, crypt floor {floor?.ToString() ?? "none"}");

        Assert.True(surface > UndeadStart.Z + 10, "the terrain surface is well above the crypt");
        Assert.NotNull(floor);
        Assert.InRange(floor.Value, UndeadStart.Z - 1.5f, UndeadStart.Z + 1.5f);
        Assert.True(vmaps.TryGetAreaInfo(EasternKingdoms, UndeadStart.X, UndeadStart.Y, UndeadStart.Z + 1, out ModelAreaInfo area));
        Assert.False(area.IsOutdoors);
    }

    [RealTerrainFact]
    public void VMaps_GoldshireInnFloorIsAModel()
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        (int tx, int ty) = TerrainTile.TileOf(InsideGoldshireInn.X, InsideGoldshireInn.Y)!.Value;
        Assert.True(vmaps.LoadTile(EasternKingdoms, tx, ty));

        float? floor = vmaps.GetModelHeight(EasternKingdoms, InsideGoldshireInn.X, InsideGoldshireInn.Y, InsideGoldshireInn.Z + 2, 10);
        output.WriteLine($"inn floor model height {floor?.ToString() ?? "none"} (innkeeper z {InsideGoldshireInn.Z})");
        Assert.NotNull(floor);
        Assert.InRange(floor.Value, InsideGoldshireInn.Z - 1.5f, InsideGoldshireInn.Z + 1.5f);
    }

    [RealTerrainFact]
    public void VMaps_DoodadsStoredWithANulTerminatedName_AreLoaded()
    {
        // A bed in Darnassus (Elfbed01.m2, spawn 131453) whose vmtile name is "Elfbed01.m2\0"; its
        // model file is "Elfbed01.m2" without ".vmo". vmangos' VMapManager2 stands on the bed top
        // (1347.2909) above the Darnassus floor (1346.6178) at this spot.
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        const float x = 9755.158f, y = 2192.4019f, z = 1354.8529f;
        (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
        Assert.True(vmaps.LoadTile(Kalimdor, tx, ty));

        float? top = vmaps.GetModelHeight(Kalimdor, x, y, z, 50);
        output.WriteLine($"bed top {top?.ToString() ?? "none"}");
        Assert.NotNull(top);
        Assert.InRange(top.Value, 1347.28f, 1347.30f);
    }

    [RealTerrainFact]
    public void EveryVmapFile_Parses()
    {
        string dir = Path.Combine(Root, "vmaps");
        var failures = new List<string>();
        int files = 0;
        void Check(string pattern, Action<string, byte[]> parse)
        {
            foreach (string file in Directory.GetFiles(dir, pattern))
            {
                files++;
                try
                {
                    parse(file, File.ReadAllBytes(file));
                }
                catch (InvalidDataException ex)
                {
                    failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        Check("*.vmtree", (file, bytes) => VMapTree.Parse(uint.Parse(Path.GetFileName(file)[..3], System.Globalization.CultureInfo.InvariantCulture), bytes));
        Check("*.vmtile", (_, bytes) => VMapTree.ParseTile(bytes));
        Check("*.vmo", (_, bytes) => WorldModel.Parse(bytes));
        Check("*.m2", (_, bytes) => WorldModel.Parse(bytes)); // gameobject model copies (temp_gameobject_models)

        output.WriteLine($"{files} vmap files read, {failures.Count} unreadable");
        foreach (string failure in failures.Take(10))
        {
            output.WriteLine("  " + failure);
        }

        Assert.True(files > 0);
        Assert.Empty(failures);
    }

    [RealTerrainFact]
    public void EveryContinentNavmeshTile_Parses_WithDistinctDetourCoordinates()
    {
        string dir = Path.Combine(Root, "mmaps");
        foreach (uint mapId in new[] { EasternKingdoms, Kalimdor })
        {
            string paramsFile = Path.Combine(dir, NavMeshFormat.ParamsFileName(mapId));
            Assert.True(File.Exists(paramsFile), paramsFile);
            NavMeshParams parameters = NavMeshParams.Parse(File.ReadAllBytes(paramsFile));
            var seen = new HashSet<(int, int)>();
            string[] files = Directory.GetFiles(dir, $"{mapId:D3}????.mmtile");
            foreach (string file in files)
            {
                NavMeshTile tile = NavMeshTile.ParseFile(File.ReadAllBytes(file));
                Assert.True(seen.Add((tile.X, tile.Y)), $"{file}: Detour tile ({tile.X}, {tile.Y}) appears twice");
            }

            output.WriteLine($"map {mapId}: {files.Length} navmesh tiles parsed (params: {parameters.MaxTiles} max tiles)");
            Assert.NotEmpty(files);
            Assert.True(files.Length <= parameters.MaxTiles);
        }
    }

    [RealTerrainFact]
    public void MMaps_NorthshireToGoldshire_CrossesTiles()
    {
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        Vector3 start = new(-8949.95f, -132.493f, 83.5312f);
        (int sx, int sy) = TerrainTile.TileOf(start.X, start.Y)!.Value;
        (int ex, int ey) = TerrainTile.TileOf(InsideGoldshireInn.X, InsideGoldshireInn.Y)!.Value;
        Assert.NotEqual((sx, sy), (ex, ey));
        for (int x = Math.Min(sx, ex) - 1; x <= Math.Max(sx, ex) + 1; x++)
        {
            for (int y = Math.Min(sy, ey) - 1; y <= Math.Max(sy, ey) + 1; y++)
            {
                nav.LoadTile(EasternKingdoms, x, y);
            }
        }

        PathResult path = nav.FindPath(EasternKingdoms, start, InsideGoldshireInn);
        output.WriteLine($"Northshire -> Goldshire inn: {path.Type}, {path.Points.Count} points, length {path.Length:F1} (straight {Vector3.Distance(start, InsideGoldshireInn):F1}), ends at {path.End}");

        Assert.True(path.HasPath);
        Assert.False(path.Type.HasFlag(PathType.NotUsingPath));
        Assert.True(path.Points.Count > 2);
        Assert.True(path.Length > Vector3.Distance(start, path.End));
    }

    [RealTerrainFact]
    public void MMaps_NorthshireNavmesh_WalksOutOfTheAbbey()
    {
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        Assert.True(nav.HasNavigationData(EasternKingdoms));
        (int tx, int ty) = TerrainTile.TileOf(InsideAbbey.X, InsideAbbey.Y)!.Value;
        Assert.True(nav.LoadTile(EasternKingdoms, tx, ty));

        Vector3 start = new(-8949.95f, -132.493f, 83.5312f);
        PathResult path = nav.FindPath(EasternKingdoms, start, InsideAbbey);
        output.WriteLine($"path {path.Type}, {path.Points.Count} points, length {path.Length:F1} (straight {Vector3.Distance(start, InsideAbbey):F1})");
        foreach (Vector3 p in path.Points)
        {
            output.WriteLine($"  {p.X:F2} {p.Y:F2} {p.Z:F2}");
        }

        Assert.Equal(PathType.Normal, path.Type);
        Assert.True(path.Points.Count > 2, "a path out of the abbey has corners");
        Assert.True(Vector3.Distance(path.End, InsideAbbey) < 2f);
        Assert.True(path.Length > Vector3.Distance(start, InsideAbbey));
    }
}

/// <summary>A fact that needs a real terrain extraction; skipped, visibly, without one.</summary>
public sealed class RealTerrainFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_TERRAIN_DIR";

    public RealTerrainFactAttribute()
    {
        if (RealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>The theory form of <see cref="RealTerrainFactAttribute"/>.</summary>
public sealed class RealTerrainTheoryAttribute : TheoryAttribute
{
    public RealTerrainTheoryAttribute()
    {
        if (RealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class RealTerrain
{
    public static string? SkipReason()
    {
        string? dir = Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable);
        return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "maps"))
            ? $"{RealTerrainFactAttribute.Variable} is not set to a terrain data root (maps/, vmaps/, mmaps/ from the 1.12.1 client)."
            : null;
    }
}
