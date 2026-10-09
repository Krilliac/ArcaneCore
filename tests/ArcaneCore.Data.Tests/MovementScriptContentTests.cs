using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class MovementScriptContentTests
{
    [Fact]
    public async Task World43_UpgradesAnExistingPathAndStoresTheNewContent()
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions cs = await databases.CreateAsync(DatabaseProvider.Sqlite);
        SchemaDefinition prefix = new()
        {
            Component = WorldDbContext.Schema.Component,
            CurrentVersion = 42,
            Version1Tables = WorldDbContext.Schema.Version1Tables,
            Steps = [.. WorldDbContext.Schema.Steps.Where(s => s.Version <= 42)],
        };
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, prefix);
            // Prefix creation uses today's EF model; remove the two v43 columns to represent an actual v42 database.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE creature_movement DROP COLUMN ScriptId");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE creature_movement_template DROP COLUMN ScriptId");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO creature_movement (SpawnGuid, Point, X, Y, Z, Orientation, WaitTimeMs, Run) VALUES (1, 1, 2, 3, 4, 0, 0, 0)");
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(0u, (await db.Set<CreatureMovementRow>().SingleAsync()).ScriptId);
            db.Add(new CreatureMovementScriptRow { Id = 367802, Command = 1, DataLong = 25 });
            db.Add(new SpellScriptTargetRow { SpellId = 8283, Type = 1, TargetEntry = 4781 });
            db.Add(new CreatureLinkRow { SlaveGuid = 13991, MasterGuid = 13990, Flags = 515 });
            db.Add(new CreatureTemplateLinkRow { SlaveEntry = 390, MapId = 0, MasterEntry = 330, Flags = 515 });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.Set<CreatureMovementScriptRow>().CountAsync());
            Assert.Equal(1, await db.Set<SpellScriptTargetRow>().CountAsync());
            Assert.Equal(1, await db.Set<CreatureLinkRow>().CountAsync());
            Assert.Equal(1, await db.Set<CreatureTemplateLinkRow>().CountAsync());

            db.Add(new CreatureMovementTemplateRow { Entry = 390, PathId = 0, Point = 1, X = 5 });
            await db.SaveChangesAsync();
            var refresh = new CreatureDumpImporter();
            refresh.Read(new StringReader("""
                INSERT INTO `creature_movement` (`Id`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`) VALUES
                (1,1,99,3,4,0,0,367802);
                INSERT INTO `creature_movement_template` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`) VALUES
                (390,0,1,99,3,4,0,0,367803);
                """));
            await refresh.RefreshMovementScriptIdsAsync(db);
            db.ChangeTracker.Clear();
            CreatureMovementRow spawnPoint = await db.Set<CreatureMovementRow>().SingleAsync();
            CreatureMovementTemplateRow entryPoint = await db.Set<CreatureMovementTemplateRow>().SingleAsync();
            Assert.Equal((2f, 367802u), (spawnPoint.X, spawnPoint.ScriptId));
            Assert.Equal((5f, 367803u), (entryPoint.X, entryPoint.ScriptId));
        }
    }

    [ClassicDbDumpFact]
    public void RealClassicDb_ContainsTheNaralexStepsScriptTargetsAndFollowLinks()
    {
        string path = Environment.GetEnvironmentVariable("ARCANECORE_CLASSICDB_DUMP")!;
        using FileStream file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        var importer = new CreatureDumpImporter();
        importer.Read(reader);

        Assert.Equal([367801u, 367802u, 367803u], importer.PathSnapshot()
            .Where(r => r.Entry == 0 && r.PathId == (CreatureContent.WaypointPathBit | 3678u) && r.ScriptId != 0)
            .Select(r => r.ScriptId).Order());
        Assert.All(new uint[] { 367801, 367802, 367803 }, id =>
            Assert.Contains(importer.DbScripts.Scripts, s => s.Kind == DbScriptKind.CreatureMovement && s.Step.Id == id));
        Assert.Contains(importer.SpellScriptTargets, r => r.SpellId == 8283 && r.Type == 1 && r.TargetEntry == 4781);
        Assert.Contains(importer.SpellScriptTargets, r => r.SpellId == 10252 && r.TargetEntry == 7076);
        Assert.Contains(importer.CreatureLinks, r => r.SlaveGuid == 13991 && r.MasterGuid == 13990 && (r.Flags & 0x200) != 0);
        Assert.Contains(importer.CreatureTemplateLinks, r => r.SlaveEntry == 390 && r.MasterEntry == 330 && (r.Flags & 0x200) != 0);
    }

    private sealed class ClassicDbDumpFactAttribute : FactAttribute
    {
        public ClassicDbDumpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARCANECORE_CLASSICDB_DUMP")))
                Skip = "Set ARCANECORE_CLASSICDB_DUMP to the z2815 SQL dump.";
        }
    }
    [Fact]
    public void ClassicDbMovementSpellAndLinkRows_AreCaptured()
    {
        const string dump = """
            INSERT INTO `creature_movement` (`Id`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`) VALUES
            (13991,1,1,2,3,0,0,367802);
            INSERT INTO `creature_movement_template` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`) VALUES
            (390,0,1,1,2,3,0,0,367803);
            INSERT INTO `spell_script_target` (`entry`,`type`,`targetEntry`,`inverseEffectMask`) VALUES
            (8283,1,4781,0),(10252,1,7076,0),(10258,1,10120,0);
            INSERT INTO `creature_linking` (`guid`,`master_guid`,`flag`) VALUES (13991,13990,515);
            INSERT INTO `creature_linking_template` (`entry`,`map`,`master_entry`,`flag`,`search_range`) VALUES (390,0,330,515,20);
            """;
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump));

        Assert.Equal(367802u, Assert.Single(importer.Snapshot().Movement).ScriptId);
        Assert.Equal(367803u, Assert.Single(importer.PathSnapshot()).ScriptId);
        Assert.Equal([8283u, 10252u, 10258u], importer.SpellScriptTargets.Select(r => r.SpellId).Order());
        Assert.Equal((13991u, 13990u, 515u), importer.CreatureLinks.Select(r => (r.SlaveGuid, r.MasterGuid, r.Flags)).Single());
        Assert.Equal((390u, 330u), importer.CreatureTemplateLinks.Select(r => (r.SlaveEntry, r.MasterEntry)).Single());
    }
}
