using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The instance script state of a dungeon instance (vmangos InstanceData, Maps/InstanceData.h/.cpp; Map::CreateInstanceData,
/// Maps/Map.cpp:1987-2037; ScriptDev2 ScriptedInstance): created with the instance map of a map that has a script, initialized and then loaded
/// from the save string the instance save keeps; a DONE value saves (SaveToDB); a state saved IN_PROGRESS comes back NOT_STARTED; a reset
/// instance starts without data.
/// </summary>
public sealed class InstanceScriptTests
{
    /// <summary>The ScriptDev2 shape: three encounters by type, saved when one is DONE.</summary>
    private sealed class ThreeEncounters(Map map) : ScriptedInstance(map, 3)
    {
        public List<uint> CreatedCreatures { get; } = [];

        public override void SetData(uint type, uint data)
        {
            if (type < 3)
            {
                Encounters[type] = data;
            }

            SaveIfDone(data);
        }

        public override uint GetData(uint type) => type < 3 ? Encounters[type] : 0;

        public override void OnCreatureCreate(Creature creature) => CreatedCreatures.Add(creature.Template.Entry);
    }

    private static InstanceFixture Fixture(InstanceOptions? options = null)
    {
        var f = new InstanceFixture(options);
        f.Manager.Scripts = new InstanceScriptRegistry().Register(Dungeon, map => new ThreeEncounters(map));
        return f;
    }

    private static ThreeEncounters DataOf(Player player) => Assert.IsType<ThreeEncounters>(InstanceManager.InstanceDataOf(player.Map!));

    [Fact]
    public void TheInstanceMapOfADungeonWithAScript_HasItsData_AndAMapWithoutAScriptHasNone()
    {
        using InstanceFixture f = Fixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(b, f.AddPlayer(3));

        Assert.True(f.EnterDungeon(a));
        Assert.True(f.EnterRaid(b));

        ThreeEncounters data = DataOf(a);
        Assert.Same(a.Map, data.Instance);
        Assert.Equal([0u, 0u, 0u], data.EncounterStates);
        Assert.Null(InstanceManager.InstanceDataOf(b.Map!));
        Assert.Null(InstanceManager.InstanceDataOf(f.World.GetMap(0)));
    }

    [Fact]
    public void OnlyADoneValue_SavesTheState_IntoTheInstanceSave()
    {
        using InstanceFixture f = Fixture();
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        InstanceSave save = f.Manager.FindSave(a.Map!.InstanceId)!;
        ThreeEncounters data = DataOf(a);

        data.SetData(1, EncounterState.InProgress);
        data.SetData(2, EncounterState.Special);
        Assert.Null(save.Data);
        Assert.DoesNotContain(f.Persistence.Calls, c => c.StartsWith("data ", StringComparison.Ordinal));

        data.SetData(0, EncounterState.Done);
        Assert.Equal("3 1 4", save.Data);
        Assert.Equal($"data {save.InstanceId} 3 1 4", Assert.Single(f.Persistence.Calls, c => c.StartsWith("data ", StringComparison.Ordinal)));
        Assert.Equal(EncounterState.InProgress, data.GetData(1));
    }

    [Fact]
    public void AnInstanceMapCreatedAgain_LoadsTheSavedState_WithInProgressStartingOver()
    {
        using InstanceFixture f = Fixture(new InstanceOptions { UnloadDelayMs = 1000 });
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        Map first = a.Map!;
        uint id = first.InstanceId;
        ThreeEncounters before = DataOf(a);
        before.SetData(1, EncounterState.InProgress);
        before.SetData(0, EncounterState.Done); // saves "3 1 0"

        f.LeaveToContinent(a);
        f.Tick(600);
        f.Tick(600);
        Assert.True(first.IsUnloaded);
        Assert.Equal("3 1 0", f.Manager.FindSave(id)!.Data);

        Assert.True(f.EnterDungeon(a));
        Assert.NotSame(first, a.Map);
        Assert.Equal(id, a.Map!.InstanceId);
        ThreeEncounters after = DataOf(a);
        Assert.NotSame(before, after);
        Assert.Equal(EncounterState.Done, after.GetData(0));
        Assert.Equal(EncounterState.NotStarted, after.GetData(1)); // ScriptDev2 Load: IN_PROGRESS -> NOT_STARTED
    }

    [Fact]
    public void AResetInstance_StartsAgainWithoutData()
    {
        using InstanceFixture f = Fixture();
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        uint id = a.Map!.InstanceId;
        DataOf(a).SetData(0, EncounterState.Done);

        f.LeaveToContinent(a);
        f.Manager.HandleResetInstances(a);
        Assert.Null(f.Manager.FindSave(id));
        f.Tick();

        Assert.True(f.EnterDungeon(a));
        Assert.NotEqual(id, a.Map!.InstanceId);
        Assert.Null(f.Manager.FindSave(a.Map.InstanceId)!.Data);
        Assert.Equal([0u, 0u, 0u], DataOf(a).EncounterStates);
    }

    [Fact]
    public void ScriptedInstanceLoad_ReadsTheStatesInOrder_AndStopsAtTheFirstValueThatIsNotANumber()
    {
        using InstanceFixture f = Fixture();
        var data = new ThreeEncounters(f.World.GetMap(Dungeon, 4242));
        data.Initialize();

        data.Load("3 x 3");
        Assert.Equal([3u, 0u, 0u], data.EncounterStates); // istream: a failed extraction stops the rest

        data.Initialize();
        data.Load("2 1");
        Assert.Equal([2u, 0u, 0u], data.EncounterStates); // IN_PROGRESS starts over; a missing value stays 0
    }

    [Fact]
    public void TheDefaultRegistry_HasDeadminesAndTheEightDungeonsEventAiWritesTo()
    {
        // classic-db z2815 ACTION_T_SET_INST_DATA rows: Shadowfang Keep 33, Wailing Caverns 43, Razorfen Kraul 47, Blackfathom Deeps 48,
        // Sunken Temple 109, Blackrock Depths 230, Zul'Gurub 309, Dire Maul 429.
        Assert.Equal([33u, 36u, 43u, 47u, 48u, 109u, 230u, 309u, 429u], InstanceScriptRegistry.Default.MapIds.Order());
    }
}
