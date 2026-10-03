using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Kernel.Instances;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The read-only seams other lanes use (vmangos ObjectMgr::GetMapEntranceTrigger /
/// GetGoBackTrigger, ObjectMgr.cpp:7789-7822) and the Loaded / SaveCreated events.
/// RED on main: the members do not exist (compile failure); the assertions pin the behaviour.
/// </summary>
public sealed class InstanceLookupsTests
{
    [Fact]
    public void EntranceAndGoBackTriggers_AreFoundFromTheTriggerTables()
    {
        using var f = new InstanceFixture();

        Assert.Equal(78u, f.Manager.GetMapEntranceTrigger(Dungeon)!.Id);
        Assert.Equal(1000u, f.Manager.GetGoBackTrigger(Dungeon)!.Id);
        Assert.Null(f.Manager.GetMapEntranceTrigger(Raid)); // no trigger leads to the raid in the fixture
        Assert.Null(f.Manager.GetGoBackTrigger(0)); // continents have no go-back trigger
        Assert.Null(f.Manager.GetGoBackTrigger(Raid)); // none placed on the raid map
    }

    [Fact]
    public void IsSaveLive_FollowsTheSaveLifecycle()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        Assert.False(f.Manager.IsSaveLive(Dungeon, 101));

        Assert.True(f.EnterDungeon(a));
        uint id = a.Map!.InstanceId;
        Assert.True(f.Manager.IsSaveLive(Dungeon, id));
        Assert.False(f.Manager.IsSaveLive(Raid, id)); // wrong map for that id

        f.LeaveToContinent(a);
        f.Now += 2 * 60 * 60 + 1;
        f.Manager.UpdateSchedule();
        f.Tick();
        Assert.False(f.Manager.IsSaveLive(Dungeon, id));
    }

    [Fact]
    public void SaveCreatedAndLoaded_AreRaisedOnce()
    {
        using var f = new InstanceFixture(load: false);
        int loaded = 0;
        var created = new List<uint>();
        f.Manager.Loaded += () => loaded++;
        f.Manager.SaveCreated += save => created.Add(save.InstanceId);

        f.Manager.Load(InstanceStoreSnapshot.Empty);
        Assert.Equal(1, loaded);

        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        Assert.Equal([a.Map!.InstanceId], created);

        Player b = f.AddPlayer(2);
        f.Party(a, b);
        Assert.True(f.EnterDungeon(b)); // joins the existing save: no new one
        Assert.Single(created);
    }

    [Fact]
    public void ParentMapChain_IsEmptyWithoutParents()
    {
        using var f = new InstanceFixture();
        Assert.Empty(f.Manager.GetParentMapChain(Dungeon));
        Assert.Empty(f.Manager.GetParentMapChain(9999));
    }
}
