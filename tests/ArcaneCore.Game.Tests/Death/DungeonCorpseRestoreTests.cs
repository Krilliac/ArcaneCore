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
/// A relogged ghost whose body lies in a dungeon instance gets it back in that instance without the corpse ever creating a map
/// the instance manager does not manage. vmangos keeps the corpse (ObjectAccessor) and adds it to the grid only when its
/// instance's map is next created; it never creates an instance map for a corpse.
/// </summary>
public sealed class DungeonCorpseRestoreTests
{
    private static CorpseSnapshot Body(uint instanceId) =>
        new(Dungeon, -16f, -383f, 61f, 0f, Start - 10, (byte)CorpseType.ResurrectablePve, instanceId);

    private static InstanceFixture Create()
    {
        var f = new InstanceFixture(new InstanceOptions { UnloadDelayMs = 1000 });
        DeathHooks.Register(f.World, new DeathHooks(new DeathOptions(), new FixedDeathClock(Start)));
        return f;
    }

    /// <summary>a ran the dungeon alone, walked out, and its instance map unloaded; the save lives on through a's bind.</summary>
    private static (Player A, uint Instance) UnloadedInstance(InstanceFixture f)
    {
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        uint id = a.Map!.InstanceId;
        Assert.True(f.LeaveToContinent(a));
        f.Tick(600);
        f.Tick(600);
        Assert.Null(f.World.FindMap(Dungeon, id));
        Assert.True(f.Manager.IsSaveLive(Dungeon, id));
        return (a, id);
    }

    [Fact]
    public void ABodyInAnUnloadedInstanceWithALiveSave_GoesIntoAMapTheInstanceManagerManages()
    {
        using InstanceFixture f = Create();
        (Player a, uint id) = UnloadedInstance(f);

        f.World.GetMap(0).Combat.RestoreGhost(a, Body(id));

        Map map = Assert.IsType<Map>(f.World.FindMap(Dungeon, id));
        Assert.Same(map, a.Combat.Corpse!.Map);
        Assert.NotNull(f.Manager.StateOf(map)); // managed: unloads on its timer, and the timed reset of the save sees it
    }

    [Fact]
    public void ABodyInAnUnloadedInstanceWithALiveSave_DoesNotStopThatSaveFromResettingOnItsTimer()
    {
        // The reviewer's case: a wipe, the ghost logs out at the graveyard, the dungeon unloads, the ghost logs back in.
        using InstanceFixture f = Create();
        (Player a, uint id) = UnloadedInstance(f);
        f.World.GetMap(0).Combat.RestoreGhost(a, Body(id));

        f.Now += 3 * 3600; // past the 2 h normal dungeon reset
        f.Manager.UpdateSchedule();
        f.Tick(50);
        f.Tick(50);

        Assert.False(f.Manager.IsSaveLive(Dungeon, id));
        Assert.Null(f.World.FindMap(Dungeon, id));
    }

    [Fact]
    public void ABodyWhoseInstanceIsGone_CreatesNoMap_AndTheGhostKeepsItsBodyAndItsInstance()
    {
        using InstanceFixture f = Create();
        Player a = f.AddPlayer(1);
        const uint gone = 999;
        Assert.Null(f.Manager.FindSave(gone));

        f.World.GetMap(0).Combat.RestoreGhost(a, Body(gone));

        Assert.Null(f.World.FindMap(Dungeon, gone));
        Assert.Null(f.World.FindMap(Dungeon, 0));
        Corpse corpse = Assert.IsType<Corpse>(a.Combat.Corpse);
        Assert.Null(corpse.Map);
        CorpseSnapshot saved = Assert.IsType<CorpseSnapshot>(PlayerLife.Capture(a).Corpse);
        Assert.Equal((Dungeon, gone), (saved.MapId, saved.InstanceId));
    }

    [Fact]
    public void ARowFromBeforeTheCorpseInstance_InsideTheDungeon_PutsTheBodyIntoTheGhostsOwnInstance()
    {
        // Rows written before characters v34 read InstanceId 0: the old code put such a body into the ghost's map when the map id matched.
        using InstanceFixture f = Create();
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        Map instance = a.Map!;

        instance.Combat.RestoreGhost(a, Body(0));

        Assert.Same(instance, a.Combat.Corpse!.Map);
        Assert.Null(f.World.FindMap(Dungeon, 0));
    }

    [Fact]
    public void ARowFromBeforeTheCorpseInstance_OutsideTheDungeon_CreatesNoSharedCopyOfIt()
    {
        using InstanceFixture f = Create();
        Player a = f.AddPlayer(1);

        f.World.GetMap(0).Combat.RestoreGhost(a, Body(0));

        Assert.Null(f.World.FindMap(Dungeon, 0));
        Assert.Equal(Dungeon, a.Combat.Corpse!.MapId);
    }
}
