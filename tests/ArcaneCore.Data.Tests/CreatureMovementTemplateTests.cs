using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <c>creature_movement_template</c> (<see cref="CreatureMovementTemplateDataModule"/>): the waypoint paths a creature entry walks when
/// its spawn has no <c>creature_movement</c> rows of its own (mangos-classic MotionGenerators/WaypointManager.h:69-93, vmangos
/// Movement/WaypointManager.h:77-93: the guid path wins, then the entry path). Rows are hand-written in the classic-db column layout
/// (<c>Entry, PathId, Point, PositionX/Y/Z, Orientation, WaitTime, ScriptId, Comment</c>); no GPL rows are copied. Classic-db has 15,402
/// such rows on 544 paths, and 319 of its 2,898 waypoint spawns have no path of their own.
/// </summary>
public sealed class CreatureMovementTemplateTests : IAsyncLifetime
{
    private const string Dump = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`,`MovementType`) VALUES (920101,'Entry Walker','',5,5,32,2);
        INSERT INTO `creature` (`guid`,`id`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecsmin`,`spawntimesecsmax`,`spawndist`,`MovementType`) VALUES
        (920101,920101,0,10,20,30,1.5,300,300,0,2),(920102,920101,0,50,60,30,1.5,300,300,0,2);
        INSERT INTO `creature_movement` (`Id`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
        (920102,1,1,2,3,100,0,3,'own path with a node script');
        INSERT INTO `creature_movement_template` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
        (920101,0,1,100,200,30,100,0,0,'first'),
        (920101,0,2,110,200,30,0,5000,12,'second, scripted'),
        (920101,0,5,120,210,30,1.5,0,0,'fifth: point ids need not be contiguous'),
        (920101,1,1,7,7,7,100,0,0,'a second path of the same entry');
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static CreatureDumpImporter Read(string dump)
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void WorldStep_IsTheMovementTemplateTable()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureMovementTemplateDataModule.Version);
        Assert.Equal(["creature_movement_template"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Empty(step.Changes.OfType<AddColumnChange>());
        Assert.True(WorldDbContext.Schema.CurrentVersion >= CreatureMovementTemplateDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureMovementTemplateDataModule);
    }

    [Fact]
    public void Importer_KeepsEveryPathOfTheEntry_AndThePointIdsAsTheyAre()
    {
        CreatureDumpImporter importer = Read(Dump);

        CreatureMovementTemplateRow[] rows = [.. importer.PathSnapshot().OrderBy(r => r.PathId).ThenBy(r => r.Point)];

        Assert.Equal(4, rows.Length);
        Assert.Equal([(0u, 1u), (0u, 2u), (0u, 5u), (1u, 1u)], rows.Select(r => (r.PathId, r.Point)));
        CreatureMovementTemplateRow second = rows[1];
        Assert.Equal((920101u, 110f, 200f, 30f, 0f, 5000u), (second.Entry, second.X, second.Y, second.Z, second.Orientation, second.WaitTimeMs));
        Assert.Equal(100f, rows[0].Orientation); // 100 is "keep the travel direction", kept verbatim
        Assert.Equal(4, importer.BuildReport().MovementTemplates);
    }

    [Fact]
    public void Importer_ReportsTheNodeScriptsItCannotRun_InsteadOfDroppingThemSilently()
    {
        CreatureImportReport report = Read(Dump).BuildReport();

        // One scripted node in creature_movement (ScriptId 3) and one in creature_movement_template (ScriptId 12).
        string warning = Assert.Single(report.Warnings, w => w.Contains("ScriptId", StringComparison.Ordinal));
        Assert.Contains("2 waypoint node", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ADumpWithoutThePathTable_ReportsZeroTemplateRows()
    {
        Assert.Equal(0, Read("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (5,'Only',1,1);").BuildReport().MovementTemplates);
    }

    /// <summary>
    /// With <c>ARCANECORE_CLASSICDB_DUMP</c> set to the classic-db z2815 dump this imports the real paths and checks the figures the design
    /// was sized on (skipped when the variable is unset; fails when it is set and the file is missing).
    /// </summary>
    [ClassicDbDumpFact]
    public async Task RealClassicDbDump_EntryPaths_CoverTheWaypointSpawnsWithoutOwnRows()
    {
        string path = Environment.GetEnvironmentVariable(ClassicDbDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(path), $"{ClassicDbDumpFactAttribute.Variable} is set but '{path}' does not exist");
        var importer = new CreatureDumpImporter();
        await using FileStream file = File.OpenRead(path);
        await using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        importer.Read(reader);

        CreatureImportReport report = importer.BuildReport();
        // The store also holds script_waypoint (1,523 rows) and waypoint_path (5,393 rows, under entry 0) in their own path namespaces.
        Assert.Equal((22318, 1523, 5393), (report.MovementTemplates, report.ScriptWaypoints, report.WaypointPaths));
        const uint namespaceBits = CreatureContent.ScriptWaypointPathBit | CreatureContent.WaypointPathBit;
        CreatureMovementTemplateRow[] rows = [.. importer.PathSnapshot().Where(r => (r.PathId & namespaceBits) == 0)];
        Assert.Equal(15402, rows.Length);
        Assert.Equal(544, rows.Select(r => (r.Entry, r.PathId)).Distinct().Count());
        Assert.Equal(479, rows.Select(r => r.Entry).Distinct().Count());
        Assert.Equal(51, rows.Where(r => r.PathId > 0).Select(r => r.Entry).Distinct().Count());
        Assert.Contains(report.Warnings, w => w.Contains("ScriptId", StringComparison.Ordinal));

        // 319 of the 2,898 waypoint spawns have no creature_movement rows but do have an entry path.
        var (_, spawns, movement, _, _) = importer.Snapshot();
        HashSet<uint> withOwnPath = [.. movement.Select(m => m.SpawnGuid)];
        HashSet<uint> entriesWithPath = [.. rows.Where(r => r.PathId == 0).Select(r => r.Entry)];
        CreatureSpawnRow[] waypointSpawns = [.. spawns.Where(s => s.MovementType == 2)];
        Assert.Equal(2898, waypointSpawns.Length);
        Assert.Equal(319, waypointSpawns.Count(s => !withOwnPath.Contains(s.Guid) && entriesWithPath.Contains(s.Entry)));
    }

    /// <summary>
    /// The real z2815 snapshot has the quest and gossip scripts this lane must preserve, plus Corporal Keeshan's
    /// script_waypoint path (ScriptDev2 redridge_mountains.cpp npc_corporal_keeshan_escortAI).
    /// </summary>
    [ClassicDbDumpFact]
    public void RealClassicDbDump_QuestGossipEventScriptsAndEscortPathAreRead()
    {
        string path = Environment.GetEnvironmentVariable(ClassicDbDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(path), $"{ClassicDbDumpFactAttribute.Variable} is set but '{path}' does not exist");
        var importer = new DbScriptDumpImporter();
        using TextReader reader = ArcaneCore.Data.Content.Import.DumpFiles.OpenText(path);
        importer.Read(reader);

        Assert.Contains(importer.Scripts, row => row.Kind == DbScriptKind.QuestStart && row.Step.Id == 68
            && row.Step.Command == 10 && row.Step.DataLong == 2044);
        Assert.Contains(importer.Scripts, row => row.Kind == DbScriptKind.QuestStart && row.Step.Id == 68
            && row.Step.Command == 26 && row.Step.DelayMs == 3000);
        Assert.Contains(importer.Scripts, row => row.Kind == DbScriptKind.QuestStart && row.Step.Id == 74
            && row.Step.Command == 10 && row.Step.DataLong == 2044);
        Assert.Contains(importer.Scripts, row => row.Kind == DbScriptKind.QuestEnd && row.Step.Id == 112
            && row.Step.Command == 0);
        Assert.Contains(importer.Scripts, row => row.Kind == DbScriptKind.Gossip && row.Step.Id == 21
            && row.Step.Command == 7 && row.Step.DataLong == 6981);
        Assert.Contains(importer.Scripts, row => row.Kind == DbScriptKind.Event && row.Step.Id == 364
            && row.Step.Command == 10 && row.Step.DataLong == 2624);
        Assert.Contains(importer.ScriptWaypoints, row => row.Entry == 349 && row.PathId == 0
            && row.Point.Point == 1 && row.Point.X == -8769.59f);
    }

    private sealed class ClassicDbDumpFactAttribute : FactAttribute
    {
        public const string Variable = "ARCANECORE_CLASSICDB_DUMP";

        public ClassicDbDumpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            {
                Skip = $"Set {Variable} to the classic-db z2815 dump (.sql or .sql.gz) to import the real data.";
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Paths_RoundTripThroughTheStore_AndTheGuidPathWinsOverTheEntryPath(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            CreatureImportReport report = await Read(Dump).WriteAsync(db, replace: false);
            Assert.Equal(4, report.MovementTemplates);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        CreatureContent content = await new EfCreatureDataStore(read).LoadAsync();

        // Spawn 920101 has no creature_movement rows: the entry's default path (PathId 0), ordered by point id, not renumbered.
        CreatureWaypointPath fromEntry = content.ResolveWaypointPath(920101, 920101);
        Assert.Equal(CreatureWaypointOrigin.Entry, fromEntry.Origin);
        Assert.Equal([1u, 2u, 5u], fromEntry.Points.Select(p => p.Point));
        Assert.Equal((110f, 200f, 5000u), (fromEntry.Points[1].X, fromEntry.Points[1].Y, fromEntry.Points[1].WaitTimeMs));
        Assert.Equal(1.5f, fromEntry.Points[2].Orientation);

        // Spawn 920102 has its own rows: they win.
        CreatureWaypointPath own = content.ResolveWaypointPath(920102, 920101);
        Assert.Equal(CreatureWaypointOrigin.Guid, own.Origin);
        Assert.Single(own.Points);

        // The second path of the entry is stored but is not the default one.
        Assert.Equal([1u], content.GetEntryWaypoints(920101, pathId: 1).Select(p => p.Point));

        // Nothing for an entry without paths.
        Assert.Equal(CreatureWaypointOrigin.None, content.ResolveWaypointPath(1, 99).Origin);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AReplaceImport_RemovesThePreviousTemplatePaths(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Read(Dump).WriteAsync(db, replace: false);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await Read("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (5,'Only',1,1);").WriteAsync(db, replace: true);
        }

        await using WorldDbContext check = TestContexts.Create<WorldDbContext>(cs);
        Assert.Equal(0, await check.Set<CreatureMovementTemplateRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromThePreviousVersion_AddsTheTable_KeepsTheRows_AndRerunsCleanly(DatabaseProvider provider)
    {
        // MariaDB commits DDL implicitly (a refused step keeps earlier DDL), PostgreSQL DDL is transactional: either way a second
        // EnsureAsync over the finished schema must change nothing.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int previous = CreatureMovementTemplateDataModule.Version - 1;
        SchemaDefinition prefix = new()
        {
            Component = WorldDbContext.Schema.Component,
            CurrentVersion = previous,
            Version1Tables = WorldDbContext.Schema.Version1Tables,
            Steps = [.. WorldDbContext.Schema.Steps.Where(s => s.Version <= previous)],
        };

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, prefix);
            db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 77, Name = "Before" });
            await db.SaveChangesAsync();
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal("Before", (await db.Set<CreatureTemplateRow>().SingleAsync()).Name);

            // The new table takes rows keyed by (Entry, PathId, Point).
            db.Set<CreatureMovementTemplateRow>().Add(new CreatureMovementTemplateRow { Entry = 77, PathId = 0, Point = 1 + (uint)pass, WaitTimeMs = 5 });
            await db.SaveChangesAsync();
            Assert.Equal(1 + pass, await db.Set<CreatureMovementTemplateRow>().CountAsync());
        }
    }
}
