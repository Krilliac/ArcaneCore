using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
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
            INSERT INTO `gossip_texts` (`entry`,`content_default`,`content_loc1`,`comment`) VALUES
            (-3090000,'I am ready to begin.',NULL,'emi shortfuse GOSSIP_ITEM_START'),(-3090001,'Unrelated option',NULL,'other');
            """));

        // Wave-7 integration: every script_waypoint path and script_texts row is imported (sd2-low), the paths under the script_waypoint
        // namespace (CreatureContent.ScriptWaypointPathBit); only the gossip_texts rows stay limited to the ported option line.
        Assert.Equal([4508u, 6575u, 7998u, 8516u, 9000u], importer.PathSnapshot().Select(r => r.Entry).Order());
        Assert.All(importer.PathSnapshot(), r => Assert.Equal(CreatureContent.ScriptWaypointPathBit, r.PathId));
        Assert.Equal(8, importer.AiSnapshot().Texts.Count);
        Assert.Contains(importer.AiSnapshot().Texts, t => t.Entry == -999999);
        Assert.Equal("I am ready to begin.", Assert.Single(importer.AiSnapshot().Texts, t => t.Entry == -3090000).Content);
        Assert.DoesNotContain(importer.AiSnapshot().Texts, t => t.Entry == -3090001);
        Assert.Equal([RelayScriptCatalog.EventRelayId(2488), RelayScriptCatalog.EventRelayId(2609)],
            importer.RelaySnapshot().Steps.Select(s => s.Id).Order());
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
            .Select(entry => importer.PathSnapshot().Count(row => row.Entry == entry && row.PathId == CreatureContent.ScriptWaypointPathBit)));
        int[] requiredTextIds = [-1000003, -1090000, -1189005, -1129005, -1047000, -1070001, -1209000];
        foreach (int id in requiredTextIds)
            Assert.Contains(importer.AiSnapshot().Texts, row => row.Entry == id);
        Assert.Equal(2, importer.RelaySnapshot().Steps.Count(row => row.Id == RelayScriptCatalog.EventRelayId(2488)));
        Assert.Equal(109, importer.RelaySnapshot().Steps.Count(row => row.Id == RelayScriptCatalog.EventRelayId(2609)));
        Assert.Contains(importer.AiSnapshot().Texts, row => row.Entry == -3090000 && row.Content == "I am ready to begin.");
        // No real relay reaches the block reserved for event scripts: every dbscripts_on_relay id kept is below it, none was refused.
        Assert.All(importer.RelaySnapshot().Steps.Where(row => row.Id != RelayScriptCatalog.EventRelayId(2488)
                && row.Id != RelayScriptCatalog.EventRelayId(2609)),
            row => Assert.False(RelayScriptCatalog.IsEventRelayId(row.Id)));
        Assert.DoesNotContain(importer.BuildReport().Warnings, w => w.Contains("reserved for dbscripts_on_event"));
        Assert.True(importer.RelaySnapshot().Steps.Max(row => row.Id < RelayScriptCatalog.EventRelayIdOffset ? row.Id : 0) > 1_000_000);
    }

    [Fact]
    public void Importer_RefusesARealRelayInTheBlockReservedForEventScripts_InEitherOrder()
    {
        uint reserved = RelayScriptCatalog.EventRelayId(2609);
        foreach (bool relayFirst in new[] { true, false })
        {
            string relay = $"""
                INSERT INTO `dbscripts_on_relay` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`buddy_entry`,`search_radius`,`data_flags`,`x`,`y`,`z`,`o`) VALUES
                ({reserved},0,0,0,1,0,0,0,0,0,0,0,0),(1050801,0,0,0,2,0,0,0,0,0,0,0,0);
                """;
            const string Event = """
                INSERT INTO `dbscripts_on_event` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`buddy_entry`,`search_radius`,`data_flags`,`x`,`y`,`z`,`o`) VALUES
                (2609,0,0,11,2090049,9000000,0,0,0,0,0,0,0);
                """;
            var importer = new CreatureDumpImporter();
            importer.Read(new StringReader(relayFirst ? relay + Event : Event + relay));

            RelayScriptRow kept = Assert.Single(importer.RelaySnapshot().Steps, row => row.Id == reserved);
            Assert.Equal(11u, kept.Command); // the event script, not the stray relay
            Assert.Contains(importer.RelaySnapshot().Steps, row => row.Id == 1050801);
            Assert.Contains(importer.BuildReport().Warnings, w => w.Contains($"dbscripts_on_relay {reserved}"));
        }
    }

    [Fact]
    public void EventRelayBlock_SitsAboveEveryClassicRelayId_AndRefusesEventIdsThatOverflowIt()
    {
        Assert.True(RelayScriptCatalog.EventRelayIdOffset > 1_574_201); // z2815's highest dbscripts_on_relay id
        Assert.True(RelayScriptCatalog.EventRelayId(2609) <= int.MaxValue); // EventAI and relay commands may carry it as an int
        Assert.False(RelayScriptCatalog.IsEventRelayId(1_574_201));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelayScriptCatalog.EventRelayId(uint.MaxValue));
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
