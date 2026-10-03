using System.Buffers.Binary;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The game object and loot tables (world schema v7 via <see cref="GameObjectLootDataModule"/>),
/// the EF stores and the cmangos/vmangos dump importer. Dump snippets are hand-written in each
/// source's column layout; no GPL rows are copied.
/// </summary>
public sealed class GameObjectLootDataTests : IAsyncLifetime
{
    private const string CMangosDump = """
        -- cmangos classic-db layout
        INSERT INTO `gameobject_template` (`entry`,`type`,`displayId`,`name`,`faction`,`flags`,`size`,`data0`,`data1`,`data2`,`data3`,`data4`,`data5`,`data6`,`data7`,`data8`,`data9`,`data10`,`data11`,`data12`,`data13`,`data14`,`data15`,`data16`,`data17`,`data18`,`data19`,`data20`,`data21`,`data22`,`data23`) VALUES
        (900100,3,259,'Test Chest',0,0,1.5,57,900100,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,23),
        (900101,0,411,'Test \'Door\'',114,32,1,0,0,3000,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0);
        INSERT INTO `gameobject` (`guid`,`id`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`rotation0`,`rotation1`,`rotation2`,`rotation3`,`spawntimesecs`,`animprogress`,`state`) VALUES
        (1,900100,0,-8900.5,-110.25,83.75,1.5,0,0,0.68,0.73,180,100,1),(2,900101,1,10,20,30,0,0,0,0,0,-600,255,0);
        INSERT INTO `gameobject_questrelation` (`id`,`quest`) VALUES (900100,77);
        INSERT INTO `gameobject_involvedrelation` (`id`,`quest`) VALUES (900100,78),(900100,79);
        INSERT INTO `creature_template` (`Entry`,`Name`,`LootId`,`SkinningLootId`,`MinLootGold`,`MaxLootGold`) VALUES (900001,'Wolf',900001,900002,5,12),(900002,'Nothing',0,0,0,0);
        INSERT INTO `creature_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES
        (900001,117,40,0,1,2,0),(900001,750,-35,0,1,1,0),(900001,0,5,0,-900003,2,0),(900001,2589,0,1,1,1,4);
        INSERT INTO `gameobject_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES (900100,2589,100,0,1,3,0);
        INSERT INTO `item_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES (5523,117,100,0,2,2,0);
        INSERT INTO `skinning_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES (900002,2318,100,0,1,1,0);
        INSERT INTO `reference_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES (900003,4306,0,1,1,1,0);
        """;

    private const string VmangosDump = """
        -- vmangos layout: patch-versioned templates and rows, renamed loot columns
        INSERT INTO `gameobject_template` (`entry`,`patch`,`type`,`displayId`,`name`,`faction`,`flags`,`size`,`data0`,`data1`) VALUES
        (900200,0,3,1,'Old Chest',0,0,1,0,1),(900200,7,3,2,'Patched Chest',0,0,1,0,2),(900200,11,3,3,'TBC Chest',0,0,1,0,3);
        INSERT INTO `gameobject` (`guid`,`id`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`rotation0`,`rotation1`,`rotation2`,`rotation3`,`spawntimesecsmin`,`spawntimesecsmax`,`animprogress`,`state`,`patch_min`,`patch_max`) VALUES
        (20,900200,0,1,2,3,0,0,0,0,0,30,60,100,1,0,10),(21,900200,0,1,2,3,0,0,0,0,0,30,60,100,1,0,4),(22,900200,0,1,2,3,0,0,0,0,0,30,60,100,1,11,11);
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`level_min`,`loot_id`,`skinning_loot_id`,`gold_min`,`gold_max`) VALUES (900010,0,'A',1,1,0,1,2),(900010,5,'B',1,2,0,3,4),(900010,11,'C',1,3,0,5,6);
        INSERT INTO `creature_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`,`patch_min`,`patch_max`) VALUES
        (2,117,50,0,1,1,0,0,10),(2,118,50,0,1,1,0,11,11);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void WorldSchema_Version7_IsTheGameObjectAndLootTables()
    {
        Assert.True(WorldDbContext.Schema.CurrentVersion >= GameObjectLootDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == GameObjectLootDataModule.Version);
        Assert.Equal(GameObjectLootDataModule.Tables, step.Changes.Cast<CreateTableChange>().Select(c => c.Table));
        Assert.Equal(11, GameObjectLootDataModule.Tables.Count);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is GameObjectLootDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshWorldDatabase_ImportAndLoad_RoundTrip(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader(CMangosDump));
        importer.ReadLocks(DbcFile.Parse(LockImage([57, 2, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 25, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0])));

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            GameObjectLootImportReport report = await importer.WriteAsync(db, replace: false);
            Assert.Equal((2, 2, 1, 2, 1, 8, 1, 0), (report.Templates, report.Spawns, report.QuestStarters, report.QuestEnders, report.Locks, report.LootRows, report.CreatureLootInfos, report.SkippedRows));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            GameObjectContent content = await new EfGameObjectDataStore(db).LoadAsync();
            Assert.Equal((2, 2, 1), (content.TemplateCount, content.SpawnCount, content.LockCount));
            GameObjectTemplate chest = content.FindTemplate(900100)!;
            Assert.Equal((3u, 259u, "Test Chest", 1.5f), (chest.Type, chest.DisplayId, chest.Name, chest.Size));
            Assert.Equal(57u, chest.GetData(0));
            Assert.Equal(900100u, chest.GetData(1));
            Assert.Equal(23u, chest.GetData(23));
            Assert.Equal(24, chest.Data.Count);
            GameObjectTemplate door = content.FindTemplate(900101)!;
            Assert.Equal(("Test 'Door'", 114u, 32u, 3000u), (door.Name, door.Faction, door.Flags, door.GetData(2)));

            GameObjectSpawn spawn = Assert.Single(content.GetSpawns(0));
            Assert.Equal((900100u, -8900.5f, -110.25f, 83.75f, 1.5f, 0.68f, 0.73f, 180, 100u, (byte)1),
                (spawn.Entry, spawn.X, spawn.Y, spawn.Z, spawn.Orientation, spawn.Rotation2, spawn.Rotation3, spawn.SpawnTimeSeconds, spawn.AnimProgress, spawn.State));
            GameObjectSpawn eventSpawn = Assert.Single(content.GetSpawns(1));
            Assert.Equal((-600, 255u, (byte)0), (eventSpawn.SpawnTimeSeconds, eventSpawn.AnimProgress, eventSpawn.State));
            Assert.Equal([0u, 1u], content.MapsWithSpawns.Order());
            Assert.Equal([77u], content.QuestStartersOf(900100));
            Assert.Equal([78u, 79u], content.QuestEndersOf(900100));

            LockEntry lck = content.FindLock(57)!;
            Assert.Equal((2u, 2u, 25u), (lck.Types[0], lck.Indexes[0], lck.Skills[0]));
            Assert.Equal(8, lck.Types.Count);
            Assert.Null(content.FindLock(0));

            LootContent loot = await new EfLootDataStore(db).LoadAsync();
            Assert.Equal(8, loot.RowCount);
            IReadOnlyList<LootStoreRow> wolf = loot.GetRows(LootTableKind.Creature, 900001);
            Assert.Equal(4, wolf.Count);
            Assert.Contains(wolf, r => r is { Item: 750, ChanceOrQuestChance: -35f });
            Assert.Contains(wolf, r => r is { Item: 0, MinCountOrRef: -900003, MaxCount: 2 });
            Assert.Contains(wolf, r => r is { Item: 2589, GroupId: 1, ConditionId: 4 });
            Assert.Equal(3u, Assert.Single(loot.GetRows(LootTableKind.GameObject, 900100)).MaxCount);
            Assert.Equal(2, Assert.Single(loot.GetRows(LootTableKind.Item, 5523)).MinCountOrRef);
            Assert.Single(loot.GetRows(LootTableKind.Skinning, 900002));
            Assert.Single(loot.GetRows(LootTableKind.Reference, 900003));
            Assert.Equal(new CreatureLootInfo(900001, 900001, 900002, 5, 12), loot.FindCreature(900001));
            Assert.Null(loot.FindCreature(900002)); // nothing to drop: no row
        }

        var second = new GameObjectLootDumpImporter();
        second.Read(new StringReader("INSERT INTO `gameobject_template` (`entry`,`type`,`name`) VALUES (5,19,'Mailbox');"));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await second.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            GameObjectContent content = await new EfGameObjectDataStore(db).LoadAsync();
            Assert.Equal((1, 0, 0), (content.TemplateCount, content.SpawnCount, content.LockCount));
            Assert.Equal(0, (await new EfLootDataStore(db).LoadAsync()).RowCount);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FailedImport_RollsBack_AndCallerTransactionIsProtectedBySavepoint(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var first = new GameObjectLootDumpImporter();
        first.Read(new StringReader(CMangosDump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await first.WriteAsync(db, replace: false);
        }

        // Importing the same rows again without replace violates the primary keys: nothing changes.
        var duplicate = new GameObjectLootDumpImporter();
        duplicate.Read(new StringReader(CMangosDump + "\nINSERT INTO `gameobject_template` (`entry`,`type`,`name`) VALUES (7,19,'New');"));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => duplicate.WriteAsync(db, replace: false));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            GameObjectContent content = await new EfGameObjectDataStore(db).LoadAsync();
            Assert.Equal(2, content.TemplateCount);
            Assert.Null(content.FindTemplate(7));
        }

        // Inside a caller's transaction a replace import is undone with the caller's rollback.
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var replacement = new GameObjectLootDumpImporter();
            replacement.Read(new StringReader("INSERT INTO `gameobject_template` (`entry`,`type`,`name`) VALUES (8,19,'Temp');"));
            await replacement.WriteAsync(db, replace: true);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Equal(2, (await new EfGameObjectDataStore(db).LoadAsync()).TemplateCount);
            db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow { Entry = 9, Name = "pending" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => new GameObjectLootDumpImporter().WriteAsync(db, replace: false));
        }
    }

    [Fact]
    public void Vmangos_PicksTheHighestPatchUpTo10_AndRowsWhosePatchRangeContains10()
    {
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader(VmangosDump));
        GameObjectLootImportReport report = importer.BuildReport();
        Assert.Equal((1, 1, 1, 1), (report.Templates, report.Spawns, report.LootRows, report.CreatureLootInfos));
        Assert.Equal(3, report.SkippedRows); // two spawns and one loot row outside patch 10
        Assert.Equal(20u, Assert.Single(importer.SpawnRows).Guid);
        Assert.Equal(30, Assert.Single(importer.SpawnRows).SpawnTimeSeconds);
        LootTemplateRowBase row = Assert.Single(importer.LootRows);
        Assert.Equal((2u, 117u), (row.Entry, row.Item));
    }

    [Fact]
    public void LockDbc_WithTheWrongLayout_IsRejected()
    {
        var importer = new GameObjectLootDumpImporter();
        Assert.Throws<InvalidDataException>(() => importer.ReadLocks(DbcFile.Parse(Image(32, new uint[32]))));
    }

    [Fact]
    public void Spawn_WithAGuidBeyondTheCounter_IsSkippedWithAWarning()
    {
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader("INSERT INTO `gameobject` (`guid`,`id`,`map`,`position_x`,`position_y`,`position_z`) VALUES (16777216,1,0,0,0,0),(5,1,0,0,0,0);"));
        GameObjectLootImportReport report = importer.BuildReport();
        Assert.Equal((1, 1), (report.Spawns, report.SkippedRows));
        Assert.Contains("16777216", Assert.Single(report.Warnings));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static byte[] LockImage(params uint[][] records) => Image(GameObjectLootDumpImporter.LockFieldCount, records);

    private static byte[] Image(int fields, params uint[][] records)
    {
        byte[] image = new byte[20 + records.Length * fields * 4 + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (record * fields + field) * 4), records[record][field]);
            }
        }

        return image;
    }
}
