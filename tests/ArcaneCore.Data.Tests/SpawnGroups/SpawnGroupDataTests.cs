using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Tests.WorldState;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.SpawnGroups;

/// <summary>
/// <see cref="SpawnGroupDataModule"/> (world 43) and <see cref="SpawnGroupDumpImporter"/>: <c>gameobject_spawn_entry</c> and the cmangos
/// spawn group tables. <see cref="Excerpt"/> holds classic-db z2815 rows as the dump has them (the dump is GPL data and is not committed;
/// these rows are fixtures, as the other real-row tests keep theirs).
/// </summary>
public sealed class SpawnGroupDataTests : IAsyncLifetime
{
    /// <summary>
    /// Groups 1 (Musty Tome, game objects), 2 (Kargath Expeditionary Force, flags 3, a formation), 21 (Western Plaguelands ore, MaxCount 1),
    /// 44 (Balgaras the Foul, MaxCount 1) and 19008 (AQ war Qiraji Majors, condition 2099), with their spawns, entries and formation rows,
    /// a few of their <c>creature</c>/<c>gameobject</c> rows, and the <c>gameobject_spawn_entry</c> rows of spawns 11427 and 11434.
    /// </summary>
    public const string Excerpt = """
        INSERT INTO `creature` (`guid`,`id`,`map`,`spawnMask`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecsmin`,`spawntimesecsmax`,`spawndist`,`MovementType`) VALUES
        (6877,9085,0,1,-6687.94,-2159.12,244.195,3.9968,300,300,0,0),
        (11000,0,0,1,-2834.99,-2870.69,32.6675,3.8757,300,300,2,1),
        (155391,0,1,1,-5750.83,-3489.55,-57.25,0,600,900,10,1),
        (155404,0,1,1,-6114.33,-3942.42,-58.62,0,600,900,10,1);
        INSERT INTO `gameobject` (`guid`,`id`,`map`,`spawnMask`,`position_x`,`position_y`,`position_z`,`orientation`,`rotation0`,`rotation1`,`rotation2`,`rotation3`,`spawntimesecsmin`,`spawntimesecsmax`) VALUES
        (11427,0,0,1,-6118.81,-2911.51,210.97,-2.37365,0,0,0.927184,-0.374607,300,600),
        (45459,0,0,1,1382.42,-1449.85,56.969,1.64061,0,0,0.731354,0.681998,60,60),
        (78606,0,0,1,1682.83,-1010.24,79.0426,4.62512,0,0,-0.737277,0.675591,600,1500);
        INSERT INTO `gameobject_spawn_entry` (`guid`,`entry`) VALUES (11427,126049),(11427,128293),(11434,126049),(11434,128293);
        INSERT INTO `spawn_group` (`Id`,`Name`,`Type`,`MaxCount`,`WorldState`,`WorldStateExpression`,`Flags`,`StringId`) VALUES
        (1,'Western Plaguelands (Ruins of Andorhal) - Musty Tome (176150,176151)',1,0,0,0,0,0),
        (2,'Kargath Expeditionary Force c.entry 9082,9083,9084,9085,9086 & Linked to 9077 for RP',0,0,0,0,3,0),
        (21,'Western Plaguelands - Mithril Deposit | Gold Vein | Truesilver Deposit (1) Ore 000',1,1,0,0,0,0),
        (44,'Wetlands - Balgaras the Foul (1) Wandering 000',0,1,0,0,0,0),
        (19008,'AQ War Effort (10 Hour War) - Qiraji Major He\'al-ie 15816 & Qiraji Major 15750 (4)',0,0,2099,0,0,0);
        INSERT INTO `spawn_group_entry` (`Id`,`Entry`,`MinCount`,`MaxCount`,`Chance`) VALUES
        (1,176150,0,1,0),(1,176151,0,9,0),(21,1734,0,0,5),(21,2040,0,0,0),(21,2047,0,0,5),(19008,15750,0,0,0),(19008,15816,1,1,0);
        INSERT INTO `spawn_group_formation` (`Id`,`FormationType`,`FormationSpread`,`FormationOptions`,`PathId`,`MovementType`,`Comment`) VALUES
        (2,4,4,0,6883,2,'Kargath Expeditionary Force c.entry 9082,9083,9084,9085,9086 & Linked to 9077 for RP');
        INSERT INTO `spawn_group_spawn` (`Id`,`Guid`,`SlotId`,`Chance`) VALUES
        (1,45459,-1,0),(1,45460,-1,0),(1,45461,-1,0),(1,45462,-1,0),(1,45463,-1,0),(1,45464,-1,0),(1,45465,-1,0),(1,45466,-1,0),(1,45467,-1,0),(1,45468,-1,0),
        (2,6877,3,0),(2,6880,4,0),(2,6883,0,0),(2,6885,2,0),(2,6886,1,0),
        (21,78606,-1,0),(21,78609,-1,0),(21,78612,-1,0),(21,78615,-1,0),(21,78618,-1,0),
        (44,11000,0,0),(44,11001,1,0),(44,11002,2,0),(44,11003,3,0),(44,11004,4,0),(44,11005,5,0),(44,11006,6,0),
        (19008,155391,-1,0),(19008,155404,-1,0),(19008,155417,-1,0),(19008,155703,-1,0);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static SpawnGroupDumpImporter Read(string dump)
    {
        var importer = new SpawnGroupDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void WorldStep_IsTheSixSpawnGroupTables()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == SpawnGroupDataModule.Version);
        Assert.Equal(
            ["gameobject_spawn_entry", "spawn_group", "spawn_group_spawn", "spawn_group_entry", "spawn_group_formation", "spawn_group_linked_group"],
            step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Empty(step.Changes.OfType<AddColumnChange>());
        Assert.True(SpawnGroupDataModule.Version > DbScriptDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is SpawnGroupDataModule);
    }

    [Fact]
    public void TheExcerpt_IsReadByColumnName()
    {
        SpawnGroupDumpImporter importer = Read(Excerpt);

        SpawnGroupImportReport report = importer.BuildReport();
        Assert.Equal((4, 5, 31, 7, 1, 0), (report.GameObjectSpawnEntries, report.Groups, report.Spawns, report.Entries, report.Formations, report.LinkedGroups));
        Assert.Empty(report.Warnings);
        SpawnGroupRow qiraji = importer.GroupSnapshot().Single(g => g.Id == 19008);
        Assert.Equal((0u, 0u, 2099u, 0u), (qiraji.Type, qiraji.MaxCount, qiraji.WorldState, qiraji.Flags));
        Assert.Equal("AQ War Effort (10 Hour War) - Qiraji Major He'al-ie 15816 & Qiraji Major 15750 (4)", qiraji.Name);
        Assert.Equal(-1, importer.SpawnSnapshot().Single(s => s.Guid == 45459).SlotId);
        Assert.Equal(3, importer.SpawnSnapshot().Single(s => s.Guid == 6877).SlotId);
        SpawnGroupEntryRow healie = importer.EntrySnapshot().Single(e => e.Entry == 15816);
        Assert.Equal((1u, 1u, 0u), (healie.MinCount, healie.MaxCount, healie.Chance));
        Assert.Equal(6883u, importer.FormationSnapshot().Single().PathId);
        Assert.Equal(0u, importer.DumpCreatureEntries[155391]);
        Assert.Equal(9085u, importer.DumpCreatureEntries[6877]);
        Assert.Equal(0u, importer.DumpGameObjectEntries[11427]);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Rows_RoundTripThroughTheStores_AsCatalogsByType(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Read(Excerpt).WriteAsync(db, replace: false);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        GameObjectContent objects = await new EfGameObjectDataStore(read).LoadAsync();
        CreatureContent creatures = await new EfCreatureDataStore(read).LoadAsync();

        Assert.Equal([126049u, 128293u], objects.GetSpawnEntries(11427));
        Assert.Equal([126049u, 128293u], objects.GetSpawnEntries(11434));
        Assert.Empty(objects.GetSpawnEntries(45459));
        Assert.Equal([1u, 21u], objects.SpawnGroups.Groups.Select(g => g.Id).Order());
        Assert.Equal([2u, 44u, 19008u], creatures.SpawnGroups.Groups.Select(g => g.Id).Order());

        SpawnGroupDefinition ore = objects.SpawnGroups.GroupOf(SpawnGroupType.GameObject, 78612)!;
        Assert.Equal((21u, 1u), (ore.Id, ore.MaxCount));
        Assert.Equal([(1734u, 5u), (2040u, 0u), (2047u, 5u)], ore.RandomEntries.Select(e => (e.Entry, e.Chance)));
        Assert.Null(objects.SpawnGroups.GroupOf(SpawnGroupType.Creature, 78612)); // the guid space is per type

        SpawnGroupDefinition kargath = creatures.SpawnGroups.Find(2)!;
        Assert.Equal(SpawnGroupFlags.AggroTogether | SpawnGroupFlags.RespawnTogether, kargath.Flags);
        Assert.Equal((byte)4, kargath.Formation!.FormationType);
        Assert.Equal([6877u, 6880u, 6883u, 6885u, 6886u], kargath.Members.Select(m => m.Guid));
        Assert.Equal(2099u, creatures.SpawnGroups.Find(19008)!.WorldStateCondition);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AReplaceImport_EmptiesTheSixTables(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Read(Excerpt).WriteAsync(db, replace: false);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await Read("INSERT INTO `spawn_group` (`Id`,`Name`,`Type`) VALUES (7,'Only',0);").WriteAsync(db, replace: true);
        }

        await using WorldDbContext check = TestContexts.Create<WorldDbContext>(cs);
        Assert.Equal([7u], await check.Set<SpawnGroupRow>().Select(g => g.Id).ToListAsync());
        Assert.Equal(0, await check.Set<GameObjectSpawnEntryRow>().CountAsync());
        Assert.Equal(0, await check.Set<SpawnGroupSpawnRow>().CountAsync());
        Assert.Equal(0, await check.Set<SpawnGroupEntryRow>().CountAsync());
        Assert.Equal(0, await check.Set<SpawnGroupFormationRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Refresh_FillsEmptyTables_OnlyForTheWorldsOwnSpawns_AndASecondRunChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureSpawnRow>().AddRange(
                new CreatureSpawnRow { Guid = 6877, Entry = 9085, MapId = 0 },   // group 2, the dump's entry
                new CreatureSpawnRow { Guid = 11000, Entry = 0, MapId = 0 },     // group 44
                new CreatureSpawnRow { Guid = 155391, Entry = 0, MapId = 1 },    // group 19008
                new CreatureSpawnRow { Guid = 155404, Entry = 777, MapId = 1 }); // another spawn under a dump guid: not a member
            db.Set<GameObjectSpawnRow>().AddRange(
                new GameObjectSpawnRow { Guid = 11427, Entry = 0, MapId = 0 },
                new GameObjectSpawnRow { Guid = 78606, Entry = 0, MapId = 0 });
            await db.SaveChangesAsync();
        }

        SpawnGroupFillReport first;
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            first = await Read(Excerpt).FillAsync(db);
        }

        // 11434 is no spawn of this world; group 1's tomes are none of its objects; 155404 is another object; 155417/155703 do not exist.
        Assert.Equal(2, first.GameObjectSpawnEntries);
        Assert.Equal((4, 4, 5, 1, 0), (first.Groups!.Value, first.Spawns, first.Entries, first.Formations, first.LinkedGroups));
        Assert.Equal(31 - 4, first.SkippedMembers);
        await using (WorldDbContext check = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Equal([2u, 21u, 44u, 19008u], await check.Set<SpawnGroupRow>().OrderBy(g => g.Id).Select(g => g.Id).ToListAsync());
            Assert.Equal([6877u, 11000u, 78606u, 155391u], (await check.Set<SpawnGroupSpawnRow>().Select(s => s.Guid).ToListAsync()).Order());
            Assert.Equal([126049u, 128293u], (await check.Set<GameObjectSpawnEntryRow>().Select(r => r.Entry).ToListAsync()).Order());
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            SpawnGroupFillReport second = await Read(Excerpt).FillAsync(db);
            Assert.Null(second.GameObjectSpawnEntries);
            Assert.Null(second.Groups);
        }

        await using WorldDbContext after = TestContexts.Create<WorldDbContext>(cs);
        Assert.Equal(4, await after.Set<SpawnGroupRow>().CountAsync());
        Assert.Equal(4, await after.Set<SpawnGroupSpawnRow>().CountAsync());
        Assert.Equal(2, await after.Set<GameObjectSpawnEntryRow>().CountAsync());
    }

    /// <summary>
    /// The whole z2815 dump (env-gated): the six tables' rows, and how its 2,802 creature and 3,614 game object spawns with <c>id</c> 0
    /// get an entry with a template: through <c>*_spawn_entry</c> or their spawn group's entries, all but two creatures that have no entry in
    /// cmangos either.
    /// </summary>
    [ClassicDbDumpFact]
    public void ClassicDb_z2815_EveryEntryZeroSpawnResolves()
    {
        var importer = new SpawnGroupDumpImporter();
        using (var reader = new StreamReader(new GZipStream(File.OpenRead(ClassicDbDumpFactAttribute.DumpPath), CompressionMode.Decompress)))
        {
            importer.Read(reader);
        }

        HashSet<uint> creatureTemplates = [];
        HashSet<uint> objectTemplates = [];
        var creatureSpawnEntryRows = new List<(uint Guid, uint Entry)>();
        using (var reader = new StreamReader(new GZipStream(File.OpenRead(ClassicDbDumpFactAttribute.DumpPath), CompressionMode.Decompress)))
        {
            foreach (object item in new MySqlDumpReader(reader).Read())
            {
                if (item is not DumpRow row)
                {
                    continue;
                }

                switch (row.Table)
                {
                    case "creature_template" when row.TryGet(out string? entry, "Entry"):
                        creatureTemplates.Add(uint.Parse(entry!, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "gameobject_template" when row.TryGet(out string? entry, "entry"):
                        objectTemplates.Add(uint.Parse(entry!, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "creature_spawn_entry" when row.TryGet(out string? guid, "guid") && row.TryGet(out string? entry, "entry"):
                        creatureSpawnEntryRows.Add((uint.Parse(guid!, System.Globalization.CultureInfo.InvariantCulture),
                            uint.Parse(entry!, System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                }
            }
        }

        SpawnGroupImportReport report = importer.BuildReport();
        Assert.Equal((5001, 1040, 3772, 1781, 164, 0), (report.GameObjectSpawnEntries, report.Groups, report.Spawns, report.Entries, report.Formations, report.LinkedGroups));

        SpawnGroupCatalog groups = SpawnGroupStore.Build(importer.GroupSnapshot(), importer.SpawnSnapshot(), importer.EntrySnapshot(), importer.FormationSnapshot(), importer.LinkSnapshot());
        ILookup<uint, uint> creatureSpawnEntries = creatureSpawnEntryRows.ToLookup(r => r.Guid, r => r.Entry);
        uint[] zeroCreatures = [.. importer.DumpCreatureEntries.Where(p => p.Value == 0).Select(p => p.Key)];
        Assert.Equal(2802, zeroCreatures.Length);
        int bySpawnEntry = zeroCreatures.Count(g => creatureSpawnEntries[g].Any(creatureTemplates.Contains));
        int byGroup = zeroCreatures.Count(g => !creatureSpawnEntries[g].Any()
            && groups.GroupOf(SpawnGroupType.Creature, g) is { } group && group.RandomEntries.Any(e => creatureTemplates.Contains(e.Entry)));
        Assert.Equal((2234, 566), (bySpawnEntry, byGroup));

        // The last two are the members of group 19980 "Stormwind - Battleground Emissary x2 - Patrol", which has no spawn_group_entry rows:
        // they have no entry in cmangos either (its condition 19998 is "any classic Battleground Emissary Event Active").
        Assert.Equal([11559u, 11560u], zeroCreatures.Where(g => !creatureSpawnEntries[g].Any()
            && groups.GroupOf(SpawnGroupType.Creature, g) is not { RandomEntries.Count: > 0 }).Order());
        Assert.Equal(19980u, groups.GroupOf(SpawnGroupType.Creature, 11559)!.Id);

        ILookup<uint, uint> objectSpawnEntries = importer.SpawnEntrySnapshot().ToLookup(r => r.SpawnGuid, r => r.Entry);
        uint[] zeroObjects = [.. importer.DumpGameObjectEntries.Where(p => p.Value == 0).Select(p => p.Key)];
        Assert.Equal(3614, zeroObjects.Length);
        int objectsBySpawnEntry = zeroObjects.Count(g => objectSpawnEntries[g].Any(objectTemplates.Contains));
        int objectsByGroup = zeroObjects.Count(g => !objectSpawnEntries[g].Any()
            && groups.GroupOf(SpawnGroupType.GameObject, g) is { } group && group.RandomEntries.Any(e => objectTemplates.Contains(e.Entry)));
        Assert.Equal((1809, 1805), (objectsBySpawnEntry, objectsByGroup));
    }
}
