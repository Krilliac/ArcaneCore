using ArcaneCore.Data.World.Creatures;
using System.IO.Compression;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ScriptDev2DungeonContentTests
{
    [Fact]
    public void Importer_MapsOnlyTheRequestedDungeonEscortPathsAndTexts()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `script_waypoint` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
            (7998,0,1,1,2,3,0,500,0,'Emi'),(8516,0,24,4,5,6,0,1000,0,'Belnistrasz'),
            (4508,0,46,7,8,9,0,0,0,'Willix'),(6575,0,1,10,11,12,0,0,0,'trainee'),
            (9000,0,1,1,1,1,0,0,0,'unrelated script');
            INSERT INTO `script_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`broadcast_text_id`,`comment`) VALUES
            (-1090000,'Start!',0,0,0,0,0,'Emi'),(-1189005,'Attack!',0,0,0,0,0,'Mograine'),
            (-1129005,'Ready!',0,0,0,0,0,'Belnistrasz'),(-1047000,'Ready!',0,0,0,0,0,'Willix'),
            (-1070001,'Awake!',0,0,0,0,0,'Archaedas'),(-1209000,'Intro!',0,0,0,0,0,'Zumrah'),
            (-999999,'Unrelated',0,0,0,0,0,'other dungeon');
            INSERT INTO `dbscripts_on_event` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`buddy_entry`,`search_radius`,`data_flags`,`x`,`y`,`z`,`o`) VALUES
            (2488,2000,0,10,7273,0,0,0,0,1,2,3,0),(2609,0,0,11,2090049,9000000,0,0,0,0,0,0,0),
            (9999,0,0,10,1234,0,0,0,0,0,0,0,0);
            """));

        Assert.Equal([4508u, 6575u, 7998u, 8516u], importer.PathSnapshot().Select(r => r.Entry).Order());
        Assert.Equal(6, importer.AiSnapshot().Texts.Count);
        Assert.DoesNotContain(importer.AiSnapshot().Texts, t => t.Entry == -999999);
        Assert.Equal([1002488u, 1002609u], importer.RelaySnapshot().Steps.Select(s => s.Id).Order());
    }

    [ClassicDbDumpFact]
    public void RealClassicDb_DungeonScriptRowsArePresentWhenTheDumpIsProvided()
    {
        string path = Environment.GetEnvironmentVariable("ARCANECORE_CLASSICDB_DUMP")!;
        using FileStream file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        var importer = new CreatureDumpImporter();
        importer.Read(reader);

        Assert.Equal([19, 25, 47, 12], new[] { 7998u, 8516u, 4508u, 6575u }
            .Select(entry => importer.PathSnapshot().Count(row => row.Entry == entry)));
        int[] requiredTextIds = [-1090000, -1189005, -1129005, -1047000, -1070001, -1209000];
        foreach (int id in requiredTextIds)
            Assert.Contains(importer.AiSnapshot().Texts, row => row.Entry == id);
        Assert.Equal(2, importer.RelaySnapshot().Steps.Count(row => row.Id == 1_002_488));
        Assert.Equal(109, importer.RelaySnapshot().Steps.Count(row => row.Id == 1_002_609));
    }

    private sealed class ClassicDbDumpFactAttribute : FactAttribute
    {
        public ClassicDbDumpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARCANECORE_CLASSICDB_DUMP")))
                Skip = "Set ARCANECORE_CLASSICDB_DUMP to the z2815 SQL dump to check the real ScriptDev2 rows.";
        }
    }
}
