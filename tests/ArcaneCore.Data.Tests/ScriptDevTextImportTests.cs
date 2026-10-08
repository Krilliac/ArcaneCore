using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ScriptDevTextImportTests
{
    [Fact]
    public void ScriptWaypoints_AreKeptSeparateFromOrdinaryCreatureMovement()
    {
        const string dump = """
            INSERT INTO `script_waypoint` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`) VALUES
            (3849,0,12,-241.15,2154.67,90.62,1.15,2000,0);
            """;
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump));

        CreatureMovementTemplateRow point = Assert.Single(importer.PathSnapshot());
        Assert.Equal((3849u, 0x8000_0000u, 12u, 2000u), (point.Entry, point.PathId, point.Point, point.WaitTimeMs));
    }

    /// <summary>
    /// cmangos <c>waypoint_path</c> is keyed by path id alone (WaypointManager::GetPathFromOrigin, PATH_FROM_WAYPOINT_PATH): its rows land
    /// under entry 0 and path <c>0x40000000 | PathId</c>, apart from every entry's own and script paths, and are counted on their own.
    /// </summary>
    [Fact]
    public void WaypointPathRows_AreStoredUnderEntryZero_InTheirOwnNamespace()
    {
        const string dump = """
            INSERT INTO `waypoint_path` (`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
            (3678,12,-104.288,234.408,-91.6416,1.124,1000,0,'Spawn 1 Wave'),
            (3678,13,-104.288,234.408,-91.6416,5.74213,3000,367802,NULL),
            (1073741825,1,0,0,0,0,0,0,NULL);
            INSERT INTO `creature_movement_template` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`) VALUES
            (3678,3678,1,1,2,3,0,0,0);
            """;
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump));

        CreatureImportReport report = importer.BuildReport();
        Assert.Equal((3, 2), (report.MovementTemplates, report.WaypointPaths));
        CreatureMovementTemplateRow[] shared = [.. importer.PathSnapshot().Where(p => p.Entry == CreatureContent.WaypointPathEntry).OrderBy(p => p.Point)];
        Assert.Equal([(CreatureContent.WaypointPathBit | 3678u, 12u, 1000u), (CreatureContent.WaypointPathBit | 3678u, 13u, 3000u)],
            shared.Select(p => (p.PathId, p.Point, p.WaitTimeMs)));
        Assert.Contains(importer.PathSnapshot(), p => (p.Entry, p.PathId, p.Point) == (3678u, 3678u, 1u));
        Assert.Contains(report.Warnings, w => w.Contains("waypoint_path PathId 1073741825", StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptDevTextRow_BecomesAnAiTextWithoutLosingSoundOrBroadcastId()
    {
        const string dump = """
            INSERT INTO `script_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`broadcast_text_id`) VALUES
            (-1036000,'Synthetic line',5775,1,0,0,12345);
            """;
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump));

        CreatureAiTextRow text = Assert.Single(importer.AiSnapshot().Texts);
        Assert.Equal((-1036000, "Synthetic line", 5775u, 12345u),
            (text.Entry, text.Content, text.Sound, text.BroadcastTextId));
    }
}
