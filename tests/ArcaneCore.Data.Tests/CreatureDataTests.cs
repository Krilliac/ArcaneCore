using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The creature tables (world schema v2 via <see cref="CreatureDataModule"/>), the store and the
/// cmangos/vmangos dump importer. Dump snippets are hand-written in each source's column layout
/// (cmangos mangos.sql, vmangos ObjectMgr queries); no GPL rows are copied.
/// </summary>
public sealed class CreatureDataTests : IAsyncLifetime
{
    private const string CMangosDump = """
        -- MySQL dump (cmangos classic-db layout)
        CREATE TABLE `creature_template` (
          `Entry` mediumint(8) unsigned NOT NULL DEFAULT '0',
          `Name` char(100) NOT NULL DEFAULT '',
          PRIMARY KEY (`Entry`)
        ) ENGINE=MyISAM;
        /*!40000 ALTER TABLE `creature_template` DISABLE KEYS */;
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`DisplayId1`,`DisplayId2`,`DisplayIdProbability1`,`DisplayIdProbability2`,`Scale`,`Faction`,`NpcFlags`,`UnitFlags`,`CreatureTypeFlags`,`CreatureType`,`Family`,`Rank`,`UnitClass`,`Civilian`,`RacialLeader`,`SpeedWalk`,`SpeedRun`,`MinLevelHealth`,`MaxLevelHealth`,`MinLevelMana`,`MaxLevelMana`,`Armor`,`MinMeleeDmg`,`MaxMeleeDmg`,`MeleeBaseAttackTime`,`PetSpellDataId`,`MovementType`,`CorpseDecay`,`ExtraFlags`,`ScriptName`) VALUES
        (900001,'Test Wolf','',2,3,903,904,50,50,0,32,0,0,1,1,1,0,1,0,0,1,1.14286,55,71,0,0,20,1.5,2.5,2000,0,1,0,0,'npc_test_wolf'),
        (900002,'Guard O\'Brien','Town \"Watch\"',55,55,3167,0,0,0,1.1,11,3,4096,0,7,0,1,1,1,0,1,1.14286,3052,3052,0,0,3000,80,100,2000,0,0,600,64,'');
        INSERT INTO `creature` VALUES (1,900001,0,1,-8900.5,-110.25,83.75,1.5,300,420,5,1),(2,900002,0,1,-8910,-120,84,0,120,120,0,2);
        INSERT INTO `creature_movement` (`Id`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES (2,1,-8911,-121,84,100,0,0,NULL),(2,2,-8920,-130,84,3.1,5000,0,'look around');
        INSERT INTO `creature_model_info` (`modelid`,`bounding_radius`,`combat_reach`,`gender`,`modelid_other_gender`) VALUES (3167,0.306,1.5,0,0);
        INSERT INTO `creature_addon` (`guid`,`mount`,`stand_state`,`sheath_state`,`emote`,`moveflags`,`auras`) VALUES (2,2410,0,1,0,0,NULL);
        """;

    private const string CMangosCreatureColumns = """
        CREATE TABLE `creature` (
          `guid` int(10) unsigned NOT NULL AUTO_INCREMENT,
          `id` mediumint(8) unsigned NOT NULL DEFAULT '0',
          `map` smallint(5) unsigned NOT NULL DEFAULT '0',
          `spawnMask` tinyint(3) unsigned NOT NULL DEFAULT '1',
          `position_x` DECIMAL(40,20) NOT NULL DEFAULT '0',
          `position_y` DECIMAL(40,20) NOT NULL DEFAULT '0',
          `position_z` DECIMAL(40,20) NOT NULL DEFAULT '0',
          `orientation` DECIMAL(40,20) NOT NULL DEFAULT '0',
          `spawntimesecsmin` int(10) unsigned NOT NULL DEFAULT '120',
          `spawntimesecsmax` int(10) unsigned NOT NULL DEFAULT '120',
          `spawndist` float NOT NULL DEFAULT '5',
          `MovementType` tinyint(3) unsigned NOT NULL DEFAULT '0',
          PRIMARY KEY (`guid`)
        ) ENGINE=MyISAM;
        """;

    private const string VMangosDump = """
        INSERT INTO `creature_classlevelstats` (`level`,`class`,`melee_damage`,`ranged_damage`,`attack_power`,`ranged_attack_power`,`health`,`base_health`,`mana`,`base_mana`,`armor`) VALUES
        (10,1,10,8,30,4,200,180,0,0,400),(12,1,12,9,34,5,240,210,0,0,480),(10,8,7,6,10,2,150,140,300,250,200);
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`display_id1`,`display_probability1`,`faction`,`npc_flags`,`speed_walk`,`speed_run`,`rank`,`unit_class`,`type`,`pet_family`,`static_flags1`,`static_flags2`,`health_multiplier`,`mana_multiplier`,`armor_multiplier`,`damage_multiplier`,`damage_variance`,`base_attack_time`,`ranged_attack_time`,`movement_type`,`flags_extra`) VALUES
        (900010,0,'Old Name','',10,12,1000,100,14,0,1,1.14286,0,1,1,0,0,0,1,1,1,1,0.14,2000,2000,0,0),
        (900010,5,'New Name','Sub',10,12,1001,100,14,0,1,1.14286,0,1,1,1,16,8,1.5,1,0.5,2,0.1,1800,1900,1,64),
        (900010,11,'Too New','',10,12,1002,100,14,0,1,1.14286,0,1,1,0,0,0,1,1,1,1,0.14,2000,2000,0,0),
        (900011,0,'Caster','',10,10,1003,100,14,0,1,1.14286,0,8,7,0,32,0,1,2,1,1,0.14,2000,2000,0,0),
        (900012,0,'No Stats','',40,40,1004,100,14,0,1,1.14286,0,1,7,0,0,0,1,1,1,1,0.14,2000,2000,0,0);
        INSERT INTO `creature` (`guid`,`id`,`id2`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecsmin`,`spawntimesecsmax`,`wander_distance`,`movement_type`,`patch_min`,`patch_max`) VALUES
        (10,900010,0,1,1,2,3,0,60,90,4,1,0,10),(11,900010,0,1,1,2,3,0,60,90,4,1,0,4),(12,900011,900010,1,5,6,7,0,30,30,0,0,8,10),(13,900011,0,1,5,6,7,0,30,30,0,0,11,11);
        INSERT INTO `creature_display_info_addon` (`display_id`,`build`,`bounding_radius`,`combat_reach`,`gender`,`display_id_other_gender`) VALUES (1001,4222,0.5,1.5,1,0),(1001,5875,0.6,1.7,1,1003),(1001,6005,9,9,9,9);
        INSERT INTO `creature_addon` (`guid`,`display_id`,`mount_display_id`,`equipment_id`,`stand_state`,`sheath_state`,`emote_state`,`auras`) VALUES (10,0,-1,0,8,1,10,NULL);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void WorldSchema_Version2_IsTheCreatureTables()
    {
        Assert.True(WorldDbContext.Schema.CurrentVersion >= CreatureDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureDataModule.Version);
        Assert.Equal(CreatureDataModule.Tables, step.Changes.Cast<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RefreshScriptNames_ChangesOnlyThatColumn_AndIsIdempotent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`Name`,`ScriptName`) VALUES (900001,'Dump Name','npc_script');"));
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 900001, Name = "Kept Name", AIName = "EventAI" });
        await db.SaveChangesAsync();

        Assert.Equal(1, await importer.RefreshScriptNamesAsync(db));
        Assert.Equal(0, await importer.RefreshScriptNamesAsync(db));
        CreatureTemplateRow row = await db.Set<CreatureTemplateRow>().SingleAsync();
        Assert.Equal("npc_script", row.ScriptName);
        Assert.Equal("Kept Name", row.Name);
        Assert.Equal("EventAI", row.AIName);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshWorldDatabase_ImportAndLoad_RoundTrip(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(CMangosCreatureColumns + CMangosDump));

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            CreatureImportReport report = await importer.WriteAsync(db, replace: false);
            Assert.Equal((CreatureDumpDialect.CMangos, 2, 2, 2, 1, 1, 0), (report.Dialect, report.Templates, report.Spawns, report.Waypoints, report.Models, report.Addons, report.SkippedSpawns));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            Assert.Equal((2, 2), (content.TemplateCount, content.SpawnCount));

            CreatureTemplate wolf = content.FindTemplate(900001)!;
            Assert.Equal("npc_test_wolf", wolf.ScriptName);
            Assert.Equal(("Test Wolf", (byte)2, (byte)3), (wolf.Name, wolf.MinLevel, wolf.MaxLevel));
            Assert.Equal([903u, 904u, 0u, 0u], wolf.DisplayIds);
            Assert.Equal([50u, 50u, 0u, 0u], wolf.DisplayProbabilities);
            Assert.Equal((32u, 1u, 55u, 71u, 1.5f, 2.5f), (wolf.Faction, wolf.TypeFlags, wolf.MinLevelHealth, wolf.MaxLevelHealth, wolf.MinMeleeDamage, wolf.MaxMeleeDamage));

            CreatureTemplate guard = content.FindTemplate(900002)!;
            Assert.Equal("Guard O'Brien", guard.Name);
            Assert.Equal("Town \"Watch\"", guard.SubName);
            Assert.Equal((1.1f, 3u, 4096u, 1u, true, 600u, 64u), (guard.Scale, guard.NpcFlags, guard.UnitFlags, guard.Rank, guard.Civilian, guard.CorpseDecaySeconds, guard.ExtraFlags));

            CreatureSpawn spawn = Assert.Single(content.GetSpawns(0), s => s.Guid == 1);
            Assert.Equal((900001u, -8900.5f, -110.25f, 83.75f, 300u, 420u, 5f, (byte)1),
                (spawn.Entry, spawn.X, spawn.Y, spawn.Z, spawn.SpawnTimeMinSeconds, spawn.SpawnTimeMaxSeconds, spawn.WanderDistance, spawn.MovementType));

            IReadOnlyList<CreatureWaypoint> path = content.GetWaypoints(2);
            Assert.Equal([1u, 2u], path.Select(p => p.Point));
            Assert.Equal((-8920f, 3.1f, 5000u), (path[1].X, path[1].Orientation, path[1].WaitTimeMs));
            Assert.Equal(new CreatureModelInfo(3167, 0.306f, 1.5f, 0, 0), content.FindModel(3167));
            Assert.Equal(new CreatureAddon(2, 2410, 0, 1, 0), content.FindAddon(2));
        }

        // A replace import empties the tables first.
        var second = new CreatureDumpImporter();
        second.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (5,'Only',1,1);"));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await second.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            Assert.Equal((1, 0), (content.TemplateCount, content.SpawnCount));
            Assert.Null(content.FindModel(3167));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorldVersion1Database_UpgradesWithCreatureTables(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldV1Context v1 = TestContexts.Create<WorldV1Context>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(v1, WorldV1Context.Schema);
            v1.Set<RaceInfoRow>().Add(new RaceInfoRow { Race = 1, Gender = 0, DisplayId = 49, FactionTemplate = 1 });
            await v1.SaveChangesAsync();
        }

        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
        Assert.Equal(49u, (await db.RaceInfo.SingleAsync()).DisplayId);

        db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 1, Name = "Upgraded" });
        db.Set<CreatureSpawnRow>().Add(new CreatureSpawnRow { Guid = 1, Entry = 1, MapId = 0 });
        await db.SaveChangesAsync();
        CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
        Assert.Equal("Upgraded", content.FindTemplate(1)!.Name);
        Assert.Single(content.GetSpawns(0));
    }

    [Fact]
    public void VMangosImport_PicksPatchRows_DerivesStats_AndFiltersBuilds()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(VMangosDump));
        var (templates, spawns, _, models, addons) = importer.Snapshot();
        CreatureImportReport report = importer.BuildReport();
        Assert.Equal(CreatureDumpDialect.VMangos, importer.Dialect);

        // Highest patch ≤ 10 wins (patch 11 is a later client).
        CreatureTemplateRow t = Assert.Single(templates, r => r.Entry == 900010);
        Assert.Equal(("New Name", "Sub", 1001u, 1u), (t.Name, t.SubName, t.DisplayId1, t.MovementType));

        // static_flags1 TAMEABLE (0x10) → type flag 0x01; static_flags2 0x08 → 0x40.
        Assert.Equal(0x41u, t.TypeFlags);

        // creature_classlevelstats × multipliers: health 200/240 × 1.5, armor 480 × 0.5, damage 12 × 2 ± 10 %.
        Assert.Equal((300u, 360u, 240u), (t.MinLevelHealth, t.MaxLevelHealth, t.Armor));
        Assert.Equal(24f * 0.9f, t.MinMeleeDamage, 3);
        Assert.Equal(24f * 1.1f, t.MaxMeleeDamage, 3);
        Assert.Equal((1800u, 1900u, 64u), (t.MeleeBaseAttackTime, t.RangedBaseAttackTime, t.ExtraFlags));

        CreatureTemplateRow caster = Assert.Single(templates, r => r.Entry == 900011);
        Assert.Equal((600u, 600u), (caster.MinLevelMana, caster.MaxLevelMana));
        Assert.Equal(0x100u, caster.UnitFlags); // IMMUNE_TO_PC → UNIT_FLAG_IMMUNE_TO_PLAYER

        CreatureTemplateRow noStats = Assert.Single(templates, r => r.Entry == 900012);
        Assert.Equal((1u, 1u), (noStats.MinLevelHealth, noStats.MaxLevelHealth));
        Assert.Contains(report.Warnings, w => w.Contains("900012", StringComparison.Ordinal));

        // Spawns whose patch range contains 10; id2 becomes a creature_spawn_entry row (see CreatureSpawnEntryTests).
        Assert.Equal([10u, 12u], spawns.Select(s => s.Guid).Order());
        CreatureSpawnRow s10 = spawns.Single(s => s.Guid == 10);
        Assert.Equal((60u, 90u, 4f, (byte)1), (s10.SpawnTimeMinSeconds, s10.SpawnTimeMaxSeconds, s10.WanderDistance, s10.MovementType));
        Assert.Equal(2, report.SkippedSpawns);
        Assert.Equal([(12u, 900010u), (12u, 900011u)], importer.SpawnEntrySnapshot().OrderBy(e => e.Entry).Select(e => (e.SpawnGuid, e.Entry)));

        // Display info: the newest build ≤ 5875.
        CreatureModelInfoRow model = Assert.Single(models);
        Assert.Equal((0.6f, 1.7f, (byte)1, 1003u), (model.BoundingRadius, model.CombatReach, model.Gender, model.DisplayIdOtherGender));

        // mount_display_id -1 means "keep"; stored as no mount.
        CreatureAddonRow addon = Assert.Single(addons);
        Assert.Equal((0u, (byte)8, (byte)1, 10u), (addon.MountDisplayId, addon.StandState, addon.SheathState, addon.EmoteState));
    }

    [Fact]
    public void VMangosImport_RetainsPerDisplayScaleOverrides()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("INSERT INTO creature_template (entry,patch,name,level_min,level_max,display_id1,display_scale1,display_id2,display_scale2,display_id3,display_scale3,display_id4,display_scale4) VALUES (990001,0,'Scale test',1,1,101,0.8,102,1.2,103,1.4,104,1.6);"));
        var (templates, _, _, _, _) = importer.Snapshot();
        CreatureTemplateRow row = Assert.Single(templates);
        Assert.Equal((0.8f, 1.2f, 1.4f, 1.6f), (row.Scale, row.DisplayScale2, row.DisplayScale3, row.DisplayScale4));
    }

    [Fact]
    public async Task SqliteSchema23_ImportsAndReloadsAllFourDisplayScalesWithoutLosingPriorRows()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, SchemaProbe.ThroughVersion(WorldDbContext.Schema, 22));
            db.PlayerCreateInfo.Add(new PlayerCreateInfoRow { Race = 1, Class = 1, MapId = 0, X = 23 });
            await db.SaveChangesAsync();
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(23f, (await db.PlayerCreateInfo.SingleAsync()).X);
            db.ChangeTracker.Clear();
            var importer = new CreatureDumpImporter();
            importer.Read(new StringReader("INSERT INTO creature_template (entry,patch,name,level_min,level_max,display_id1,display_scale1,display_id2,display_scale2,display_id3,display_scale3,display_id4,display_scale4) VALUES (990001,0,'Scale test',1,1,101,0.8,102,1.2,103,1.4,104,1.6);"));
            await importer.WriteAsync(db, replace: false);
        }

        await using WorldDbContext reloaded = TestContexts.Create<WorldDbContext>(cs);
        CreatureContent content = await new EfCreatureDataStore(reloaded).LoadAsync();
        Assert.Equal([0.8f, 1.2f, 1.4f, 1.6f], content.FindTemplate(990001)!.DisplayScales);
        Assert.Equal(23f, (await reloaded.PlayerCreateInfo.SingleAsync()).X);
    }

    [Fact]
    public void Importer_RejectsMixedDialects()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(CMangosCreatureColumns + CMangosDump));
        Assert.Throws<FormatException>(() => importer.Read(new StringReader(VMangosDump)));
    }

    [Fact]
    public void DumpReader_HandlesEscapesNullsCommentsAndColumnLists()
    {
        const string dump = """
            -- comment line
            # hash comment
            /* block
               comment */
            CREATE TABLE `t` (
              `a` int NOT NULL, -- trailing comment
              `b` varchar(20) DEFAULT NULL,
              `c` float,
              PRIMARY KEY (`a`),
              KEY `idx` (`b`)
            ) ENGINE=InnoDB;
            INSERT INTO `t` VALUES (1,'semi;colon',1.5),(2,NULL,-2e3);
            REPLACE INTO t (`c`, `a`, `b`) VALUES (0.25, 3, 'it''s \\ \n'), (1, 4, '(paren), comma');
            INSERT INTO `other` (`x`) VALUES ('a\'b');
            """;
        var reader = new MySqlDumpReader(new StringReader(dump));
        List<object> items = [.. reader.Read()];

        DumpTable table = Assert.IsType<DumpTable>(items[0]);
        Assert.Equal("t", table.Name);
        Assert.Equal(["a", "b", "c"], table.Columns);

        DumpRow[] rows = [.. items.OfType<DumpRow>()];
        Assert.Equal(5, rows.Length);
        Assert.Equal(["1", "semi;colon", "1.5"], rows[0].Values);
        Assert.True(rows[1].TryGet(out string? b, "B"));
        Assert.Null(b);
        Assert.Equal("-2e3", rows[1].Values[2]);

        Assert.True(rows[2].TryGet(out string? text, "b"));
        Assert.Equal("it's \\ \n", text);
        Assert.True(rows[2].TryGet(out string? a, "missing", "a"));
        Assert.Equal("3", a);
        Assert.Equal("(paren), comma", rows[3].Values[2]);
        Assert.Equal(("other", "a'b"), (rows[4].Table, rows[4].Values[0]));
        Assert.False(rows[4].Has("y"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

/// <summary>The world database exactly as M5/M6 mapped it: world schema version 1, no creature tables.</summary>
internal sealed class WorldV1Context(DbContextOptions<WorldV1Context> options) : DbContext(options)
{
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "world",
        CurrentVersion = 1,
        Version1Tables = ["player_create_info", "race_info", "class_info"],
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);
        modelBuilder.Entity<PlayerCreateInfoRow>(entity =>
        {
            entity.ToTable("player_create_info");
            entity.HasKey(r => new { r.Race, r.Class });
        });
        modelBuilder.Entity<RaceInfoRow>(entity =>
        {
            entity.ToTable("race_info");
            entity.HasKey(r => new { r.Race, r.Gender });
        });
        modelBuilder.Entity<ClassInfoRow>(entity =>
        {
            entity.ToTable("class_info");
            entity.HasKey(r => r.Class);
        });
    }
}
