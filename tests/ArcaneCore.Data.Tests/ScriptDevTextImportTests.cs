using ArcaneCore.Data.World.Creatures;
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
