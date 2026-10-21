using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Maps;

/// <summary>
/// <c>wmo_area_template</c> from WMOAreaTable.dbc (world schema <see cref="WmoAreaDataModule.Version"/>): the reader's columns, the importer
/// that fills the table beside <c>area_template</c> (the route the area table takes: a client DBC through the content importer, never rows
/// in the repository), and the store that hands the rows to the world.
/// </summary>
public sealed class WmoAreaDbcImporterTests
{
    // WMOAreaTable.dbc rows (20 fields): id, WMOID, NameSetID, WMOGroupID, 4 sound columns, field 8 IntroSound, flags (9), AreaTableID (10).
    private static readonly int[][] Rows =
    [
        Row(id: 100, root: 1500, adt: 3, group: 77, flags: 0x20, area: 3),
        Row(id: 101, root: 1500, adt: 3, group: -1, flags: 0, area: 0),
        Row(id: 102, root: 1500, adt: 4, group: 12, flags: 0, area: 4),
    ];

    private static int[] Row(int id, int root, int adt, int group, int flags, int area)
    {
        int[] row = new int[20];
        row[0] = id; row[1] = root; row[2] = adt; row[3] = group; row[9] = flags; row[10] = area;
        return row;
    }

    [Fact]
    public void Reader_TakesTheKeyFlagsAndArea_IncludingANegativeGroup()
    {
        using TempFiles files = Create();
        IReadOnlyList<WmoAreaTemplateRow> rows = WmoAreaTableDbcReader.Read(files.Wmo);

        Assert.Equal(3, rows.Count);
        Assert.Equal((100u, 1500, 3, 77, 3u, 0x20u), (rows[0].Id, rows[0].RootId, rows[0].AdtId, rows[0].GroupId, rows[0].AreaId, rows[0].Flags));
        Assert.Equal(-1, rows[1].GroupId);
        Assert.Equal(0u, rows[1].AreaId);
    }

    [Fact]
    public void Reader_RefusesAWrongLayout_AndADuplicateId()
    {
        using TempFiles files = Create();
        string wrong = Path.Combine(files.Directory, "wrong.dbc");
        WriteDbc(wrong, 19, [[1]]);
        Assert.Throws<InvalidDataException>(() => WmoAreaTableDbcReader.Read(wrong));

        string dup = Path.Combine(files.Directory, "dup.dbc");
        WriteDbc(dup, 20, [Row(5, 1, 0, 0, 0, 0), Row(5, 2, 0, 0, 0, 0)]);
        Assert.Throws<InvalidDataException>(() => WmoAreaTableDbcReader.Read(dup));
    }

    [Fact]
    public async Task Import_FillsTheTable_ReplacesWhatWasThere_AndTheStoreReadsItBack()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<WmoAreaTemplateRow>().Add(new WmoAreaTemplateRow { Id = 9, RootId = 9, AreaId = 9 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using TempFiles files = Create();
        MapAreaDbcImportReport report = await MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, instances: null, replace: true,
            wmoAreaPath: files.Wmo);

        Assert.Equal((2, 4, 3), (report.MappedMaps, report.MappedAreas, report.MappedWmoAreas));
        uint[] ids = await db.Set<WmoAreaTemplateRow>().OrderBy(r => r.Id).Select(r => r.Id).ToArrayAsync();
        Assert.Equal([100u, 101u, 102u], ids);

        MapContent content = await new EfMapDataStore(db).LoadAsync();
        Assert.Equal(3, content.WmoAreas.Count);
        Assert.Equal(new WmoAreaTemplate(100, 1500, 3, 77, 3, 0x20), content.WmoAreas[0]);
        Assert.Equal(new WmoAreaTemplate(101, 1500, 3, -1, 0, 0), content.WmoAreas[1]);
    }

    [Fact]
    public async Task AWorldWithOnlyWmoAreaRows_RefusesAnImportThatCarriesThem_UnlessReplace()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<WmoAreaTemplateRow>().Add(new WmoAreaTemplateRow { Id = 9, RootId = 9, AreaId = 9 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using TempFiles files = Create();
        MapAreaDbcSnapshot withWmo = MapAreaDbcImporter.ReadSnapshot(files.Map, files.Area, instances: null, files.Wmo);
        MapAreaDbcSnapshot withoutWmo = MapAreaDbcImporter.ReadSnapshot(files.Map, files.Area, instances: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => MapAreaDbcImporter.ImportSnapshotAsync(db, withWmo, replace: false, default));
        Assert.Equal(0, await db.Set<MapTemplateRow>().CountAsync());

        // A snapshot without WMO rows does not touch the table, so its rows are no reason to refuse.
        await MapAreaDbcImporter.ImportSnapshotAsync(db, withoutWmo, replace: false, default);
        Assert.Equal(2, await db.Set<MapTemplateRow>().CountAsync());
    }

    [Fact]
    public async Task WithoutAWmoAreaTable_TheTableIsLeftAsItIs()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using WorldDbContext db = Context(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<WmoAreaTemplateRow>().Add(new WmoAreaTemplateRow { Id = 9, RootId = 9, AreaId = 9 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using TempFiles files = Create();
        MapAreaDbcImportReport report = await MapAreaDbcImporter.ImportAsync(db, files.Map, files.Area, replace: true);

        Assert.Equal(0, report.MappedWmoAreas);
        uint[] ids = await db.Set<WmoAreaTemplateRow>().Select(r => r.Id).ToArrayAsync();
        Assert.Equal([9u], ids);
    }

    [Fact]
    public void AnAreaAbsentFromTheAreaTable_IsKeptAndWarnedAbout()
    {
        using TempFiles files = Create();
        MapAreaDbcSnapshot snapshot = MapAreaDbcImporter.ReadSnapshot(files.Map, files.Area, instances: null, files.Wmo);

        Assert.Equal(3, snapshot.WmoAreas!.Count);
        // Area 4 is in AreaTable.dbc; make a row whose area is not.
        string orphan = Path.Combine(files.Directory, "orphan.dbc");
        WriteDbc(orphan, 20, [Row(1, 1, 0, 0, 0, 777), Row(2, 1, 0, 1, 0, 3)]);
        MapAreaDbcSnapshot withOrphan = MapAreaDbcImporter.ReadSnapshot(files.Map, files.Area, instances: null, orphan);
        Assert.Equal(2, withOrphan.WmoAreas!.Count);
        Assert.Contains(withOrphan.Warnings, w => w.Contains("777", StringComparison.Ordinal) && w.Contains("WMOAreaTable.dbc", StringComparison.Ordinal));
    }

    [Fact]
    public void TheModule_IsDiscovered_AtItsAllocatedWorldVersion()
    {
        IDataModule module = Assert.Single(DataModules.All, m => m is WmoAreaDataModule);
        Assert.Equal(DatabaseComponent.World, module.Component);
        Assert.Equal(WmoAreaDataModule.Version, module.SchemaVersion);
        Assert.Contains(WorldDbContext.Schema.Steps, s => s.Version == WmoAreaDataModule.Version);
        Assert.True(WorldDbContext.Schema.CurrentVersion >= WmoAreaDataModule.Version);
    }

    private static WorldDbContext Context(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite(connection).Options);

    // Map.dbc: the continents. AreaTable.dbc: areas 1 to 4. WMOAreaTable.dbc: Rows.
    private static TempFiles Create()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-wmo-area-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string map = Path.Combine(dir, "Map.dbc");
        string area = Path.Combine(dir, "AreaTable.dbc");
        string wmo = Path.Combine(dir, "WMOAreaTable.dbc");
        WriteDbc(map, 42, [[0, 1, 0, 0, 18], [1, 9, 0, 0, 9]], "", "Azeroth", "Kalimdor", "Eastern Kingdoms");
        WriteDbc(area, 25,
            [
                [1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [2, 0, 1, 2, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [3, 1, 0, 3, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0],
                [4, 0, 0, 4, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            ], "", "Zone", "Child", "Kalimdor");
        WriteDbc(wmo, 20, Rows);
        return new TempFiles(map, area, wmo, dir);
    }

    private static void WriteDbc(string path, int fieldCount, IReadOnlyList<int[]> rows, params string[] strings)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        byte[] block = System.Text.Encoding.UTF8.GetBytes(string.Join("\0", strings.Length == 0 ? new[] { "" } : strings) + "\0");
        writer.Write("WDBC"u8.ToArray()); writer.Write(rows.Count); writer.Write(fieldCount);
        writer.Write(fieldCount * 4); writer.Write(block.Length);
        foreach (int[] row in rows)
        {
            for (int i = 0; i < fieldCount; i++) writer.Write(i < row.Length ? row[i] : 0);
        }

        writer.Write(block);
    }

    private sealed record TempFiles(string Map, string Area, string Wmo, string Directory) : IDisposable
    {
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
