using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The source layouts of ClassicDB z2815's quest, gossip, event and ScriptDev2 waypoint tables.</summary>
public sealed class DbScriptDumpImporterTests
{
    private const string ScriptColumns =
        "`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,"
        + "`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`";

    [Fact]
    public void NaralexWaypointScript_IsImportedInItsOwnNamespace()
    {
        // ClassicDB z2815: 367802 points and speaks (text template 1257) on arrival.
        string dump = $"INSERT INTO `dbscripts_on_creature_movement` ({ScriptColumns}) VALUES "
            + "(367802,0,0,1,25,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'point'),"
            + "(367802,0,1,0,0,0,0,0,0,0,1257,0,0,0,0,0,0,0,0,0,0,'text');";
        var importer = new DbScriptDumpImporter();
        importer.Read(new StringReader(dump));

        Assert.Equal(2, importer.Counts[DbScriptDataModule.CreatureMovementTable]);
        Assert.Equal([1u, 0u], importer.Scripts.Where(s => s.Kind == DbScriptKind.CreatureMovement)
            .Select(s => s.Step.Command).ToArray());
    }

    [Fact]
    public void ParsesFourIndependentNamespaces_AndKeepsScriptWaypointSeparateFromPatrols()
    {
        string dump = $"""
            INSERT INTO `dbscripts_on_quest_start` ({ScriptColumns}) VALUES
            (68,3000,0,26,0,0,0,2044,25,0,0,0,0,0,0,0,0,0,0,0,0,'attack');
            INSERT INTO `dbscripts_on_quest_end` ({ScriptColumns}) VALUES
            (112,4000,0,1,69,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'emote');
            INSERT INTO `dbscripts_on_gossip` ({ScriptColumns}) VALUES
            (5,0,0,15,21100,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'letter');
            INSERT INTO `dbscripts_on_event` ({ScriptColumns}) VALUES
            (364,0,0,10,2624,90000,0,0,0,0,0,0,0,0,0,-12179.4,644.22,-67.1,5.18,0,0,'Gazban');
            INSERT INTO `script_waypoint` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
            (349,0,1,-8769.59,-2185.73,141.975,0,500,0,'escort');
            """;
        var importer = new DbScriptDumpImporter();
        importer.Read(new StringReader(dump));

        Assert.Equal(4, importer.Scripts.Count);
        Assert.Contains(importer.Scripts, s => s.Kind == DbScriptKind.QuestStart && s.Step.Id == 68 && s.Step.Command == 26 && s.Step.BuddyEntry == 2044);
        Assert.Contains(importer.Scripts, s => s.Kind == DbScriptKind.QuestEnd && s.Step.Id == 112 && s.Step.DelayMs == 4000);
        Assert.Contains(importer.Scripts, s => s.Kind == DbScriptKind.Gossip && s.Step.Id == 5 && s.Step.DataLong == 21100);
        Assert.Contains(importer.Scripts, s => s.Kind == DbScriptKind.Event && s.Step.Id == 364 && s.Step.X == -12179.4f);
        var path = Assert.Single(importer.ScriptWaypoints);
        Assert.Equal((349u, 0u, 1u, -8769.59f, 500u), (path.Entry, path.PathId, path.Point.Point, path.Point.X, path.Point.WaitTimeMs));
    }

    [Fact]
    public void AScriptWaypointPathWithAPointZero_IsRejectedWhole_AndALaterDumpReplacesAScript()
    {
        // Synthetic rows: mangos-classic LoadScriptWaypoints blacklists the (entry, path) of a point 0 and skips all its points.
        string dump = $"""
            INSERT INTO `script_waypoint` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
            (900,0,0,1,1,1,0,0,0,'bad'),(900,0,1,2,2,2,0,0,0,'dropped'),(900,1,1,3,3,3,0,0,0,'other path kept'),(901,0,1,4,4,4,0,0,7,'kept');
            INSERT INTO `dbscripts_on_gossip` ({ScriptColumns}) VALUES
            (5,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'old a'),(5,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'old b');
            """;
        string patch = $"""
            INSERT INTO `dbscripts_on_gossip` ({ScriptColumns}) VALUES
            (5,0,0,0,3,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'new');
            """;
        var importer = new DbScriptDumpImporter();
        importer.Read(new StringReader(dump));
        importer.Read(new StringReader(patch));

        Assert.Equal([(900u, 1u), (901u, 0u)], importer.ScriptWaypoints.Select(w => (w.Entry, w.PathId)).Order());
        Assert.Equal(7u, importer.ScriptWaypoints.Single(w => w.Entry == 901).Point.ScriptId);
        Assert.Equal(3u, Assert.Single(importer.Scripts).Step.DataLong);
    }
}
