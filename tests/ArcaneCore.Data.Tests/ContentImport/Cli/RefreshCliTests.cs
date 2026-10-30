using System.Text;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Battlegrounds;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.Procs;
using ArcaneCore.Data.World.Rest;
using ArcaneCore.Data.World.Transports;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// The content importer's <c>refresh</c> command: a world database an older importer built (creatures, portals) gains the tables the
/// current world reads (safe locations, battlegrounds, exploration XP, taverns, transports, proc events, relay scripts, area trigger
/// volumes) without its other tables changing, and a second run leaves the same rows. The rows are real classic-db z2815 tuples
/// (shortened); the DBCs are synthetic files with the build-5875 layouts. Driven in-process against SQLite files in the temp directory.
/// </summary>
public sealed class RefreshCliTests : IDisposable
{
    private const string Dump = """
        INSERT INTO `db_version` (`version`,`creature_ai_version`,`cache_id`,`required_z2815_01_mangos_spawn_group_formation`) VALUES ('Classic DB version 1.12.1 \"Melting Pot v2\". For Classic core z2815.','ACID',NULL,NULL);
        INSERT INTO `world_safe_locs` (`id`,`map`,`x`,`y`,`z`,`o`,`name`) VALUES (610,30,-818.557,-619.255,54.0389,2.02458,'Alterac Valley, Horde Safe'),(611,30,873.002,-491.284,96.5419,3.92699,'Alterac Valley, Alliance Safe'),(769,489,1519.53,1481.87,352.024,3.14159,'Warsong Gulch - Alliance Enter Loc'),(770,489,933.331,1433.72,345.536,0,'Warsong Gulch - Horde Enter Loc');
        INSERT INTO `game_graveyard_zone` (`id`,`ghost_loc`,`link_kind`,`faction`) VALUES (610,2597,0,67);
        INSERT INTO `battleground_template` (`id`,`MinPlayersPerTeam`,`MaxPlayersPerTeam`,`MinLvl`,`MaxLvl`,`AllianceStartLoc`,`HordeStartLoc`,`StartMaxDist`,`PlayerSkinReflootId`) VALUES (1,20,40,51,60,611,610,100,0),(2,5,10,10,60,769,770,75,0);
        INSERT INTO `battlemaster_entry` (`entry`,`bg_template`) VALUES (347,1),(2302,2);
        CREATE TABLE `exploration_basexp` (
          `level` tinyint NOT NULL DEFAULT '0',
          `basexp` mediumint NOT NULL DEFAULT '0',
          PRIMARY KEY (`level`)
        ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3 ROW_FORMAT=FIXED COMMENT='Exploration System';
        INSERT INTO `exploration_basexp` VALUES (0,0),(1,5),(60,660);
        INSERT INTO `areatrigger_tavern` (`id`,`name`) VALUES (71,'Westfall - Sentinel Hill Inn'),(562,'Elwynn Forest - Goldshire');
        INSERT INTO `transports` (`entry`,`name`,`period`) VALUES (176231,'Menethil Harbor and Theramore Isle',329313);
        INSERT INTO `spell_proc_event` (`entry`,`SchoolMask`,`SpellFamilyName`,`SpellFamilyMask0`,`SpellFamilyMask1`,`SpellFamilyMask2`,`procFlags`,`procEx`,`ppmRate`,`CustomChance`,`Cooldown`) VALUES (324,0,0,0,0,0,0,65536,0,0,3);
        INSERT INTO `instance_template` (`map`,`parent`,`levelMin`,`levelMax`,`maxPlayers`,`reset_delay`,`ghostEntranceMap`,`ghostEntranceX`,`ghostEntranceY`,`ScriptName`,`mountAllowed`) VALUES (36,0,17,26,10,0,0,-11207.8,1681.15,'instance_deadmines',1),(489,0,0,0,0,0,0,0,0,'',0);
        INSERT INTO `dbscripts_on_relay` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`) VALUES (19958,0,0,32,1,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'pause'),(19958,4000,0,32,0,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'unpause');
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-refresh-" + Guid.NewGuid().ToString("N"));

    public RefreshCliTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static WorldDbContext Open(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private const string WeatherOnlyDump = """
        CREATE TABLE `game_weather` (
          `zone` mediumint unsigned NOT NULL,
          `spring_rain_chance` tinyint unsigned NOT NULL,
          `spring_snow_chance` tinyint unsigned NOT NULL,
          `spring_storm_chance` tinyint unsigned NOT NULL,
          `summer_rain_chance` tinyint unsigned NOT NULL,
          `summer_snow_chance` tinyint unsigned NOT NULL,
          `summer_storm_chance` tinyint unsigned NOT NULL,
          `fall_rain_chance` tinyint unsigned NOT NULL,
          `fall_snow_chance` tinyint unsigned NOT NULL,
          `fall_storm_chance` tinyint unsigned NOT NULL,
          `winter_rain_chance` tinyint unsigned NOT NULL,
          `winter_snow_chance` tinyint unsigned NOT NULL,
          `winter_storm_chance` tinyint unsigned NOT NULL,
          PRIMARY KEY (`zone`)
        ) ENGINE=MyISAM;
        INSERT INTO `game_weather` VALUES (12,1,2,3,4,5,6,7,8,9,10,11,12);
        """;

    private async Task<string> SeededWorldAsync()
    {
        string world = await OldWorldAsync(withShip: false);
        await using WorldDbContext seed = Open(world);
        seed.Set<WorldSafeLocRow>().Add(new WorldSafeLocRow { Id = 5, MapId = 0, X = 1, Y = 2, Z = 3, Orientation = 1.5f, Name = "Local graveyard" });
        seed.Set<GraveyardZoneRow>().Add(new GraveyardZoneRow { Id = 5, GhostZone = 12, Team = 0 });
        seed.Set<ExplorationBaseXpRow>().Add(new ExplorationBaseXpRow { Level = 9, BaseXp = 99 });
        await seed.SaveChangesAsync();
        return world;
    }

    [Fact]
    public async Task Refresh_WithOnlyGraveyardLinks_LeavesTheWorldsSafeLocations()
    {
        string world = await SeededWorldAsync();
        string dump = PathOf("links.sql");
        File.WriteAllText(dump, "INSERT INTO `game_graveyard_zone` (`id`,`ghost_loc`,`link_kind`,`faction`) VALUES (610,2597,0,67);");

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world);

        Assert.True(code == ExitCodes.Ok, error + output);
        await using WorldDbContext db = Open(world);
        Assert.Equal([5u], await db.Set<WorldSafeLocRow>().Select(r => r.Id).ToListAsync());
        Assert.Equal([610u], await db.Set<GraveyardZoneRow>().Select(r => r.Id).ToListAsync());
    }

    [Fact]
    public async Task Refresh_WithOnlySafeLocations_LeavesTheWorldsGraveyardLinks()
    {
        string world = await SeededWorldAsync();
        string dump = PathOf("locs.sql");
        File.WriteAllText(dump, "INSERT INTO `world_safe_locs` (`id`,`map`,`x`,`y`,`z`,`o`,`name`) VALUES (610,30,1,2,3,2.5,'AV Horde');");

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world);

        Assert.True(code == ExitCodes.Ok, error + output);
        await using WorldDbContext db = Open(world);
        Assert.Equal([610u], await db.Set<WorldSafeLocRow>().Select(r => r.Id).ToListAsync());
        Assert.Equal([5u], await db.Set<GraveyardZoneRow>().Select(r => r.Id).ToListAsync());
    }

    [Fact]
    public async Task Refresh_WithOnlyWeather_LeavesTheWorldsExplorationBaseXp()
    {
        string world = await SeededWorldAsync();
        string dump = PathOf("weather.sql");
        File.WriteAllText(dump, WeatherOnlyDump);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world);

        Assert.True(code == ExitCodes.Ok, error + output);
        await using WorldDbContext db = Open(world);
        Assert.Equal([12u], await db.Set<GameWeatherRow>().Select(r => r.Zone).ToListAsync());
        Assert.Equal(99u, (await db.Set<ExplorationBaseXpRow>().SingleAsync(r => r.Level == 9)).BaseXp);
    }

    [Fact]
    public async Task Refresh_RestoresPortalRequirements_WithoutReplacingCustomPortalsOrDestinations()
    {
        string world = await OldWorldAsync(withShip: false);
        await using (WorldDbContext seed = Open(world))
        {
            seed.Set<AreaTriggerTeleportRow>().AddRange(
                new AreaTriggerTeleportRow { Id = 2848, Name = "local destination", TargetMap = 249 },
                new AreaTriggerTeleportRow { Id = 3528, Name = "local molten core", TargetMap = 409 },
                new AreaTriggerTeleportRow { Id = 9999, Name = "custom portal", RequiredItem = 42 });
            await seed.SaveChangesAsync();
        }

        string dump = PathOf("portals.sql");
        await File.WriteAllTextAsync(dump, """
            CREATE TABLE `areatrigger_teleport` (`id` mediumint unsigned NOT NULL, `name` text, `required_level` tinyint unsigned NOT NULL, `required_item` mediumint unsigned, `required_item2` mediumint unsigned, `required_quest_done` mediumint unsigned, `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL, `status_failed_text` text, `condition_id` int, PRIMARY KEY (`id`));
            INSERT INTO `areatrigger_teleport` VALUES (2848,'Onyxia',50,16309,7,0,249,1,2,3,4,'amulet',11),(3528,'Molten Core',50,0,0,7848,409,5,6,7,8,'quest',0);
            """);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world);
        Assert.True(code == ExitCodes.Ok, error + output);
        await using WorldDbContext verify = Open(world);
        AreaTriggerTeleportRow onyxia = await verify.Set<AreaTriggerTeleportRow>().SingleAsync(r => r.Id == 2848);
        AreaTriggerTeleportRow moltenCore = await verify.Set<AreaTriggerTeleportRow>().SingleAsync(r => r.Id == 3528);
        Assert.Equal(((byte)50, 16309u, 7u, 0u, 11u), (onyxia.RequiredLevel, onyxia.RequiredItem, onyxia.RequiredItem2, onyxia.RequiredQuestDone, onyxia.RequiredCondition));
        Assert.Equal(((byte)50, 0u, 0u, 7848u, 0u), (moltenCore.RequiredLevel, moltenCore.RequiredItem, moltenCore.RequiredItem2, moltenCore.RequiredQuestDone, moltenCore.RequiredCondition));
        Assert.Equal("local destination", onyxia.Name);
        Assert.Equal(42u, (await verify.Set<AreaTriggerTeleportRow>().SingleAsync(r => r.Id == 9999)).RequiredItem);
    }

    /// <summary>A world as an older importer left it: one creature template, one portal, everything this command fills empty.</summary>
    private async Task<string> OldWorldAsync(bool withShip = true)
    {
        string path = PathOf("world.db");
        await using WorldDbContext db = Open(path);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 1328, Name = "Elly Langston" });
        db.Set<AreaTriggerTeleportRow>().Add(new AreaTriggerTeleportRow { Id = 78, Name = "Deadmines Entrance", TargetMap = 36 });
        if (withShip)
        {
            db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow { Entry = 176231, Type = 15, Name = "Proudmore's Treasure" });
        }

        await db.SaveChangesAsync();
        return path;
    }

    /// <summary>A WDBC file: the given four-byte fields per record and an empty string block.</summary>
    private string Dbc(string name, int fields, params object[][] rows)
    {
        string directory = PathOf("dbc");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        byte[] strings = Encoding.UTF8.GetBytes("\0Arathi Basin - Alliance Entrance\0");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("WDBC"));
        writer.Write((uint)rows.Length);
        writer.Write((uint)fields);
        writer.Write((uint)(fields * 4));
        writer.Write((uint)strings.Length);
        foreach (object[] row in rows)
        {
            Assert.Equal(fields, row.Length);
            foreach (object value in row)
            {
                switch (value)
                {
                    case float f: writer.Write(f); break;
                    case int i: writer.Write((uint)i); break;
                    default: writer.Write((uint)value); break;
                }
            }
        }

        writer.Write(strings);
        return directory;
    }

    private string Dbcs()
    {
        // AreaTrigger.dbc (id, map, x, y, z, radius, box x/y/z/orientation): the Deadmines portal 78 and the Sentinel Hill and Goldshire inns.
        Dbc("AreaTrigger.dbc", 10,
            [71, 0, -10646.7f, 1105.23f, 35.6f, 15f, 0f, 0f, 0f, 0f],
            [78, 0, -11208.5f, 1685.34f, 25.7612f, 7f, 0f, 0f, 0f, 0f],
            [562, 0, -9462.89f, 15.2f, 56.9f, 0f, 20f, 15f, 10f, 0.5f]);

        // WorldSafeLocs.dbc (id, map, x, y, z, eight names, flags): 611 again (the dump's row with its facing must win) and 890, which the
        // dump lacks (name at string offset 1).
        Dbc("WorldSafeLocs.dbc", 14,
            [611, 30, 1f, 2f, 3f, 1, 0, 0, 0, 0, 0, 0, 0, 0],
            [890, 529, 1313.9f, 1310.74f, -9.01043f, 1, 0, 0, 0, 0, 0, 0, 0, 0]);
        return MapDbcs();
    }

    /// <summary>
    /// Map.dbc (42 fields: id, directory, instance type, pvp, names, ..., linked zone at 19) with the continents, Alterac Valley and Warsong
    /// Gulch (battlegrounds) and The Deadmines (dungeon); AreaTable.dbc (25 fields: id, map, parent, explore flag, flags, ..., level at 10,
    /// name at 11, team at 20, liquid at 24) with Elwynn Forest, Alterac Valley (2597, the dump's graveyard zone) and The Deadmines (1581).
    /// </summary>
    private string MapDbcs()
    {
        Dbc("Map.dbc", 42, MapRow(0, 0, 0), MapRow(1, 0, 0), MapRow(30, 3, 0), MapRow(36, 1, 1581), MapRow(489, 3, 3277));
        return Dbc("AreaTable.dbc", 25, AreaRow(12, 0, 12), AreaRow(2597, 30, 2597), AreaRow(1581, 36, 0));

        static object[] AreaRow(int id, int map, int flag)
        {
            object[] row = Enumerable.Repeat<object>(0, 25).ToArray();
            row[0] = id;
            row[1] = map;
            row[3] = flag;
            row[11] = 1;
            return row;
        }
    }

    /// <summary>A Map.dbc row (42 fields): id, instance type at 2, an enUS name at 4, the linked zone at 19.</summary>
    private static object[] MapRow(int id, int type, int linkedZone)
    {
        object[] row = Enumerable.Repeat<object>(0, 42).ToArray();
        row[0] = id;
        row[2] = type;
        row[4] = 1;
        row[19] = linkedZone;
        return row;
    }

    /// <summary>
    /// Blackrock Spire (229) keeps no global reset: classic-db z2815 gives it <c>reset_delay</c> 3, but vmangos, the fidelity reference,
    /// removed it ("Blackrock Spire no reset", sql/old_migrations/20170917193208_world.sql: <c>UPDATE map_template SET ResetDelay=0
    /// WHERE Entry=229</c>; all its 229 rows in 20171129015531 have 0). With 3 the world would schedule a global reset of map 229 and send
    /// everyone inside home every three days. Naxxramas' 7 is left alone.
    /// </summary>
    [Fact]
    public async Task Refresh_GivesBlackrockSpireNoResetDelay_AsVmangosDoes_AndKeepsTheRaidsOwn()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        // classic-db z2815 instance_template 229 and 533, verbatim.
        File.WriteAllText(dump, Dump + "\n" +
            "INSERT INTO `instance_template` (`map`,`parent`,`levelMin`,`levelMax`,`maxPlayers`,`reset_delay`,`ghostEntranceMap`,`ghostEntranceX`,`ghostEntranceY`,`ScriptName`,`mountAllowed`) " +
            "VALUES (229,0,55,0,10,3,0,-7522.53,-1233.04,'instance_blackrock_spire',0),(533,0,60,60,40,7,0,0,0,'instance_naxxramas',0);\n");
        string dbc = Dbcs();
        Dbc("Map.dbc", 42, MapRow(0, 0, 0), MapRow(1, 0, 0), MapRow(30, 3, 0), MapRow(36, 1, 1581), MapRow(489, 3, 3277),
            MapRow(229, 1, 0), MapRow(533, 2, 0));

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("map 229 reset_delay 3 -> 0 (vmangos: Blackrock Spire has no global reset)", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(world);
        MapTemplateRow spire = await db.Set<MapTemplateRow>().SingleAsync(r => r.Entry == 229);
        Assert.Equal(((byte)1, 10u, 0u, "instance_blackrock_spire"), (spire.MapType, spire.PlayerLimit, spire.ResetDelay, spire.ScriptName));
        Assert.Equal(7u, (await db.Set<MapTemplateRow>().SingleAsync(r => r.Entry == 533)).ResetDelay);
    }

    [Fact]
    public async Task Refresh_FillsTheTablesAnOlderWorldLacks_AndLeavesItsOtherTablesAlone()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("spell_proc_event cooldown unit: seconds", output, StringComparison.Ordinal);
        Assert.Contains("WorldSafeLocs.dbc: 2 row(s), 1 not in the dump added", output, StringComparison.Ordinal);
        Assert.True(output.Contains("refresh: every checked reference resolves", StringComparison.Ordinal), output);
        await using WorldDbContext db = Open(world);
        Assert.Equal([610u, 611u, 769u, 770u, 890u], await db.Set<WorldSafeLocRow>().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync());
        WorldSafeLocRow alliance = await db.Set<WorldSafeLocRow>().SingleAsync(r => r.Id == 611);
        Assert.Equal((873.002f, 3.92699f), (alliance.X, alliance.Orientation)); // the dump's row, not the DBC's
        Assert.Equal("Arathi Basin - Alliance Entrance", (await db.Set<WorldSafeLocRow>().SingleAsync(r => r.Id == 890)).Name);
        Assert.Equal(1, await db.Set<GraveyardZoneRow>().CountAsync());
        Assert.Equal(2, await db.Set<BattlegroundTemplateRow>().CountAsync());
        Assert.Equal(2, await db.Set<BattlemasterEntryRow>().CountAsync());
        Assert.Equal(660u, (await db.Set<ExplorationBaseXpRow>().SingleAsync(r => r.Level == 60)).BaseXp);
        Assert.Equal([71u, 562u], await db.Set<AreaTriggerTavernRow>().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync());
        TransportRow ship = await db.Set<TransportRow>().SingleAsync();
        Assert.Equal((176231u, (ushort)0, 329313u), (ship.Entry, ship.Build, ship.Period));
        Assert.Equal(3000u, (await new EfSpellProcEventStore(db).LoadAsync()).Find(324)!.Cooldown); // 3 s in z2815
        Assert.Equal(2, await db.Set<RelayScriptRow>().CountAsync(r => r.Id == 19958));
        AreaTriggerTemplateRow inn = await db.Set<AreaTriggerTemplateRow>().SingleAsync(r => r.Id == 562);
        Assert.Equal((20f, 15f, 10f, 0.5f), (inn.BoxX, inn.BoxY, inn.BoxZ, inn.BoxOrientation));
        Assert.Equal(3, await db.Set<AreaTriggerTemplateRow>().CountAsync());

        // Every map and area of the DBCs, the dungeon columns from the dump's instance_template.
        Assert.Contains("  map_template  5", output, StringComparison.Ordinal);
        Assert.Contains("  area_template  3", output, StringComparison.Ordinal);
        Assert.Equal([0u, 1u, 30u, 36u, 489u], await db.Set<MapTemplateRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToListAsync());
        MapTemplateRow deadmines = await db.Set<MapTemplateRow>().SingleAsync(r => r.Entry == 36);
        Assert.Equal(((byte)1, 1581u, 10u, 0, "instance_deadmines"),
            (deadmines.MapType, deadmines.LinkedZone, deadmines.PlayerLimit, deadmines.GhostEntranceMap, deadmines.ScriptName));
        Assert.Equal(-1, (await db.Set<MapTemplateRow>().SingleAsync(r => r.Entry == 489)).GhostEntranceMap);
        Assert.Equal([12u, 1581u, 2597u], await db.Set<AreaTemplateRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToListAsync());

        // The rest of the old world is as it was.
        Assert.Equal([1328u], await db.Set<CreatureTemplateRow>().Select(r => r.Entry).ToListAsync());
        Assert.Equal(1, await db.Set<AreaTriggerTeleportRow>().CountAsync());
    }

    /// <summary>
    /// Real classic-db z2815 game-event tuples (shortened): Feast of Winter Veil (2), Winter Veil: Gifts (21) with quest 8827
    /// "Winter's Presents", Love is in the Air (8) with the spawns of Colara Dean (91691) and Tormek Stoneriver (91693) of quest 8898
    /// "Dearest Colara,", and Greatfather Winter's spawn (86184) under event 2.
    /// </summary>
    private const string EventDump = """
        INSERT INTO `game_event` (`entry`,`schedule_type`,`occurence`,`length`,`holiday`,`linkedTo`,`description`) VALUES (2,11,525600,27360,141,0,'Feast of Winter Veil'),(8,11,525600,5760,335,0,'Love is in the Air'),(21,1,525600,11700,0,0,'Winter Veil: Gifts');
        INSERT INTO `game_event_time` (`entry`,`start_time`,`end_time`) VALUES (2,'2020-12-16 23:00:00','2030-12-31 22:59:59'),(8,'2020-02-08 22:00:00','2030-12-31 22:59:59'),(21,'2020-12-25 06:00:00','2030-12-31 22:59:59');
        INSERT INTO `game_event_creature` (`guid`,`event`) VALUES (86184,2),(91691,8),(91693,8);
        INSERT INTO `game_event_quest` (`quest`,`event`) VALUES (8827,21);
        """;

    /// <summary>
    /// A world migrated from the Codex-line schema has empty game-event tables (the live world of the wave-8 rehearsal): every holiday NPC
    /// stood in the world all year and its quests were offered in October. Refresh fills them from the dump.
    /// </summary>
    [Fact]
    public async Task Refresh_FillsEmptyGameEventTables()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump + Environment.NewLine + EventDump);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("  game_event  3", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(world);
        Assert.Equal([2u, 8u, 21u], await db.Set<GameEventRow>().OrderBy(r => r.Entry).Select(r => r.Entry).ToListAsync());
        Assert.Equal(3, await db.Set<GameEventTimeRow>().CountAsync());
        Assert.Equal([86184u, 91691u, 91693u], await db.Set<GameEventCreatureRow>().OrderBy(r => r.Guid).Select(r => r.Guid).ToListAsync());
        GameEventQuestRow gifts = await db.Set<GameEventQuestRow>().SingleAsync();
        Assert.Equal((8827u, 21), (gifts.Quest, gifts.Event));
    }

    /// <summary>A world that has events (imported, or one a GM disabled) keeps its own game-event tables.</summary>
    [Fact]
    public async Task Refresh_LeavesAWorldsOwnGameEvents()
    {
        string world = await OldWorldAsync();
        await using (WorldDbContext seed = Open(world))
        {
            seed.Set<GameEventRow>().Add(new GameEventRow { Entry = 2, ScheduleType = 11, Occurence = 525600, Length = 27360, Description = "Winter Veil", Disabled = true });
            await seed.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump + Environment.NewLine + EventDump);
        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("the world has 1 event(s) already", output, StringComparison.Ordinal);
        Assert.DoesNotContain("  game_event  3", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(world);
        GameEventRow kept = await db.Set<GameEventRow>().SingleAsync();
        Assert.True(kept.Disabled);
        Assert.Equal(0, await db.Set<GameEventCreatureRow>().CountAsync());
    }

    /// <summary>
    /// Real classic-db z2815 <c>creature_spawn_entry</c> rows: spawn 804 (creature.id 0, Stranglethorn: entries 684 and 772), 8308 (id 4463,
    /// alternatives 435, 615 and 4463), 15138 (id 3236, alternatives 3235 to 3237) and 93766, which is in no <c>creature</c> row of the dump.
    /// </summary>
    private const string SpawnEntryDump = """
        INSERT INTO `creature_spawn_entry` (`guid`,`entry`) VALUES (804,684),(804,772),(8308,435),(8308,615),(8308,4463),(15138,3235),(15138,3236),(15138,3237),(93766,15246),(93766,15250);
        """;

    private async Task SeedSpawnsAsync(string world, params (uint Guid, uint Entry)[] spawns)
    {
        await using WorldDbContext seed = Open(world);
        foreach ((uint guid, uint entry) in spawns)
        {
            seed.Set<CreatureSpawnRow>().Add(new CreatureSpawnRow { Guid = guid, Entry = entry, MapId = 0, X = 1, Y = 2, Z = 3 });
        }

        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// A Codex-line world has <c>creature_spawn_entry</c> empty, so its 2802 spawns with entry 0 never appear. Refresh fills it for the
    /// world's own spawns: 804 (entry 0) and 8308 (its entry is one of the dump's), not 15138 (the world's spawn is another creature) or
    /// 93766 (not a spawn of this world).
    /// </summary>
    [Fact]
    public async Task Refresh_FillsAnEmptySpawnEntryTable_ForTheWorldsOwnSpawnsOnly()
    {
        string world = await OldWorldAsync();
        await SeedSpawnsAsync(world, (804, 0), (8308, 4463), (15138, 9999));
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump + Environment.NewLine + SpawnEntryDump);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("  creature_spawn_entry  5", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(world);
        Assert.Equal([(804u, 684u), (804u, 772u), (8308u, 435u), (8308u, 615u), (8308u, 4463u)],
            (await db.Set<CreatureSpawnEntryRow>().ToListAsync()).Select(r => (r.SpawnGuid, r.Entry)).Order().ToList());

        // a second run finds rows and leaves them
        (code, output, error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());
        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("creature_spawn_entry: the world has rows already; left as it is", output, StringComparison.Ordinal);
        await using WorldDbContext again = Open(world);
        Assert.Equal(5, await again.Set<CreatureSpawnEntryRow>().CountAsync());
    }

    /// <summary>A world with its own spawn entries keeps them (a GM or another importer chose them).</summary>
    [Fact]
    public async Task Refresh_LeavesAWorldsOwnSpawnEntries()
    {
        string world = await OldWorldAsync();
        await SeedSpawnsAsync(world, (804, 0));
        await using (WorldDbContext seed = Open(world))
        {
            seed.Set<CreatureSpawnEntryRow>().Add(new CreatureSpawnEntryRow { SpawnGuid = 804, Entry = 684 });
            await seed.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump + Environment.NewLine + SpawnEntryDump);
        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.DoesNotContain("  creature_spawn_entry  ", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(world);
        CreatureSpawnEntryRow kept = await db.Set<CreatureSpawnEntryRow>().SingleAsync();
        Assert.Equal((804u, 684u), (kept.SpawnGuid, kept.Entry));
    }

    /// <summary>
    /// Real classic-db z2815 <c>creature_movement_template</c> rows: Cook Ghilm (1355) path 0, which relay script 25 starts, and entry 1269 path
    /// 1; plus <c>waypoint_path</c> 19023 point 1 (the Magram patrol formation), which goes to its own namespace. The live world of 2026-10-16
    /// had none of classic-db's 544 entry paths, so 326 waypoint spawns had no path and every relay waypoint step was skipped.
    /// </summary>
    private const string EntryPathDump = """
        INSERT INTO `creature_movement_template` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES (1269,1,1,-5496.71,-455.32,395.437,0.81,10000,126901,NULL),(1269,1,2,-5499.78,-454.443,395.437,1.137,6000,126902,NULL),(1269,1,3,-5497.94,-455.835,395.392,100,1000,126903,NULL),(1355,0,1,-5664.75,-1566.57,383.329,100,7000,135501,'Cook Ghilm'),(1355,0,2,-5666.22,-1565.62,383.257,2.32129,100,135502,'Cook Ghilm');
        INSERT INTO `waypoint_path` (`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES (19023,1,-1.5,2.5,3.5,100,0,0,NULL);
        """;

    /// <summary>
    /// A world built before <c>creature_movement_template</c> existed has the table empty; refresh fills the dump's ordinary entry paths (the
    /// <c>waypoint_path</c> copy stays in its namespace), and a second run leaves them.
    /// </summary>
    [Fact]
    public async Task Refresh_FillsEmptyEntryPaths_FromTheDump()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump + Environment.NewLine + EntryPathDump);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("  creature_movement_template (entry paths)  5", output, StringComparison.Ordinal);
        await using (WorldDbContext db = Open(world))
        {
            List<CreatureMovementTemplateRow> rows = await db.Set<CreatureMovementTemplateRow>().ToListAsync();
            Assert.Equal([(1269u, 1u, 1u), (1269u, 1u, 2u), (1269u, 1u, 3u), (1355u, 0u, 1u), (1355u, 0u, 2u)],
                rows.Where(r => r.Entry != 0).Select(r => (r.Entry, r.PathId, r.Point)).Order().ToList());
            CreatureMovementTemplateRow first = rows.Single(r => r.Entry == 1355 && r.Point == 1);
            Assert.Equal((-5664.75f, 7000u, 135501u), (first.X, first.WaitTimeMs, first.ScriptId));
            Assert.Equal((0u, 0x4000_0000u | 19023u), rows.Where(r => r.Entry == 0).Select(r => (r.Entry, r.PathId)).Single());
        }

        (code, output, error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());
        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("creature_movement_template (entry paths): the world has entry paths already; left as they are", output, StringComparison.Ordinal);
        await using WorldDbContext again = Open(world);
        Assert.Equal(5, await again.Set<CreatureMovementTemplateRow>().CountAsync(r => r.Entry != 0));
    }

    /// <summary>A world with an entry path of its own keeps exactly what it has (refresh never replaces the creature tables).</summary>
    [Fact]
    public async Task Refresh_LeavesAWorldsOwnEntryPaths()
    {
        string world = await OldWorldAsync();
        await using (WorldDbContext seed = Open(world))
        {
            seed.Set<CreatureMovementTemplateRow>().Add(new CreatureMovementTemplateRow { Entry = 1355, PathId = 0, Point = 1, X = 9, Y = 9, Z = 9 });
            await seed.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump + Environment.NewLine + EntryPathDump);
        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.DoesNotContain("  creature_movement_template (entry paths)  ", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(world);
        CreatureMovementTemplateRow kept = await db.Set<CreatureMovementTemplateRow>().SingleAsync(r => r.Entry != 0);
        Assert.Equal((1355u, 0u, 1u, 9f), (kept.Entry, kept.PathId, kept.Point, kept.X));
    }

    [Fact]
    public async Task Refresh_RunTwice_LeavesTheSameRows()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();
        Assert.Equal(ExitCodes.Ok, (await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc)).Code);
        string first = await SnapshotAsync(world);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Equal(first, await SnapshotAsync(world));
    }

    private static async Task<string> SnapshotAsync(string world)
    {
        await using WorldDbContext db = Open(world);
        var text = new StringBuilder();
        foreach (WorldSafeLocRow r in await db.Set<WorldSafeLocRow>().OrderBy(r => r.Id).ToListAsync())
        {
            text.Append($"loc {r.Id} {r.MapId} {r.X} {r.Y} {r.Z} {r.Orientation} {r.Name}\n");
        }

        text.Append($"links {await db.Set<GraveyardZoneRow>().CountAsync()} bg {await db.Set<BattlegroundTemplateRow>().CountAsync()} ");
        text.Append($"masters {await db.Set<BattlemasterEntryRow>().CountAsync()} xp {await db.Set<ExplorationBaseXpRow>().CountAsync()} ");
        text.Append($"inns {await db.Set<AreaTriggerTavernRow>().CountAsync()} ships {await db.Set<TransportRow>().CountAsync()} ");
        text.Append($"procs {await db.Set<SpellProcEventRow>().CountAsync()} relay {await db.Set<RelayScriptRow>().CountAsync()} ");
        text.Append($"triggers {await db.Set<AreaTriggerTemplateRow>().CountAsync()} creatures {await db.Set<CreatureTemplateRow>().CountAsync()}\n");
        foreach (MapTemplateRow r in await db.Set<MapTemplateRow>().OrderBy(r => r.Entry).ToListAsync())
        {
            text.Append($"map {r.Entry} {r.MapType} {r.LinkedZone} {r.PlayerLimit} {r.ResetDelay} {r.GhostEntranceMap} {r.GhostEntranceX} {r.GhostEntranceY} {r.MapName} {r.ScriptName}\n");
        }

        foreach (AreaTemplateRow r in await db.Set<AreaTemplateRow>().OrderBy(r => r.Entry).ToListAsync())
        {
            text.Append($"area {r.Entry} {r.MapId} {r.ZoneId} {r.ExploreFlag} {r.Name}\n");
        }

        return text.ToString();
    }

    [Fact]
    public async Task Refresh_WithoutTheAreaTriggerDbc_NamesThePortalsThatStillHaveNoTrigger()
    {
        string world = await OldWorldAsync(withShip: false);
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("check: 1 areatrigger_teleport row(s) have no areatrigger_template row: 78", output, StringComparison.Ordinal);
        Assert.Contains("check: 2 areatrigger_tavern row(s) have no areatrigger_template row: 71, 562", output, StringComparison.Ordinal);
        Assert.Contains("check: 1 transports row(s) name no gameobject_template of type 15: 176231", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A world whose map_template and area_template hold only the continents (what import-map-dbc left before) and a DBC directory without
    /// Map.dbc and AreaTable.dbc: the refresh leaves both tables alone and names what the world will skip at start (the portal to map 36,
    /// the graveyard link to zone 2597), the world's own "unknown target map" and "not existing zone id" warnings.
    /// </summary>
    [Fact]
    public async Task Refresh_WithoutTheMapDbcs_NamesThePortalsToUnknownMaps_AndTheGraveyardZonesWithoutAnArea()
    {
        string world = await OldWorldAsync();
        await using (WorldDbContext db = Open(world))
        {
            db.Set<MapTemplateRow>().AddRange(new MapTemplateRow { Entry = 0, MapName = "Eastern Kingdoms" }, new MapTemplateRow { Entry = 1, MapName = "Kalimdor" });
            db.Set<AreaTemplateRow>().Add(new AreaTemplateRow { Entry = 12, MapId = 0, Name = "Elwynn Forest" });
            await db.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();
        File.Delete(Path.Combine(dbc, "Map.dbc"));
        File.Delete(Path.Combine(dbc, "AreaTable.dbc"));

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("map_template and area_template are left as they are", output, StringComparison.Ordinal);
        Assert.Contains("check: 1 areatrigger_teleport row(s) lead to a map with no map_template row: 78 (map 36)", output, StringComparison.Ordinal);
        Assert.Contains("check: 1 game_graveyard_zone row(s) name a zone with no area_template row: 2597", output, StringComparison.Ordinal);
        await using WorldDbContext after = Open(world);
        Assert.Equal(2, await after.Set<MapTemplateRow>().CountAsync());
        Assert.Equal(1, await after.Set<AreaTemplateRow>().CountAsync());
    }

    /// <summary>WMOAreaTable.dbc (20 fields: id, WMOID, NameSetID, WMOGroupID, ..., flags at 9, AreaTableID at 10) beside the map DBCs fills wmo_area_template.</summary>
    [Fact]
    public async Task Refresh_WithAWmoAreaTable_FillsWmoAreaTemplate_AndReportsTheRows()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();
        Dbc("WMOAreaTable.dbc", 20, WmoRow(100, 1500, 3, 77, 12), WmoRow(101, 1500, 3, -1, 0));

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("WMOAreaTable.dbc: 2 row(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("no WMOAreaTable.dbc", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(world);
        WmoAreaTemplateRow[] rows = await db.Set<WmoAreaTemplateRow>().OrderBy(r => r.Id).ToArrayAsync();
        Assert.Equal([(100u, 1500, 3, 77, 12u), (101u, 1500, 3, -1, 0u)], rows.Select(r => (r.Id, r.RootId, r.AdtId, r.GroupId, r.AreaId)));
    }

    /// <summary>Without WMOAreaTable.dbc the table is left as it is, and the refresh says positions inside a WMO keep reading the terrain.</summary>
    [Fact]
    public async Task Refresh_WithoutAWmoAreaTable_LeavesTheTable_AndWarns()
    {
        string world = await OldWorldAsync();
        await using (WorldDbContext db = Open(world))
        {
            db.Set<WmoAreaTemplateRow>().Add(new WmoAreaTemplateRow { Id = 9, RootId = 9, AreaId = 12 });
            await db.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("no WMOAreaTable.dbc in", output, StringComparison.Ordinal);
        Assert.Contains("wmo_area_template is left as it is", output, StringComparison.Ordinal);
        await using WorldDbContext after = Open(world);
        uint[] kept = await after.Set<WmoAreaTemplateRow>().Select(r => r.Id).ToArrayAsync();
        Assert.Equal([9u], kept);
    }

    private static object[] WmoRow(int id, int root, int adt, int group, int area)
    {
        object[] row = Enumerable.Repeat<object>(0, 20).ToArray();
        row[0] = id;
        row[1] = root;
        row[2] = adt;
        row[3] = group;
        row[10] = area;
        return row;
    }

    /// <summary>The same world refreshed with Map.dbc and AreaTable.dbc: both tables are replaced and both checks pass.</summary>
    [Fact]
    public async Task Refresh_WithTheMapDbcs_ReplacesTheContinentOnlyTables_AndEveryPortalAndGraveyardZoneResolves()
    {
        string world = await OldWorldAsync();
        await using (WorldDbContext db = Open(world))
        {
            db.Set<MapTemplateRow>().AddRange(new MapTemplateRow { Entry = 0, MapName = "Eastern Kingdoms" }, new MapTemplateRow { Entry = 1, MapName = "Kalimdor" });
            db.Set<AreaTemplateRow>().Add(new AreaTemplateRow { Entry = 12, MapId = 0, Name = "Elwynn Forest" });
            await db.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.DoesNotContain("check:", output, StringComparison.Ordinal);
        Assert.True(output.Contains("refresh: every checked reference resolves", StringComparison.Ordinal), output);
        await using WorldDbContext after = Open(world);
        Assert.Equal(5, await after.Set<MapTemplateRow>().CountAsync());
        Assert.Equal(3, await after.Set<AreaTemplateRow>().CountAsync());
    }

    /// <summary>Map.dbc without AreaTable.dbc (or the other way round) is refused before anything is written: half a map table set is worse.</summary>
    [Fact]
    public async Task Refresh_WithOnlyOneOfTheMapDbcs_IsRefused_AndWritesNothing()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();
        File.Delete(Path.Combine(dbc, "AreaTable.dbc"));
        byte[] before = await File.ReadAllBytesAsync(world);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Io, $"{code}\n{error}{output}");
        Assert.Contains("AreaTable.dbc", error, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(world));
    }

    [Fact]
    public async Task Refresh_DryRun_WritesNothing()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        byte[] before = await File.ReadAllBytesAsync(world);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs(), "--dry-run");

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("would replace:", output, StringComparison.Ordinal);
        Assert.Contains("  world_safe_locs  5", output, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(world));
    }

    /// <summary>
    /// World schema 53 (shared vendor stock): refresh replaces npc_vendor_template and updates creature_template.VendorTemplateId, and the
    /// summary names the links it actually changed, not how many templates the dump carried.
    /// </summary>
    [Fact]
    public async Task Refresh_VendorTemplates_ReplacesTheSharedRows_AndCountsTheLinksItChanged()
    {
        string world = await OldWorldAsync();
        await using (WorldDbContext seed = Open(world))
        {
            seed.Set<CreatureTemplateRow>().AddRange(
                new CreatureTemplateRow { Entry = 1329, Name = "Same Link", VendorTemplateId = 5 },
                new CreatureTemplateRow { Entry = 1330, Name = "Old Link", VendorTemplateId = 6 });
            seed.Set<VendorTemplateItemRow>().Add(new VendorTemplateItemRow { Entry = 6, Item = 999 });
            seed.Set<VendorItem>().Add(new VendorItem { Entry = 1328, Item = 2900 });
            await seed.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, """
            INSERT INTO `creature_template` (`Entry`,`Name`,`ModelId1`,`MinLevel`,`MaxLevel`,`VendorTemplateId`) VALUES (1328,'Elly Langston',1,5,5,7),(1329,'Same Link',1,5,5,5),(1330,'Old Link',1,5,5,0),(9999,'Not In This World',1,5,5,8);
            INSERT INTO `npc_vendor_template` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (5,2901,0,0,1,0),(7,2902,2,60,1,0);
            """);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world);

        Assert.True(code == ExitCodes.Ok, error + output);
        // Two of the four dump rows name a world creature whose link differs (1328: 0 -> 7, 1330: 6 -> 0).
        Assert.Contains("  creature_template (VendorTemplateId)  2" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains("  npc_vendor_template  2" + Environment.NewLine, output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(world);
        Assert.Equal([(1328u, 7u), (1329u, 5u), (1330u, 0u)],
            (await db.Set<CreatureTemplateRow>().OrderBy(r => r.Entry).Select(r => new { r.Entry, r.VendorTemplateId }).ToListAsync()).Select(r => (r.Entry, r.VendorTemplateId)));
        Assert.Equal([(5u, 2901u), (7u, 2902u)],
            (await db.Set<VendorTemplateItemRow>().OrderBy(r => r.Entry).Select(r => new { r.Entry, r.Item }).ToListAsync()).Select(r => (r.Entry, r.Item)));
        Assert.Equal(2900u, (await db.Set<VendorItem>().SingleAsync()).Item); // direct rows are not replaced
    }

    /// <summary>A dump whose npc_vendor_template lacks required columns neither aborts the refresh nor empties the world's shared stock.</summary>
    [Fact]
    public async Task Refresh_AMalformedVendorTemplateTable_IsSkippedWithAWarning_AndKeepsTheWorldsRows()
    {
        string world = await OldWorldAsync();
        await using (WorldDbContext seed = Open(world))
        {
            seed.Set<VendorTemplateItemRow>().Add(new VendorTemplateItemRow { Entry = 6, Item = 999 });
            seed.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 1329, Name = "Linked", VendorTemplateId = 6 });
            await seed.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump + Environment.NewLine
            + "INSERT INTO `creature_template` (`Entry`,`Name`,`ModelId1`,`MinLevel`,`MaxLevel`,`VendorTemplateId`) VALUES (1329,'Linked',1,5,5,7);" + Environment.NewLine
            + "INSERT INTO `npc_vendor_template` (`entry`,`item`,`maxcount`,`incrtime`) VALUES (7,2902,0,0);");

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        await using WorldDbContext db = Open(world);
        Assert.Equal(999u, (await db.Set<VendorTemplateItemRow>().SingleAsync()).Item);
        Assert.Equal(6u, (await db.Set<CreatureTemplateRow>().SingleAsync(r => r.Entry == 1329)).VendorTemplateId); // links stay consistent with the stock
        Assert.DoesNotContain("npc_vendor_template  ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("creature_template (VendorTemplateId)", output, StringComparison.Ordinal);
        Assert.Contains("npc_vendor_template: skipped, the table is missing required columns: slot, condition_id", output, StringComparison.Ordinal);
        Assert.Contains("VendorTemplateId is not refreshed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_RefusesAMissingDatabase_InsteadOfCreatingAnEmptyWorld()
    {
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string missing = PathOf("no-such-world.db");

        (int code, _, string error) = await RunAsync("refresh", dump, "--database", missing);

        Assert.Equal(ExitCodes.Io, code);
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task Refresh_RefusesAWorldBehindTheImportersSchema_UnlessToldToMigrate()
    {
        // A refresh built from a tree whose world schema is ahead (another lane's step merged in) must not migrate the live world as a
        // side effect: it refuses and writes nothing; --migrate is the explicit way through (the world server's own start migrates too).
        string world = await OldWorldAsync();
        int current = WorldDbContext.Schema.CurrentVersion;
        await using (WorldDbContext db = Open(world))
        {
            await Upgrade.UpgradeTestSupport.SetVersionAsync(db, "world", current - 1);
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.Equal(ExitCodes.Schema, code);
        Assert.Contains("--migrate", error, StringComparison.Ordinal);
        await using (WorldDbContext db = Open(world))
        {
            Assert.Equal(current - 1, await Upgrade.UpgradeTestSupport.ReadVersionAsync(db, "world"));
            Assert.Equal(0, await db.Set<WorldSafeLocRow>().CountAsync());
        }

        (code, output, error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc, "--migrate");

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains($"world schema {current - 1} -> {current}", output, StringComparison.Ordinal);
        await using (WorldDbContext db = Open(world))
        {
            Assert.Equal(current, await Upgrade.UpgradeTestSupport.ReadVersionAsync(db, "world"));
            Assert.Equal(5, await db.Set<WorldSafeLocRow>().CountAsync());
        }
    }

    [Fact]
    public async Task Refresh_ProcEventsWithoutAClassicDbRevision_NeedAnExplicitCooldownUnit()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("procs.sql");
        File.WriteAllText(dump, "INSERT INTO `spell_proc_event` (`entry`,`procFlags`,`Cooldown`) VALUES (324,0,3);\n");

        (int code, _, string error) = await RunAsync("refresh", dump, "--database", world);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--cooldown-unit", error, StringComparison.Ordinal);

        (int explicitCode, string output, string explicitError) = await RunAsync("refresh", dump, "--database", world, "--cooldown-unit", "ms");
        Assert.True(explicitCode == ExitCodes.Ok, explicitError + output);
        await using WorldDbContext db = Open(world);
        Assert.Equal(3u, (await new EfSpellProcEventStore(db).LoadAsync()).Find(324)!.Cooldown);
    }

    /// <summary>A classic-db z2815 <c>gameobject_template</c> row (every column of the dump's header, data3..23 zero).</summary>
    private static string GameObjectTemplate(uint entry, uint type, uint display, string name, uint data0, uint data1, uint data2)
    {
        string columns = "`entry`,`type`,`displayId`,`name`,`faction`,`flags`,`ExtraFlags`,`size`,"
            + string.Join(",", Enumerable.Range(0, 24).Select(i => $"`data{i}`")) + ",`CustomData1`,`mingold`,`maxgold`,`StringId`,`ScriptName`";
        string data = string.Join(",", new[] { data0, data1, data2 }.Concat(Enumerable.Repeat(0u, 21)));
        string quoted = name.Replace("'", "\\'", StringComparison.Ordinal);
        return $"INSERT INTO `gameobject_template` ({columns}) VALUES ({entry},{type},{display},'{quoted}',0,40,0,1,{data},0,0,0,0,'');\n";
    }

    /// <summary>
    /// The two ships of the classic-db rows (Menethil - Theramore, path 292, and Menethil - Auberdine, path 295, both 30 yd/s and
    /// 1 yd/s²), their periods, and a chest whose template the refresh must leave alone.
    /// </summary>
    private static string ShipDump()
        => GameObjectTemplate(176231, 15, 3015, "Proudmore's Treasure", 292, 30, 1)
            + GameObjectTemplate(176310, 15, 3015, "Serenity's Shore", 295, 30, 1)
            + GameObjectTemplate(1617, 3, 270, "Silverleaf", 43, 1415, 0)
            + "INSERT INTO `transports` (`entry`,`name`,`period`) VALUES (176231,'Menethil Harbor and Theramore Isle',329313),(176310,'Menethil Harbor and Auberdine',295579);\n";

    /// <summary>
    /// TaxiNodes.dbc (id, map, x, y, z, eight names, flags, two mounts), TaxiPath.dbc (id, from, to, cost) and TaxiPathNode.dbc (id,
    /// path, index, map, x, y, z, action flag, delay): path 292 runs from map 0 to map 1, path 295 stays on map 0.
    /// </summary>
    private string TaxiDbcs(bool withPath295 = true)
    {
        Dbc("TaxiNodes.dbc", 16,
            [2, 0, -8835.76f, 490.084f, 109.616f, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 541],
            [6, 0, -4821.13f, -1152.4f, 502.295f, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 541]);
        Dbc("TaxiPath.dbc", 4, [6, 2, 6, 120], [292, 0, 0, 0]);
        var nodes = new List<object[]>
        {
            new object[] { 3001, 292, 0, 0, -3896f, -600f, 5f, 0, 0 },
            new object[] { 3002, 292, 1, 0, -3900f, -610f, 5f, 2, 30 },
            new object[] { 3003, 292, 2, 1, -3990f, -4720f, 5f, 0, 0 },
            new object[] { 3004, 292, 3, 1, -4000f, -4725f, 5f, 2, 30 },
        };
        if (withPath295)
        {
            nodes.Add([3101, 295, 0, 0, -3700f, -580f, 5f, 0, 0]);
            nodes.Add([3102, 295, 1, 0, -3720f, -590f, 5f, 2, 30]);
            nodes.Add([3103, 295, 2, 0, -3000f, 200f, 5f, 2, 30]);
        }

        return Dbc("TaxiPathNode.dbc", 9, [.. nodes]);
    }

    private async Task AddMapsAsync(string world, params uint[] maps)
    {
        await using WorldDbContext db = Open(world);
        foreach (uint map in maps)
        {
            db.Set<MapTemplateRow>().Add(new MapTemplateRow { Entry = map, MapName = "map " + map });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Refresh_ImportsTheShipTemplates_AndTheTaxiTables_LeavingTheOtherObjectsAlone()
    {
        string world = await OldWorldAsync(withShip: false);
        await AddMapsAsync(world, 0, 1);
        await using (WorldDbContext db = Open(world))
        {
            // An older import's rows: a ship whose path and speed are stale, and the chest with its own name.
            db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow { Entry = 176310, Type = 15, Name = "stale", Data0 = 7 });
            db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow { Entry = 1617, Type = 3, Name = "Silverleaf (older import)", Data0 = 43 });
            await db.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump + "\n" + ShipDump());
        Dbcs();
        string dbc = TaxiDbcs();

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("  gameobject_template (type 15)  2", output, StringComparison.Ordinal);
        Assert.Contains("  taxi_nodes  2", output, StringComparison.Ordinal);
        Assert.Contains("  taxi_path  2", output, StringComparison.Ordinal);
        Assert.Contains("TaxiPathNode.dbc: 7 row(s) on 2 path(s)", output, StringComparison.Ordinal);
        Assert.True(output.Contains("refresh: every checked reference resolves", StringComparison.Ordinal), output);
        await using (WorldDbContext check = Open(world))
        {
            GameObjectTemplateRow theramore = await check.Set<GameObjectTemplateRow>().SingleAsync(r => r.Entry == 176231);
            Assert.Equal((15u, 3015u, "Proudmore's Treasure", 40u, 292u, 30u, 1u),
                (theramore.Type, theramore.DisplayId, theramore.Name, theramore.Flags, theramore.Data0, theramore.Data1, theramore.Data2));
            GameObjectTemplateRow auberdine = await check.Set<GameObjectTemplateRow>().SingleAsync(r => r.Entry == 176310);
            Assert.Equal(("Serenity's Shore", 295u, 30u, 1u), (auberdine.Name, auberdine.Data0, auberdine.Data1, auberdine.Data2));
            Assert.Equal("Silverleaf (older import)", (await check.Set<GameObjectTemplateRow>().SingleAsync(r => r.Entry == 1617)).Name);
            Assert.Equal(2, await check.Set<TransportRow>().CountAsync());
            TaxiNode stormwind = await check.Set<TaxiNode>().SingleAsync(n => n.Id == 2);
            Assert.Equal((0u, -8835.76f, "Arathi Basin - Alliance Entrance", 0u, 541u),
                (stormwind.MapId, stormwind.X, stormwind.Name, stormwind.MountHorde, stormwind.MountAlliance));
            Assert.Equal([6u, 292u], await check.Set<TaxiPath>().OrderBy(p => p.Id).Select(p => p.Id).ToListAsync());
            TaxiPath flight = await check.Set<TaxiPath>().SingleAsync(p => p.Id == 6);
            Assert.Equal((2u, 6u, 120u), (flight.FromNode, flight.ToNode, flight.Price));
        }

        // A second run leaves the same rows.
        (code, output, error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);
        Assert.True(code == ExitCodes.Ok, error + output);
        await using WorldDbContext again = Open(world);
        Assert.Equal(3, await again.Set<GameObjectTemplateRow>().CountAsync());
        Assert.Equal(2, await again.Set<TaxiNode>().CountAsync());
        Assert.Equal(2, await again.Set<TaxiPath>().CountAsync());
    }

    [Fact]
    public async Task Refresh_NamesTheShipsTheWorldCouldNotSail()
    {
        string world = await OldWorldAsync(withShip: false);
        // No Kalimdor: the Theramore route cannot be built. Map 36 is there for the old world's Deadmines portal, so only the ships are
        // left to report.
        await AddMapsAsync(world, 0, 36);
        await using (WorldDbContext db = Open(world))
        {
            db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow { Entry = 20808, Type = 15, Name = "TEST Ship", Data0 = 292, Data1 = 0, Data2 = 1 });
            await db.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, ShipDump());
        string dbc = Dbcs(); // the portal's trigger
        // Without the map DBCs the refresh keeps the map table above (with them it would hold both continents: Map.dbc must list 0 and 1).
        File.Delete(Path.Combine(dbc, "Map.dbc"));
        File.Delete(Path.Combine(dbc, "AreaTable.dbc"));
        Assert.Equal(dbc, TaxiDbcs(withPath295: false));

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("check: transport 176231 (Proudmore's Treasure): path 292 sails on map 1, which has no map_template row", output, StringComparison.Ordinal);
        Assert.Contains("check: transport 176310 (Serenity's Shore): path 295 has 0 TaxiPathNode.dbc row(s), a route needs at least 3", output, StringComparison.Ordinal);
        Assert.Contains("check: transport 20808 (TEST Ship): speed 0 yd/s and acceleration 1 yd/s² (data1, data2) must both be positive", output, StringComparison.Ordinal);
        Assert.Contains("refresh: 3 reference check(s) to look at", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_WithoutTheTaxiDbcs_LeavesTheTaxiTablesAlone_AndSaysTheRoutesWereNotChecked()
    {
        string world = await OldWorldAsync(withShip: false);
        await using (WorldDbContext db = Open(world))
        {
            db.Set<TaxiNode>().Add(new TaxiNode { Id = 2, Name = "kept" });
            await db.SaveChangesAsync();
        }

        string dump = PathOf("world.sql");
        File.WriteAllText(dump, ShipDump());

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs());

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("no TaxiPathNode.dbc in", output, StringComparison.Ordinal);
        Assert.Contains("the ship routes (gameobject_template type 15, data0) are not checked", output, StringComparison.Ordinal);
        Assert.Contains("no TaxiNodes.dbc in", output, StringComparison.Ordinal);
        Assert.DoesNotContain("  taxi_nodes", output, StringComparison.Ordinal);
        await using WorldDbContext check = Open(world);
        Assert.Equal("kept", (await check.Set<TaxiNode>().SingleAsync()).Name);
        Assert.Equal(2, await check.Set<GameObjectTemplateRow>().CountAsync(r => r.Type == 15));
    }

    [Theory]
    [InlineData("Classic DB version 1.12.1 \"Melting Pot v2\". For Classic core z2815.", ProcCooldownUnit.Seconds)]
    [InlineData("Classic DB version 1.12.1. For Classic core z2829.", ProcCooldownUnit.Milliseconds)]
    [InlineData("Classic DB For Classic core z2900", ProcCooldownUnit.Milliseconds)]
    public void CooldownUnit_Auto_FollowsTheClassicDbRevision(string version, ProcCooldownUnit expected)
        => Assert.Equal(expected, ContentImporterCli.ResolveCooldownUnit(null, version));

    [Fact]
    public void AreaTriggerDbc_WithAnotherLayout_IsRefused()
    {
        string directory = Dbc("AreaTrigger.dbc", 9, [78, 0, 1f, 2f, 3f, 7f, 0f, 0f, 0f]);
        Assert.Throws<InvalidDataException>(() => AreaTriggerDbcReader.Load(Path.Combine(directory, "AreaTrigger.dbc")));
    }
}
