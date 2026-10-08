using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Maps;
using Xunit;

namespace ArcaneCore.Data.Tests.Maps;

/// <summary>
/// The dungeon columns of <c>map_template</c> that Map.dbc does not carry (parent, player limit, reset delay, ghost entrance, script),
/// read from a cmangos classic-db dump's <c>instance_template</c> or a vmangos dump's own <c>map_template</c>. The rows are real
/// classic-db z2815 and vmangos tuples.
/// </summary>
public sealed class InstanceTemplateDumpImporterTests
{
    private const string ClassicDb = """
        CREATE TABLE `instance_template` (
          `map` smallint unsigned NOT NULL,
          `parent` smallint unsigned NOT NULL DEFAULT '0',
          `levelMin` tinyint unsigned NOT NULL DEFAULT '0',
          `levelMax` tinyint unsigned NOT NULL DEFAULT '0',
          `maxPlayers` tinyint unsigned NOT NULL DEFAULT '0',
          `reset_delay` int unsigned NOT NULL DEFAULT '0' COMMENT 'Reset time in days',
          `ghostEntranceMap` smallint unsigned NOT NULL,
          `ghostEntranceX` float NOT NULL,
          `ghostEntranceY` float NOT NULL,
          `ScriptName` varchar(128) NOT NULL DEFAULT '',
          `mountAllowed` tinyint unsigned NOT NULL DEFAULT '0',
          PRIMARY KEY (`map`)
        ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3;
        INSERT INTO `instance_template` VALUES (36,0,17,26,10,0,0,-11207.8,1681.15,'instance_deadmines',1),(43,0,17,24,10,0,1,-751.131,-2209.24,'instance_wailing_caverns',0),(533,0,60,60,40,7,0,0,0,'instance_naxxramas',0),(489,0,0,0,0,0,0,0,0,'',0);
        """;

    // vmangos map_template (20171129015531_world.sql, columns as renamed by 20190123062532 and 20210220230709): one row per map and patch.
    private const string Vmangos = """
        INSERT INTO `map_template` (`entry`, `patch`, `parent`, `map_type`, `linked_zone`, `player_limit`, `reset_delay`, `ghost_entrance_map`, `ghost_entrance_x`, `ghost_entrance_y`, `map_name`, `script_name`) VALUES
        	(0, 0, 0, 0, 0, 0, 0, -1, 0, 0, 'Eastern Kingdoms', ''),
        	(36, 0, 0, 1, 0, 40, 0, 0, -11207.8, 1681.15, 'Deadmines', 'instance_deadmines'),
        	(36, 1, 0, 1, 0, 10, 0, 0, -11207.8, 1681.15, 'Deadmines', 'instance_deadmines'),
        	(229, 0, 0, 1, 1583, 40, 0, 0, -7522.53, -1233.04, 'Blackrock Spire', 'instance_blackrock_spire'),
        	(229, 1, 0, 1, 1583, 15, 0, 0, -7522.53, -1233.04, 'Blackrock Spire', 'instance_blackrock_spire'),
        	(229, 8, 0, 1, 1583, 10, 0, 0, -7522.53, -1233.04, 'Blackrock Spire', 'instance_blackrock_spire'),
        	(229, 11, 0, 1, 1583, 99, 0, 0, -7522.53, -1233.04, 'Blackrock Spire', 'later'),
        	(533, 0, 0, 2, 3456, 40, 7, -1, 0, 0, 'Naxxramas', 'instance_naxxramas');
        """;

    [Fact]
    public void ClassicDb_InstanceTemplate_GivesTheDungeonColumns_AndNoGhostEntranceForAZeroOne()
    {
        var importer = new InstanceTemplateDumpImporter();
        importer.Read(new StringReader(ClassicDb));

        Assert.True(importer.SawTable);
        Assert.Equal([36u, 43u, 489u, 533u], importer.Maps.Keys.Order());
        MapInstanceData deadmines = importer.Maps[36];
        Assert.Equal((0u, 10u, 0u, 0, -11207.8f, 1681.15f, "instance_deadmines"),
            (deadmines.Parent, deadmines.PlayerLimit, deadmines.ResetDelay, deadmines.GhostEntranceMap, deadmines.GhostEntranceX,
                deadmines.GhostEntranceY, deadmines.ScriptName));
        Assert.Equal(1, importer.Maps[43].GhostEntranceMap);

        // cmangos' ghostEntranceMap is unsigned: (0, 0, 0) is its "no entrance", vmangos' -1 (Naxxramas, the battlegrounds).
        Assert.Equal((-1, 40u, 7u), (importer.Maps[533].GhostEntranceMap, importer.Maps[533].PlayerLimit, importer.Maps[533].ResetDelay));
        Assert.Equal(-1, importer.Maps[489].GhostEntranceMap);
    }

    /// <summary>
    /// classic-db z2815 gives Blackrock Spire (229) <c>reset_delay</c> 3; vmangos removed that global reset on purpose ("Blackrock Spire no
    /// reset", sql/old_migrations/20170917193208_world.sql, <c>UPDATE map_template SET ResetDelay=0 WHERE Entry=229</c>). The importer
    /// follows vmangos and leaves every other reset delay as the dump has it.
    /// </summary>
    [Fact]
    public void ClassicDb_BlackrockSpire_GetsVmangosNoReset_AndOtherResetDelaysStay()
    {
        var importer = new InstanceTemplateDumpImporter();
        importer.Read(new StringReader(
            "INSERT INTO `instance_template` (`map`,`parent`,`levelMin`,`levelMax`,`maxPlayers`,`reset_delay`,`ghostEntranceMap`,`ghostEntranceX`,`ghostEntranceY`,`ScriptName`,`mountAllowed`) " +
            "VALUES (229,0,55,0,10,3,0,-7522.53,-1233.04,'instance_blackrock_spire',0),(533,0,60,60,40,7,0,0,0,'instance_naxxramas',0),(409,0,60,60,40,7,0,-7510.56,-1036.7,'instance_molten_core',0);"));

        MapInstanceData spire = importer.Maps[229];
        Assert.Equal((0u, 10u, 0u, 0, "instance_blackrock_spire"), (spire.Parent, spire.PlayerLimit, spire.ResetDelay, spire.GhostEntranceMap, spire.ScriptName));
        Assert.Equal((7u, 7u), (importer.Maps[533].ResetDelay, importer.Maps[409].ResetDelay));
        Assert.Equal(["map 229 reset_delay 3 -> 0 (vmangos: Blackrock Spire has no global reset)"], importer.Corrections);
    }

    [Fact]
    public void Vmangos_MapTemplate_TakesTheNewestPatchUpToTheLastOne()
    {
        var importer = new InstanceTemplateDumpImporter();
        importer.Read(new StringReader(Vmangos));

        Assert.True(importer.SawTable);
        Assert.Equal(10u, importer.Maps[36].PlayerLimit); // patch 1 (1.3) over patch 0
        Assert.Equal(10u, importer.Maps[229].PlayerLimit); // patch 8 (1.10); patch 11 is past 1.12 (patch 10)
        Assert.Equal("instance_blackrock_spire", importer.Maps[229].ScriptName);
        Assert.Equal((-1, 7u), (importer.Maps[533].GhostEntranceMap, importer.Maps[533].ResetDelay));
        Assert.Equal(-1, importer.Maps[0].GhostEntranceMap);
        Assert.Equal(0u, importer.Maps[229].ResetDelay);
        Assert.Empty(importer.Corrections); // vmangos already has no Blackrock Spire reset: nothing to correct
    }

    [Fact]
    public void ADumpWithoutEitherTable_LeavesNothing()
    {
        var importer = new InstanceTemplateDumpImporter();
        importer.Read(new StringReader("INSERT INTO `areatrigger_tavern` (`id`,`name`) VALUES (71,'inn');"));

        Assert.False(importer.SawTable);
        Assert.Empty(importer.Maps);
    }

    [Fact]
    public void AMalformedNumber_IsRefused()
    {
        var importer = new InstanceTemplateDumpImporter();
        Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `instance_template` (`map`,`parent`,`maxPlayers`,`reset_delay`,`ghostEntranceMap`,`ghostEntranceX`,`ghostEntranceY`,`ScriptName`) VALUES (36,0,'ten',0,0,1,2,'');")));
    }
}
