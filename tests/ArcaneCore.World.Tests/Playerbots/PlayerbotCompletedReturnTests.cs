using System.Numerics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotCompletedReturnTests
{
    [Fact]
    public void HomeBoundBot_KeepsLocalCompletedReturns_ButGivesUpFarBreadcrumbs()
    {
        var home = new PlayerbotHomeArea(1, new Vector3(0, 0, 0), 750, "home");
        CreatureSpawn inside = Spawn(1, 500);
        CreatureSpawn nearbyOutside = Spawn(1, 1000);
        CreatureSpawn far = Spawn(1, 3000);
        CreatureSpawn otherMap = Spawn(0, 100);
        Assert.True(PlayerbotQuestGoals.ReasonableReturnTrip(home, 1, 0, 0, inside));
        Assert.True(PlayerbotQuestGoals.ReasonableReturnTrip(home, 1, 500, 0, nearbyOutside));
        Assert.False(PlayerbotQuestGoals.ReasonableReturnTrip(home, 1, 0, 0, far));
        Assert.False(PlayerbotQuestGoals.ReasonableReturnTrip(home, 1, 0, 0, otherMap));
    }

    private static CreatureSpawn Spawn(uint map, float x)
        => new() { Guid = 1, Entry = 1, MapId = map, X = x, Y = 0, Z = 0 };
}
