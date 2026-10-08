using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ScriptWaypointImportTests
{
    [Fact]
    public void ScriptDevEscortPaths_ImportForGrimstoneAndPhalanx_WithoutBlendingAnExplicitPath()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `script_waypoint` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`) VALUES
            (10096,0,1,604.8,-191.0,-54.0,0,0,0),
            (10096,0,2,608.0,-185.0,-54.0,0,5000,0),
            (9502,0,1,847.8,-230.0,-43.6,0,0,0),
            (10646,0,1,1,2,3,0,0,0),
            (10646,0,2,4,5,6,0,0,0);
            INSERT INTO `creature_movement_template` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`) VALUES
            (10646,0,1,7,8,9,0,0);
            """));

        // Wave-7 integration: script_waypoint paths live in their own namespace (CreatureContent.ScriptWaypointPathBit, sd2-low), so an
        // explicit creature_movement_template path is never blended with or replaced by them; the escort reads them through GetScriptWaypoints.
        const uint script = CreatureContent.ScriptWaypointPathBit;
        var paths = importer.PathSnapshot();
        Assert.Equal(2, paths.Count(p => p.Entry == 10096 && p.PathId == script));
        Assert.Single(paths, p => p.Entry == 9502 && p.PathId == script);
        Assert.Single(paths, p => p.Entry == 10646 && p.PathId == 0 && p.X == 7f);
        Assert.Equal(2, paths.Count(p => p.Entry == 10646 && p.PathId == script));
        Assert.Equal(6, paths.Count);
    }
}
