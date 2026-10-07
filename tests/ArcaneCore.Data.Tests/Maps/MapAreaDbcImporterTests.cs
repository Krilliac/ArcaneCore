using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Maps;

public sealed class MapAreaDbcImporterTests
{
    [Fact]
    public async Task Replace_ImportsBothContinentsAndPreservesCustomOtherMapRows()
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
        MapAreaDbcImportReport report = await MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: true);

        Assert.Equal(2, report.MappedMaps);
        Assert.Equal(3, report.MappedAreas);
        Assert.Equal(1, report.SkippedAreas);
        uint[] mapIds = await db.Set<MapTemplateRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToArrayAsync();
        uint[] areaIds = await db.Set<AreaTemplateRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToArrayAsync();
        Assert.Equal([0u, 1u, 36u], mapIds);
        Assert.Equal([1u, 2u, 3u, 3600u], areaIds);
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
    public async Task Replace_AdmitsCrossMapAncestorWithoutAdmittingItsMap_AndReusesIt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<AreaTemplateRow>().Add(new AreaTemplateRow { Entry = 9000, MapId = 451, Name = "unrelated" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using TempDbc files = CreateCrossMapFiles();
        MapAreaDbcImportReport first = await MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: true);
        Assert.Equal((2, 4, 1), (first.MappedMaps, first.MappedAreas, first.SkippedAreas));
        uint[] mapIds = await db.Set<MapTemplateRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToArrayAsync();
        Assert.Equal([0u, 1u], mapIds);
        Assert.Equal(1, await db.Set<AreaTemplateRow>().CountAsync(r => r.Entry == 22));
        Assert.Equal(451u, await db.Set<AreaTemplateRow>().Where(r => r.Entry == 22).Select(r => r.MapId).SingleAsync());
        Assert.Equal(1, await db.Set<AreaTemplateRow>().CountAsync(r => r.Entry == 9000));

        db.ChangeTracker.Clear();
        MapAreaDbcImportReport second = await MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: true);
        Assert.Equal((2, 4, 1), (second.MappedMaps, second.MappedAreas, second.SkippedAreas));
        Assert.Equal(1, await db.Set<AreaTemplateRow>().CountAsync(r => r.Entry == 22));
        Assert.Equal(1, await db.Set<AreaTemplateRow>().CountAsync(r => r.Entry == 9000));
        Assert.Equal(22u, await db.Set<AreaTemplateRow>().Where(r => r.Entry == 49).Select(r => r.ZoneId).SingleAsync());
    }

    [Fact]
    public async Task IncompatibleExistingForeignAncestor_RollsBackWithoutSelectedWrites()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<MapTemplateRow>().Add(new MapTemplateRow { Entry = 36, MapName = "custom" });
        db.Set<AreaTemplateRow>().AddRange(
            new AreaTemplateRow { Entry = 22, MapId = 451, ZoneId = 99, Name = "incompatible" },
            new AreaTemplateRow { Entry = 9000, MapId = 451, Name = "unrelated" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using TempDbc files = CreateCrossMapFiles();
        await Assert.ThrowsAsync<InvalidDataException>(() => MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: true));
        uint[] mapIds = await db.Set<MapTemplateRow>().Select(r => r.Entry).ToArrayAsync();
        uint[] areaIds = await db.Set<AreaTemplateRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToArrayAsync();
        Assert.Equal([36u], mapIds);
        Assert.Equal([22u, 9000u], areaIds);
        Assert.Equal(99u, await db.Set<AreaTemplateRow>().Where(r => r.Entry == 22).Select(r => r.ZoneId).SingleAsync());
        Assert.Empty(await db.Set<AreaTemplateRow>().Where(r => r.MapId == 0 || r.MapId == 1).ToArrayAsync());
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

        using TempDbc files = CreateCrossMapFiles(cycle: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: true));
        uint[] mapIds = await db.Set<MapTemplateRow>().Select(r => r.Entry).ToArrayAsync();
        Assert.Equal([36u], mapIds);
        Assert.Empty(await db.Set<AreaTemplateRow>().ToArrayAsync());
    }

    private static WorldDbContext Context(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite(connection).Options);

    private static TempDbc CreateFiles(bool badParent)
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-map-area-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string map = Path.Combine(dir, "Map.dbc");
        string area = Path.Combine(dir, "AreaTable.dbc");
        WriteDbc(map, 42, [[0, 1, 0, 0, 0], [1, 2, 0, 0, 0]], ["", "Azeroth", "Kalimdor"]);
        WriteDbc(area, 25,
            [
                [1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [2, 0, badParent ? 99 : 1, 2, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [3, 1, 0, 3, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [4, 2, 0, 4, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            ], ["", "Zone", "Child", "Kalimdor", "Foreign"]);
        return new TempDbc(map, area, dir);
    }

    private static TempDbc CreateCrossMapFiles(bool cycle = false)
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-map-area-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string map = Path.Combine(dir, "Map.dbc");
        string area = Path.Combine(dir, "AreaTable.dbc");
        WriteDbc(map, 42, [[0, 1, 0, 0, 0], [1, 2, 0, 0, 0]], ["", "Azeroth", "Kalimdor"]);
        int parent = cycle ? 49 : 0;
        WriteDbc(area, 25,
            [
                [1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [2, 1, 0, 2, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [22, 451, cycle ? 49 : 0, 22, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                [49, 0, parent == 0 ? 22 : parent, 49, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                [9000, 451, 0, 9000, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
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
