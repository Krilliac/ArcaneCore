using System.Numerics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Progression;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// A creature that drops a missing crafting reagent is hunted only if a walk can reach one of its spawns (GitHub #1441: a dropper on another
/// landmass was picked again and again). <see cref="PlayerbotCrafting.AllSpawnsUnreachable"/> is the filter; the reachability itself is
/// <c>PlayerbotNavigation.IsUnreachableOnFoot</c>, which the picker passes in.
/// </summary>
public sealed class PlayerbotCraftingDropperReachTests
{
    private static CreatureSpawn Spawn(uint guid, float x) => new() { Guid = guid, Entry = 100, MapId = 0, X = x, Y = 0f, Z = 0f };

    [Fact]
    public void ADropperWithEverySpawnUnreachable_IsSkipped()
    {
        CreatureSpawn[] spawns = [Spawn(1, 1000f), Spawn(2, 1100f)];

        Assert.True(PlayerbotCrafting.AllSpawnsUnreachable(spawns, at => at.X >= 1000f));
    }

    [Fact]
    public void ADropperWithOneReachableSpawn_IsKept()
    {
        CreatureSpawn[] spawns = [Spawn(1, 1000f), Spawn(2, 10f)];

        Assert.False(PlayerbotCrafting.AllSpawnsUnreachable(spawns, at => at.X >= 1000f));
    }

    [Fact]
    public void ADropperWithNoSpawn_IsNotJudgedHere_AndTheReachabilityIsNeverAsked()
    {
        int asked = 0;

        Assert.False(PlayerbotCrafting.AllSpawnsUnreachable([], _ => { asked++; return true; }));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheSearch_StopsAtTheFirstReachableSpawn_AndAsksWithTheSpawnsPosition()
    {
        var seen = new List<Vector3>();
        CreatureSpawn[] spawns = [Spawn(1, 5f), Spawn(2, 6f)];

        Assert.False(PlayerbotCrafting.AllSpawnsUnreachable(spawns, at => { seen.Add(at); return false; }));

        Assert.Equal([new Vector3(5f, 0f, 0f)], seen);
    }
}
