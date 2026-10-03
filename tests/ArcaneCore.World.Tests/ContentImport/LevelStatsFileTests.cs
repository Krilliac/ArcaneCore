using ArcaneCore.Data.Content.Import;
using ArcaneCore.Game.Progression;
using Xunit;

namespace ArcaneCore.World.Tests.ContentImport;

/// <summary>
/// The level-stats file the content importer writes is the file <c>Progression:LevelStatsPath</c>
/// loads: the importer (Data) and the parser (Game) agree on the format, checked end to end on
/// hand-written rows.
/// </summary>
public sealed class LevelStatsFileTests
{
    private const string Dump = """
        CREATE TABLE `player_classlevelstats` (`class` tinyint unsigned NOT NULL, `level` tinyint unsigned NOT NULL, `basehp` mediumint unsigned NOT NULL, `basemana` mediumint unsigned NOT NULL, PRIMARY KEY (`class`, `level`));
        INSERT INTO `player_classlevelstats` VALUES (1,1,60,0),(1,2,80,0),(5,1,52,85);
        CREATE TABLE `player_levelstats` (`race` tinyint unsigned NOT NULL, `class` tinyint unsigned NOT NULL, `level` tinyint unsigned NOT NULL, `str` tinyint unsigned NOT NULL, `agi` tinyint unsigned NOT NULL, `sta` tinyint unsigned NOT NULL, `inte` tinyint unsigned NOT NULL, `spi` tinyint unsigned NOT NULL, PRIMARY KEY (`race`, `class`, `level`));
        INSERT INTO `player_levelstats` VALUES (1,1,2,23,20,22,20,21),(1,1,1,22,20,22,20,21),(4,5,1,17,25,19,20,22);
        """;

    [Fact]
    public void TheImportersFile_LoadsAsTheProgressionLevelStatsTable()
    {
        var importer = new PlayerCreateDumpImporter();
        importer.Read(new StringReader(Dump));
        var text = new StringWriter();
        Assert.Equal(3, importer.WriteLevelStats(text));

        PlayerLevelStatsTable table = PlayerLevelStatsTable.Parse(new StringReader(text.ToString()));

        Assert.Equal(3, table.Count);
        Assert.Equal(new PlayerLevelStats(80, 0, 23, 20, 22, 20, 21), table.Find(1, 1, 2));
        Assert.Equal(new PlayerLevelStats(52, 85, 17, 25, 19, 20, 22), table.Find(4, 5, 1));
        Assert.Null(table.Find(2, 1, 1));
    }
}
