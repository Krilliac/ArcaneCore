using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>
/// Server-side zone and area tracking (vmangos Player.cpp:1215-1236 zone timer, :6560-6675
/// UpdateArea/UpdateZone). The terrain is replaced by a scripted locator.
/// </summary>
public sealed class ZoneAreaTrackingTests
{
    private sealed class ScriptedLocator : IZoneLocator
    {
        public (uint Zone, uint Area) Position = (12, 9);
        public Dictionary<uint, AreaTemplate> Entries { get; } = new()
        {
            [12] = new AreaTemplate(12, 0, 0, 41, (uint)AreaFlags.Capital, 1, "Elwynn Forest", AreaTeams.Ally, 0),
            [40] = new AreaTemplate(40, 0, 0, 43, 0, 10, "Westfall", 0, 0),
        };

        public bool CanDeriveZones { get; set; } = true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => Position;

        public AreaTemplate? Find(uint areaId) => Entries.GetValueOrDefault(areaId);
    }

    private sealed class Recorder : IPlayerLocationListener
    {
        public List<string> Events { get; } = [];

        public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
            => Events.Add($"zone {oldZone}->{newZone} area {newArea} team {zoneEntry?.Team}");

        public void OnAreaChanged(Player player, uint oldArea, uint newArea) => Events.Add($"area {oldArea}->{newArea}");
    }

    private static (WorldRuntime World, ScriptedLocator Locator, Recorder Recorder, Player Player) Setup()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        var locator = new ScriptedLocator();
        var recorder = new Recorder();
        WorldStateHooks hooks = WorldStateHooks.For(world);
        hooks.Locator = locator;
        hooks.AddLocationListener(recorder);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);
        return (world, locator, recorder, player);
    }

    [Fact]
    public void EveryMap_GetsTheZoneAreaUpdater()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Assert.Contains(typeof(ZoneAreaUpdater), DefaultMapUpdaters.Types);
        Assert.NotNull(world.GetMap(0).FindUpdater<ZoneAreaUpdater>());
        Assert.NotSame(world.GetMap(0).FindUpdater<ZoneAreaUpdater>(), world.GetMap(1).FindUpdater<ZoneAreaUpdater>());
    }

    [Fact]
    public void FirstTick_FiresTheZoneEntryOnce_FromZeroToTheWalkedZone()
    {
        (WorldRuntime world, _, Recorder recorder, Player player) = Setup();
        player.ZoneId = 999; // the stored / teleport value is not the tracker's cached zone

        world.RunTick(50);
        world.RunTick(50);

        Assert.Equal(["zone 0->12 area 9 team 2", "area 0->9"], recorder.Events);
        Assert.Equal(12u, player.ZoneId);
        Assert.Equal(12u, player.Map!.FindUpdater<ZoneAreaUpdater>()!.GetZone(player));
    }

    [Fact]
    public void ZoneChange_IsSeenWhenTheOneSecondTimerElapses_NotBefore()
    {
        (WorldRuntime world, ScriptedLocator locator, Recorder recorder, Player player) = Setup();
        world.RunTick(50); // initial UpdateZone, timer = 1000
        recorder.Events.Clear();

        locator.Position = (40, 87);
        world.RunTick(950); // 950 elapsed: timer 50 left
        world.RunTick(49);  // 999 elapsed
        Assert.Empty(recorder.Events);
        Assert.Equal(12u, player.ZoneId);

        world.RunTick(1);   // 1000 elapsed
        Assert.Equal(["zone 12->40 area 87 team 0", "area 9->87"], recorder.Events);
        Assert.Equal(40u, player.ZoneId);
    }

    [Fact]
    public void AreaChangeInsideAZone_RaisesOnlyTheAreaEvent()
    {
        (WorldRuntime world, ScriptedLocator locator, Recorder recorder, _) = Setup();
        world.RunTick(50);
        recorder.Events.Clear();

        locator.Position = (12, 87);
        world.RunTick(1000);

        Assert.Equal(["area 9->87"], recorder.Events);
    }

    [Fact]
    public void UnknownZone_LeavesThePreviousZone_AndRetriesEveryTick()
    {
        (WorldRuntime world, ScriptedLocator locator, Recorder recorder, Player player) = Setup();
        world.RunTick(50);
        recorder.Events.Clear();

        locator.Position = (0, 0); // off the map: vmangos UpdateZone returns early
        world.RunTick(950); // timer 50 left
        world.RunTick(50);  // elapsed: the failed update leaves the timer at 50 (vmangos does not reset it)
        world.RunTick(50);
        Assert.Empty(recorder.Events);
        Assert.Equal(12u, player.ZoneId);

        locator.Position = (40, 87); // timer was not reset by the failed update: next tick picks it up
        world.RunTick(50);
        Assert.Equal(40u, player.ZoneId);
    }

    [Fact]
    public void ForceUpdate_RunsImmediately_AndIsNotRepeatedByThePeriodicCheck()
    {
        (WorldRuntime world, _, Recorder recorder, Player player) = Setup();

        Assert.True(player.Map!.FindUpdater<ZoneAreaUpdater>()!.ForceUpdate(player));
        Assert.Equal(["zone 0->12 area 9 team 2", "area 0->9"], recorder.Events);

        world.RunTick(50);
        Assert.Equal(2, recorder.Events.Count);
    }

    [Fact]
    public void LeavingAndReenteringAMap_ReraisesTheZoneEntry()
    {
        (WorldRuntime world, _, Recorder recorder, Player player) = Setup();
        world.RunTick(50);
        recorder.Events.Clear();

        world.GetMap(0).RemovePlayer(player);
        world.GetMap(0).AddPlayer(player);
        world.RunTick(50);

        Assert.Equal("zone 0->12 area 9 team 2", recorder.Events[0]);
    }

    [Fact]
    public void TwoPlayers_DoNotShareState()
    {
        (WorldRuntime world, ScriptedLocator locator, Recorder recorder, Player first) = Setup();
        Player second = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());
        world.AddPlayer(second);
        world.RunTick(50);

        ZoneAreaUpdater updater = first.Map!.FindUpdater<ZoneAreaUpdater>()!;
        Assert.Equal(12u, updater.GetZone(first));
        Assert.Equal(12u, updater.GetZone(second));
        Assert.Equal(2, recorder.Events.Count(e => e.StartsWith("zone 0->12", StringComparison.Ordinal)));
        _ = locator;
    }

    [Fact]
    public void ClientZoneMode_UsesThePlayersZone_WhileNoAreaDataIsLoaded()
    {
        (WorldRuntime world, ScriptedLocator locator, Recorder recorder, Player player) = Setup();
        locator.CanDeriveZones = false;
        locator.Entries.Clear();
        WorldStateHooks hooks = WorldStateHooks.For(world);
        Assert.True(hooks.UsesClientZone);

        world.RunTick(50);
        Assert.Equal("zone 0->12 area 0 team ", recorder.Events[0]); // the creating character has ZoneId 12

        player.ZoneId = 40; // what CMSG_ZONEUPDATE does in this mode
        recorder.Events.Clear();
        world.RunTick(1000);
        Assert.Equal("zone 12->40 area 0 team ", recorder.Events[0]);

        hooks.Zones.ClientZoneTrust = ClientZoneTrust.Never;
        Assert.False(hooks.UsesClientZone);
    }

    [Fact]
    public void ListenersRunInOrder_AndAFailingOneDoesNotStopTheOthers()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        var calls = new List<string>();
        WorldStateHooks hooks = WorldStateHooks.For(world);
        hooks.Locator = new ScriptedLocator();
        hooks.AddLocationListener(new Ordered("late", 5, calls, fail: false));
        hooks.AddLocationListener(new Ordered("early", -5, calls, fail: true));
        hooks.AddLocationListener(new Ordered("late2", 5, calls, fail: false));
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);

        Assert.Throws<InvalidOperationException>(() => player.Map!.FindUpdater<ZoneAreaUpdater>()!.ForceUpdate(player));

        Assert.Equal(["early", "late", "late2", "early:area", "late:area", "late2:area"], calls);
    }

    private sealed class Ordered(string name, int order, List<string> calls, bool fail) : IPlayerLocationListener
    {
        public int Order => order;

        public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
        {
            calls.Add(name);
            if (fail)
            {
                throw new InvalidOperationException(name);
            }
        }

        public void OnAreaChanged(Player player, uint oldArea, uint newArea) => calls.Add(name + ":area");
    }
}
