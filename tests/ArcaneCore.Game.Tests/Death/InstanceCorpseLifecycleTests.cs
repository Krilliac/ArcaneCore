using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.Instances;
using ArcaneCore.Kernel.Characters;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// An online ghost's body and the life of its dungeon instance. vmangos keeps every corpse in ObjectAccessor, outside the grids:
/// a map that unloads takes the corpse out of its grid but the owner keeps it (with its instance id), and the next map of that
/// instance puts it back (ObjectAccessor::AddCorpsesToGrid, ObjectAccessor.cpp:225-245, run by Map::EnsureGridLoaded). A save that is
/// reset or deleted leaves the body where it is but no other instance may ever take it: the body forgets the gone instance id
/// (instance ids are handed out again after a restart), in memory and in the stored corpse row.
/// </summary>
public sealed class InstanceCorpseLifecycleTests
{
    private static CorpseSnapshot Body(uint instanceId) =>
        new(Dungeon, -16f, -383f, 61f, 0f, Start - 10, (byte)CorpseType.ResurrectablePve, instanceId);

    private static InstanceFixture Create()
    {
        var f = new InstanceFixture(new InstanceOptions { UnloadDelayMs = 1000 });
        DeathHooks.Register(f.World, new DeathHooks(new DeathOptions(), new FixedDeathClock(Start)));
        return f;
    }

    /// <summary>a ran the dungeon alone and walked out; the map unloaded; a is now a ghost on the continent whose body lies in that instance (loaded again for it).</summary>
    private static (Player A, uint Instance, Map Map) GhostWithItsBodyInTheInstance(InstanceFixture f)
    {
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        uint id = a.Map!.InstanceId;
        Assert.True(f.LeaveToContinent(a));
        f.Tick(600);
        f.Tick(600);
        Assert.Null(f.World.FindMap(Dungeon, id));

        f.World.GetMap(0).Combat.RestoreGhost(a, Body(id));
        Map map = Assert.IsType<Map>(f.World.FindMap(Dungeon, id));
        Assert.Same(map, a.Combat.Corpse!.Map);
        return (a, id, map);
    }

    [Fact]
    public void AnInstanceMapThatUnloads_LeavesTheBodyWithItsOnlineGhost_OutsideEveryMap()
    {
        using InstanceFixture f = Create();
        (Player a, uint id, Map map) = GhostWithItsBodyInTheInstance(f);
        Corpse body = a.Combat.Corpse!;

        f.Tick(600);
        f.Tick(600);

        Assert.True(map.IsUnloaded);
        Assert.Same(body, a.Combat.Corpse);
        Assert.Null(body.Map);
        Assert.Equal((Dungeon, id), (body.MapId, body.InstanceId));
        CorpseSnapshot saved = Assert.IsType<CorpseSnapshot>(PlayerLife.Capture(a).Corpse);
        Assert.Equal((Dungeon, id), (saved.MapId, saved.InstanceId));
    }

    [Fact]
    public void TheBody_GoesBackIntoItsInstance_WhenThatInstanceMapIsCreatedAgain()
    {
        using InstanceFixture f = Create();
        (Player a, uint id, _) = GhostWithItsBodyInTheInstance(f);
        Corpse body = a.Combat.Corpse!;
        f.Tick(600);
        f.Tick(600);
        Assert.Null(body.Map);

        Map again = Assert.IsType<Map>(f.Manager.ResolveCorpseMap(Dungeon, id));

        Assert.Same(again, body.Map);
        Assert.Contains(body, again.Combat.Corpses);
        Assert.Same(body, a.Combat.Corpse);
    }

    [Fact]
    public void AGhostWhoLoggedOut_IsNotGivenItsBodyBack_ByALaterMapOfThatInstance()
    {
        using InstanceFixture f = Create();
        (Player a, uint id, _) = GhostWithItsBodyInTheInstance(f);
        Corpse body = a.Combat.Corpse!;
        f.World.RemovePlayer(a);
        f.Tick(600);
        f.Tick(600);

        Map? again = f.Manager.ResolveCorpseMap(Dungeon, id);

        Assert.NotNull(again);
        Assert.Empty(again!.Combat.Corpses);
        Assert.Null(body.Map);
    }

    [Fact]
    public void AResetSave_KeepsTheBody_ButItNoLongerPointsAtTheGoneInstance()
    {
        using InstanceFixture f = Create();
        (Player a, uint id, Map map) = GhostWithItsBodyInTheInstance(f);
        Corpse body = a.Combat.Corpse!;

        f.Manager.HandleResetInstances(a); // nobody inside: the save goes now, the map at its next update

        Assert.False(f.Manager.IsSaveLive(Dungeon, id));
        Assert.Same(body, a.Combat.Corpse);
        Assert.Equal(0u, body.InstanceId);
        CorpseSnapshot saved = Assert.IsType<CorpseSnapshot>(PlayerLife.Capture(a).Corpse);
        Assert.Equal((Dungeon, 0u), (saved.MapId, saved.InstanceId));

        f.Tick(50);
        f.Tick(50);
        Assert.True(map.IsUnloaded);
        Assert.Same(body, a.Combat.Corpse);
        Assert.Null(body.Map);

        // A new instance of the dungeon is somebody else's: the body does not appear in it.
        Player b = f.AddPlayer(2);
        Assert.True(f.EnterDungeon(b));
        Assert.Empty(b.Map!.Combat.Corpses);
        Assert.Null(body.Map);
    }
}
