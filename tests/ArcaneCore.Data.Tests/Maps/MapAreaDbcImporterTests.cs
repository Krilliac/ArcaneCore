using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Maps;

/// <summary>
/// map_template and area_template from Map.dbc and AreaTable.dbc: every map and every area (vmangos keeps the whole AreaTable in
/// area_template, instance areas included), the dungeon columns from the dump's instance rows, and the tables replaced as a whole.
/// </summary>
public sealed class MapAreaDbcImporterTests
{
    private static readonly IReadOnlyDictionary<uint, MapInstanceData> Deadmines = new Dictionary<uint, MapInstanceData>
    {
        [36] = new(0, 10, 0, 0, -11207.8f, 1681.15f, "instance_deadmines"),
        [999] = new(0, 5, 0, -1, 0, 0, "instance_nowhere"),
    };

    [Fact]
    public async Task Replace_ImportsEveryMapAndArea_WithTheDumpsDungeonColumns_AndReplacesWhatWasThere()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<MapTemplateRow>().Add(new MapTemplateRow { Entry = 36, MapName = "custom" });
        db.Set<AreaTemplateRow>().Add(new AreaTemplateRow { Entry = 3600, MapId = 36, Name = "custom" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); // ImportTransaction intentionally refuses caller-owned tracked entities.

        using TempDbc files = CreateFiles(badParent: false);
        MapAreaDbcImportReport report = await MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, Deadmines, replace: true);

        Assert.Equal((3, 5, 1), (report.MappedMaps, report.MappedAreas, report.InstanceRows));
        Assert.Contains(report.Warnings, w => w.Contains("map 999", StringComparison.Ordinal));
        uint[] mapIds = await db.Set<MapTemplateRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToArrayAsync();
        uint[] areaIds = await db.Set<AreaTemplateRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToArrayAsync();
        Assert.Equal([0u, 1u, 36u], mapIds);
        Assert.Equal([1u, 2u, 3u, 4u, 5u], areaIds);
        MapTemplateRow dungeon = await db.Set<MapTemplateRow>().SingleAsync(r => r.Entry == 36);
        Assert.Equal(((byte)1, 1581u, "Deadmines"), (dungeon.MapType, dungeon.LinkedZone, dungeon.MapName));
        Assert.Equal((10u, 0, -11207.8f, 1681.15f, "instance_deadmines"),
            (dungeon.PlayerLimit, dungeon.GhostEntranceMap, dungeon.GhostEntranceX, dungeon.GhostEntranceY, dungeon.ScriptName));
        Assert.Equal(36u, await db.Set<AreaTemplateRow>().Where(r => r.Entry == 4).Select(r => r.MapId).SingleAsync());
        Assert.Equal(17u, await db.Set<AreaTemplateRow>().Where(r => r.Entry == 5).Select(r => r.MapId).SingleAsync());
    }

    [Fact]
    public async Task WithoutInstanceRows_TheDungeonsKeepTheSqlDefaults_AndAWarningSaysSo()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

        using TempDbc files = CreateFiles(badParent: false);
        MapAreaDbcImportReport report = await MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: false);

        Assert.Equal((3, 5, 0), (report.MappedMaps, report.MappedAreas, report.InstanceRows));
        Assert.Contains(report.Warnings, w => w.Contains("36", StringComparison.Ordinal));
        MapTemplateRow dungeon = await db.Set<MapTemplateRow>().SingleAsync(r => r.Entry == 36);
        Assert.Equal((0u, -1, ""), (dungeon.PlayerLimit, dungeon.GhostEntranceMap, dungeon.ScriptName));
    }

    [Fact]
    public async Task WithoutReplace_ExistingRowsAreRefused_AndNothingChanges()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<AreaTemplateRow>().Add(new AreaTemplateRow { Entry = 3600, MapId = 36, Name = "custom" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using TempDbc files = CreateFiles(badParent: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: false));
        Assert.Empty(await db.Set<MapTemplateRow>().ToArrayAsync());
        uint[] areaIds = await db.Set<AreaTemplateRow>().Select(r => r.Entry).ToArrayAsync();
        Assert.Equal([3600u], areaIds);
    }

    [Fact]
    public async Task InvalidParent_RefusesBeforeWritingAnyRows()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<MapTemplateRow>().Add(new MapTemplateRow { Entry = 36, MapName = "custom" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using TempDbc files = CreateFiles(badParent: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: true));
        uint[] mapIds = await db.Set<MapTemplateRow>().Select(r => r.Entry).ToArrayAsync();
        Assert.Equal([36u], mapIds);
        Assert.Empty(await db.Set<AreaTemplateRow>().ToArrayAsync());
    }

    [Fact]
    public async Task ParentCycles_AreRejectedBeforeDestinationWrites()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<MapTemplateRow>().Add(new MapTemplateRow { Entry = 36, MapName = "custom" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using TempDbc files = CreateCycleFiles();
        await Assert.ThrowsAsync<InvalidDataException>(() => MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: true));
        uint[] mapIds = await db.Set<MapTemplateRow>().Select(r => r.Entry).ToArrayAsync();
        Assert.Equal([36u], mapIds);
        Assert.Empty(await db.Set<AreaTemplateRow>().ToArrayAsync());
    }

    private static WorldDbContext Context(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite(connection).Options);

    // Map.dbc: the continents and The Deadmines (dungeon, linked zone 1581); AreaTable.dbc: Elwynn and a child, a Kalimdor zone, an area
    // inside the Deadmines and one on map 17, which Map.dbc does not list (the real AreaTable has two such areas; vmangos keeps them).
    private static TempDbc CreateFiles(bool badParent)
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-map-area-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string map = Path.Combine(dir, "Map.dbc");
        string area = Path.Combine(dir, "AreaTable.dbc");
        int[] dungeon = new int[20];
        dungeon[0] = 36; dungeon[2] = 1; dungeon[4] = 35; dungeon[19] = 1581;
        // String block offsets: 1 "Azeroth", 9 "Kalimdor", 18 "Eastern Kingdoms", 35 "Deadmines".
        WriteDbc(map, 42, [[0, 1, 0, 0, 18], [1, 9, 0, 0, 9], dungeon], ["", "Azeroth", "Kalimdor", "Eastern Kingdoms", "Deadmines"]);
        WriteDbc(area, 25,
            [
                [1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [2, 0, badParent ? 99 : 1, 2, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [3, 1, 0, 3, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [4, 36, 0, 4, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                [5, 17, 0, 5, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            ], ["", "Zone", "Child", "Kalimdor", "Foreign"]);
        return new TempDbc(map, area, dir);
    }

    private static TempDbc CreateCycleFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-map-area-cycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string map = Path.Combine(dir, "Map.dbc");
        string area = Path.Combine(dir, "AreaTable.dbc");
        WriteDbc(map, 42, [[0, 1, 0, 0, 1], [1, 2, 0, 0, 1]], ["", "Azeroth", "Kalimdor"]);
        WriteDbc(area, 25,
            [
                [1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [22, 451, 49, 22, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                [49, 0, 22, 49, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            ], ["", "Zone", "Child", "Foreign"]);
        return new TempDbc(map, area, dir);
    }

    private static void WriteDbc(string path, int fieldCount, IReadOnlyList<int[]> rows, IReadOnlyList<string> strings)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        byte[] block = System.Text.Encoding.UTF8.GetBytes(string.Join("\0", strings) + "\0");
        writer.Write("WDBC"u8.ToArray()); writer.Write(rows.Count); writer.Write(fieldCount);
        writer.Write(fieldCount * 4); writer.Write(block.Length);
        foreach (int[] row in rows)
        {
            if (row.Length > fieldCount) throw new InvalidDataException("test row exceeds DBC field count");
            for (int i = 0; i < fieldCount; i++) writer.Write(i < row.Length ? row[i] : 0);
        }
        writer.Write(block);
    }

    private sealed record TempDbc(string Map, string Area, string Directory) : IDisposable
    {
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
