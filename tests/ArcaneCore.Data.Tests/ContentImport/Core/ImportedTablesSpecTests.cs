using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.Content.Import;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>
/// Every table the importer counts as imported has a <see cref="TableSpec"/>: without one `plan` listed it under "not read by any
/// importer yet" and the scan checked none of its keys.
/// </summary>
public sealed class ImportedTablesSpecTests
{
    [Theory]
    [InlineData("game_weather")]
    [InlineData("exploration_basexp")]
    [InlineData("game_event")]
    [InlineData("game_event_time")]
    [InlineData("game_event_creature")]
    [InlineData("game_event_gameobject")]
    [InlineData("game_event_creature_data")]
    [InlineData("game_event_quest")]
    [InlineData("game_event_mail")]
    [InlineData("transports")]
    [InlineData("areatrigger_tavern")]
    public void TheImportedTableHasASpec(string table) => Assert.NotNull(ContentTableSpecs.Find(table));

    [Fact]
    public void Scan_ReportsTheseTablesAsReadAndCountsDuplicateKeys()
    {
        const string Sql =
            "INSERT INTO `game_weather` (`zone`,`spring_rain_chance`) VALUES (12,5),(12,6),(14,1);\n" +
            "INSERT INTO `game_event` (`entry`,`occurence`,`length`,`description`) VALUES (1,5184000,43200,'a'),(2,1,1,'b');\n" +
            "INSERT INTO `game_event_mail` (`event`,`raceMask`,`quest`,`mailTemplateId`,`senderEntry`) VALUES (1,1,0,2,3),(1,2,0,2,3);\n" +
            "INSERT INTO `areatrigger_tavern` (`id`,`name`) VALUES (71,'x'),(72,'y');\n";

        ScanResult scan = ContentScanner.Scan([DumpInput.Text("t.sql", Sql)]);

        foreach (string table in new[] { "game_weather", "game_event", "game_event_mail", "areatrigger_tavern" })
        {
            Assert.True(scan.Tables[table].HasSpec, table);
        }

        Assert.Equal(1, scan.Tables["game_weather"].DuplicateKeys);
        Assert.Contains("spring_rain_chance", scan.Tables["game_weather"].MappedColumns);
        Assert.Contains("name", scan.Tables["areatrigger_tavern"].UnmappedColumns);
        Assert.Equal(0, scan.Tables["game_event_mail"].DuplicateKeys);
    }

    [Fact]
    public void Scan_AcceptsTransportsWithoutABuildColumn_AndSeparatesBuilds()
    {
        ScanResult withoutBuild = ContentScanner.Scan([DumpInput.Text("a.sql",
            "INSERT INTO `transports` (`entry`,`name`,`period`) VALUES (1,'a',10),(2,'b',20);")]);
        ScanResult withBuild = ContentScanner.Scan([DumpInput.Text("b.sql",
            "INSERT INTO `transports` (`entry`,`name`,`period`,`build`) VALUES (1,'a',10,3368),(1,'a',11,5875);")]);

        Assert.Equal(2, withoutBuild.Tables["transports"].DistinctKeys);
        Assert.Equal(2, withBuild.Tables["transports"].DistinctKeys);
    }

    [Fact]
    public void Scan_RejectsAGameWeatherTableWithoutItsZoneKey()
    {
        Assert.Throws<ImportSchemaException>(() => ContentScanner.Scan([DumpInput.Text("c.sql",
            "INSERT INTO `game_weather` (`id`,`spring_rain_chance`) VALUES (1,5),(2,6);")]));
    }

    [Fact]
    public void SpecKeyReader_ReadsAnOptionalKeyPartTheDumpLacksAsZero()
    {
        var row = new DumpRow("transports", ["entry", "name", "period"], ["7", "a", "10"]);

        uint[] key = new SpecKeyReader().Read(row, "transports");

        Assert.Equal([7u, 0u], key);
    }

    [Fact]
    public void Scan_KeysAMissingOptionalPartLikeAnExplicitZero()
    {
        ScanResult scan = ContentScanner.Scan([DumpInput.Text("d.sql",
            "INSERT INTO `transports` (`entry`,`name`,`period`) VALUES (1,'a',10);\n" +
            "INSERT INTO `transports` (`entry`,`name`,`period`,`build`) VALUES (1,'a',11,0),(2,'b',12,0);")]);

        Assert.Equal(2, scan.Tables["transports"].DistinctKeys);
        Assert.Equal(1, scan.Tables["transports"].DuplicateKeys);
    }

    [Fact]
    public void Scan_RejectsGameEventMailWithoutRaceMaskAndQuest()
    {
        Assert.Throws<ImportSchemaException>(() => ContentScanner.Scan([DumpInput.Text("e.sql",
            "INSERT INTO `game_event_mail` (`event`,`mailTemplateId`,`senderEntry`) VALUES (1,2,3);")]));
    }
}
