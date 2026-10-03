using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>
/// Game-event spawns: an object listed in <c>game_event_creature</c> / <c>game_event_gameobject</c> is not in the world until an event
/// adds it (vmangos ObjectMgr.cpp:2330-2345, :2498; GameEventMgr.cpp:807-960), and a negative listing removes it while the event runs.
/// Events are driven by a settable clock and <c>Update</c>; the world by <c>RunTick</c>; nothing waits on real time.
/// </summary>
public sealed class GameEventSpawnTests
{
    private const uint Spring = 100;      // creature guid listed under event 1 (positive): exists only while event 1 runs
    private const uint Nights = 101;      // creature guid listed under event 27 (negative): exists except while event 27 runs
    private const uint Plain = 102;       // not in any event
    private const uint SpringObject = 5000;
    private const uint NightsObject = 5001;
    private const uint ChestEntry = 6000;

    private sealed class Clock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool installGate = true, int objectSpawnTime = 60)
        {
            World = TestWorld.CreateRuntime();
            Map = World.GetMap(0);
            Creatures = new CreatureMapSystem(
                Map,
                Content([Template()], [Spawn(Spring, WolfEntry, 10, 0), Spawn(Nights, WolfEntry, 12, 0), Spawn(Plain, WolfEntry, 14, 0)]),
                random: new Random(1));
            Map.AddUpdater(Creatures);
            Objects = new GameObjectMapSystem(
                Map,
                new GameObjectContent(
                    [GoTemplate(ChestEntry, GameObjectType.Goober)],
                    [GoSpawn(SpringObject, ChestEntry, 11, 1, objectSpawnTime), GoSpawn(NightsObject, ChestEntry, 13, 1)],
                    [], [], []));
            Map.AddUpdater(Objects);

            var events = new GameEventContent(
                [
                    new GameEventRecord(1, 1, 1440, 120, 0, 0, "Spring"),
                    new GameEventRecord(27, 1, 1440, 120, 0, 0, "Nights"),
                ],
                [
                    new GameEventTimeRecord(1, "2026-10-03 12:00:00", "2030-12-31 22:59:59"),
                    new GameEventTimeRecord(27, "2026-10-03 20:00:00", "2030-12-31 22:59:59"),
                ],
                [new GameEventSpawnRecord(Spring, 1), new GameEventSpawnRecord(Nights, -27), new GameEventSpawnRecord(9999, 1)],
                [new GameEventSpawnRecord(SpringObject, 1), new GameEventSpawnRecord(NightsObject, -27)],
                [], [], []);
            var options = new GameEventOptions();
            GameEventLoadResult load = GameEventLoader.Load(events, options, Clock.Now, TimeZoneInfo.Utc);
            Service = new GameEventService(load, options, () => Clock.Now, TimeZoneInfo.Utc, NullLogger.Instance);
            Spawns = new GameEventSpawns(Service, Service.Rows, () => World.Maps);
            Service.AddEffects(Spawns);
            if (installGate)
            {
                Creatures.SpawnGate = Spawns;
                Objects.SpawnGate = Spawns;
            }
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem Creatures { get; }

        public GameObjectMapSystem Objects { get; }

        public Clock Clock { get; } = new(new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero));

        public GameEventService Service { get; }

        public GameEventSpawns Spawns { get; }

        public (Player Player, FakeSession Session) Join()
        {
            var session = new FakeSession();
            Player player = TestWorld.CreatePlayer(1, 0, 0, session);
            World.AddPlayer(player);
            World.RunTick(50);
            session.Clear();
            return (player, session);
        }

        public bool HasCreature(uint guid) => Creatures.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, guid)) is not null;

        public GameObject? FindObject(uint guid) => Objects.Find(ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, guid));

        public void SetTime(int hour, int minute = 0)
        {
            Clock.Now = new DateTimeOffset(2026, 10, 3, hour, minute, 0, TimeSpan.Zero);
            Service.Update();
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void AListedSpawn_IsNotInTheWorldAfterGridLoad_UntilItsEventStarts()
    {
        using var rig = new Rig();
        rig.Join();

        Assert.False(rig.HasCreature(Spring));
        Assert.True(rig.HasCreature(Nights));  // negative listing: present while event 27 is not running
        Assert.True(rig.HasCreature(Plain));
        Assert.Null(rig.FindObject(SpringObject));
        Assert.NotNull(rig.FindObject(NightsObject));
    }

    [Fact]
    public void WhenTheEventStarts_ThePositiveSpawnsAppear_AndObserversGetACreateBlock()
    {
        using var rig = new Rig();
        (Player player, FakeSession session) = rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());

        rig.SetTime(12, 30); // event 1 runs 12:00-14:00
        rig.World.RunTick(50);

        Assert.True(rig.HasCreature(Spring));
        Assert.NotNull(rig.FindObject(SpringObject));
        Creature spring = rig.Creatures.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, Spring))!;
        Assert.Contains(spring.Guid, player.VisibleObjects);
        Assert.Contains(DrainBlocks(session), b => b.Guids.Contains(spring.Guid.Value) && b.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2);
    }

    [Fact]
    public void WhenTheEventStops_ThePositiveSpawnsAreDestroyed_ForObservers()
    {
        using var rig = new Rig();
        (Player player, FakeSession session) = rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());
        rig.SetTime(12, 30);
        rig.World.RunTick(50);
        Creature spring = rig.Creatures.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, Spring))!;
        session.Clear();

        rig.SetTime(14, 30); // past the end
        rig.World.RunTick(50);

        Assert.False(rig.HasCreature(Spring));
        Assert.Null(rig.FindObject(SpringObject));
        Assert.DoesNotContain(spring.Guid, player.VisibleObjects);
        Assert.Null(rig.Map.FindObject(spring.Guid));
        // the observer is told with SMSG_DESTROY_OBJECT (the 8-byte guid), as for any object that leaves the world
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgDestroyObject && BitConverter.ToUInt64(p.Payload) == spring.Guid.Value);
        Assert.True(rig.HasCreature(Plain)); // an unlisted spawn is never touched
    }

    [Fact]
    public void ANegativeListing_RemovesTheSpawnWhileTheEventRuns_AndReturnsItAfterwards()
    {
        using var rig = new Rig();
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());
        Assert.True(rig.HasCreature(Nights));

        rig.SetTime(20, 30); // event 27 (nights) runs 20:00-22:00
        Assert.False(rig.HasCreature(Nights));
        Assert.Null(rig.FindObject(NightsObject));
        Assert.True(rig.HasCreature(Plain));

        rig.SetTime(22, 30);
        Assert.True(rig.HasCreature(Nights));
        Assert.NotNull(rig.FindObject(NightsObject));
    }

    [Fact]
    public void AGridLoadedWhileTheEventRuns_HasTheEventSpawns_AndOneLoadedAfterItStoppedDoesNot()
    {
        using var rig = new Rig();
        (Player player, _) = rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());
        rig.SetTime(12, 30);
        rig.World.RunTick(50);
        Creature live = rig.Creatures.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, Spring))!;
        ArcaneCore.Game.Maps.Grid.GridCoord coord = rig.Map.Grids.CellOf(live)!.Value.Grid;

        // the grid unloads while the event runs, and loads again: the event spawn is back
        player.Relocate(5000, 5000, 83.5f, 0, 0);
        Assert.True(rig.Map.Grids.UnloadGrid(coord, force: true));
        Assert.False(rig.HasCreature(Spring));
        player.Relocate(0, 0, 83.5f, 0, 0);
        rig.World.RunTick(50);
        Assert.True(rig.HasCreature(Spring));

        // it unloads again, the event stops while nobody is there, and a later load leaves it out
        player.Relocate(5000, 5000, 83.5f, 0, 0);
        Assert.True(rig.Map.Grids.UnloadGrid(coord, force: true));
        rig.SetTime(15);
        player.Relocate(0, 0, 83.5f, 0, 0);
        rig.World.RunTick(50);
        Assert.False(rig.HasCreature(Spring));
        Assert.True(rig.HasCreature(Plain));
    }

    [Fact]
    public void AGateInstalledAfterTheGridLoaded_RemovesWhatItRefuses()
    {
        using var rig = new Rig(installGate: false);
        rig.Join();
        Assert.True(rig.HasCreature(Spring)); // no gate yet: everything spawned
        Assert.NotNull(rig.FindObject(SpringObject));

        rig.Creatures.SpawnGate = rig.Spawns;
        rig.Objects.SpawnGate = rig.Spawns;

        Assert.False(rig.HasCreature(Spring));
        Assert.Null(rig.FindObject(SpringObject));
        Assert.True(rig.HasCreature(Nights));
    }

    [Fact]
    public void AnEventCreatureThatDiedWhileTheEventRan_ComesBackAliveAtTheNextStart()
    {
        using var rig = new Rig();
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());
        rig.SetTime(12, 30);
        rig.World.RunTick(50);
        Creature spring = rig.Creatures.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, Spring))!;
        rig.Creatures.KillCreature(spring);
        Assert.NotEqual(CreatureDeathState.Alive, spring.DeathState);

        rig.SetTime(14, 30); // stop: the dead creature and its respawn entry go
        rig.World.RunTick(50);
        Assert.False(rig.HasCreature(Spring));
        Assert.Null(rig.Creatures.PendingRespawnAt(Spring));

        rig.Clock.Now = new DateTimeOffset(2026, 10, 4, 12, 30, 0, TimeSpan.Zero); // the next day's window
        rig.Service.Update();
        rig.World.RunTick(50);
        Creature again = rig.Creatures.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, Spring))!;
        Assert.Equal(CreatureDeathState.Alive, again.DeathState);
        Assert.NotSame(spring, again);
    }

    [Fact]
    public void AGameObjectWithANegativeSpawnTime_StillObeysTheGate_AndAppearsAtTheEventStart()
    {
        // spawntimesecs < 0 means "spawned by events and scripts only": the event start brings it in, the gate keeps it out otherwise
        using var rig = new Rig(objectSpawnTime: -60);
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());
        Assert.Null(rig.FindObject(SpringObject));

        rig.SetTime(12, 30);
        GameObject spring = rig.FindObject(SpringObject)!;
        Assert.True(spring.IsSpawned);

        rig.SetTime(14, 30);
        Assert.Null(rig.FindObject(SpringObject));
    }

    [Fact]
    public void StartingAnEvent_TouchesOnlyItsOwnGuids_NotTheWholeSpawnTable()
    {
        // A counting gate: starting event 1 asks about the guids listed under event 1 (and the creature that also exists in grids), never about unlisted ones.
        using var rig = new Rig();
        rig.Join();
        var counting = new CountingGate(rig.Spawns);
        rig.Creatures.SpawnGate = counting;
        counting.Asked.Clear();

        rig.Spawns.SpawnEvent(1);

        Assert.All(counting.Asked, guid => Assert.Equal(Spring, guid));
        Assert.Single(counting.Asked.Distinct());
        Assert.DoesNotContain(Plain, counting.Asked);
        Assert.DoesNotContain(Nights, counting.Asked);
    }

    private sealed class CountingGate(ISpawnGate inner) : ISpawnGate
    {
        public List<uint> Asked { get; } = [];

        public bool AllowsCreature(uint spawnGuid)
        {
            Asked.Add(spawnGuid);
            return inner.AllowsCreature(spawnGuid);
        }

        public bool AllowsGameObject(uint spawnGuid) => inner.AllowsGameObject(spawnGuid);

        public IEnumerable<uint> GatedCreatures => inner.GatedCreatures;

        public IEnumerable<uint> GatedGameObjects => inner.GatedGameObjects;
    }

    [Fact]
    public void ARowWhoseSpawnDoesNotExist_IsIgnored_WithoutAnError()
    {
        using var rig = new Rig();
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());

        rig.SetTime(12, 30); // creature 9999 is listed under event 1 but has no spawn

        Assert.True(rig.HasCreature(Spring));
        Assert.DoesNotContain(9999u, rig.Creatures.Creatures.Select(c => c.Spawn!.Guid));
    }

    [Fact]
    public void ASpawnListedUnderBothSigns_FollowsBothEvents()
    {
        // guid 100 under event 1 (spawn) and event 27 (remove): it exists while 1 runs and 27 does not
        var state = new FakeState();
        var spawns = new GameEventSpawns(
            state,
            new GameEventRows
            {
                Creatures = new Dictionary<int, IReadOnlyList<uint>> { [1] = [100u], [-27] = [100u] },
            },
            () => []);

        Assert.False(spawns.AllowsCreature(100));
        state.Active.Add(1);
        Assert.True(spawns.AllowsCreature(100));
        state.Active.Add(27);
        Assert.False(spawns.AllowsCreature(100));
        state.Active.Remove(1);
        Assert.False(spawns.AllowsCreature(100));
        state.Active.Remove(27);
        Assert.False(spawns.AllowsCreature(100));
        Assert.True(spawns.AllowsCreature(7)); // an unlisted guid is always allowed
        Assert.Equal([100u], spawns.GatedCreatures);
    }

    private sealed class FakeState : IGameEventState
    {
        public HashSet<ushort> Active { get; } = [];

        public bool IsActiveEvent(ushort eventId) => Active.Contains(eventId);

        public bool IsActiveHoliday(uint holidayId) => false;

        public IReadOnlyCollection<ushort> ActiveEvents => Active;
    }
}
