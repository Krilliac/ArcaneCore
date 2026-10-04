using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>
/// Game-event creature data (<c>game_event_creature_data</c>): while an event runs, listed creatures take its <c>entry_id</c> and <c>modelid</c>
/// (vmangos Creature::UpdateEntry with event data, GameEventMgr.cpp:961-1021); equipment and spells are counted, not applied.
/// </summary>
public sealed class GameEventCreatureDataTests
{
    private const uint Day = 299;       // the creature as spawned
    private const uint Night = 1299;    // what the event swaps in
    private const uint Forced = 777;    // a model the event forces
    private const uint Swapped = 100;   // spawn guid whose entry the event swaps
    private const uint Reskinned = 101; // spawn guid whose model the event forces
    private const uint Casting = 102;   // spawn guid with only spells and equipment (not applied)
    private const uint Plain = 103;

    private sealed class Clock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(IEnumerable<GameEventCreatureDataRecord>? data = null)
        {
            World = TestWorld.CreateRuntime();
            Map = World.GetMap(0);
            Creatures = new CreatureMapSystem(
                Map,
                Content(
                    [Template(Day, b => { b.Name = "Day Wolf"; b.DisplayIds = [903]; b.MinLevel = 2; b.MaxLevel = 2; }), Template(Night, b => { b.Name = "Night Wolf"; b.DisplayIds = [905]; b.MinLevel = 9; b.MaxLevel = 9; b.MinLevelHealth = 400; b.MaxLevelHealth = 400; })],
                    [Spawn(Swapped, Day, 10, 0), Spawn(Reskinned, Day, 12, 0), Spawn(Casting, Day, 14, 0), Spawn(Plain, Day, 16, 0)]),
                random: new Random(1));
            Map.AddUpdater(Creatures);
            var events = new GameEventContent(
                [new GameEventRecord(27, 1, 1440, 120, 0, 0, "Nights"), new GameEventRecord(28, 1, 1440, 120, 0, 0, "Second")],
                [new GameEventTimeRecord(27, "2026-10-03 12:00:00", "2030-12-31 22:59:59"), new GameEventTimeRecord(28, "2026-10-03 13:00:00", "2030-12-31 22:59:59")],
                [], [],
                [
                    .. data ??
                    [
                        new GameEventCreatureDataRecord(Swapped, 27, Night, 0, 0, 0, 0),
                        new GameEventCreatureDataRecord(Reskinned, 27, 0, Forced, 0, 0, 0),
                        new GameEventCreatureDataRecord(Casting, 27, 0, 0, 5, 7671, 7671),
                        new GameEventCreatureDataRecord(Swapped, 28, Day, 0, 0, 0, 0),
                    ],
                ],
                [], []);
            var options = new GameEventOptions();
            GameEventLoadResult load = GameEventLoader.Load(events, options, Clock.Now, TimeZoneInfo.Utc);
            Service = new GameEventService(load, options, () => Clock.Now, TimeZoneInfo.Utc, NullLogger.Instance);
            Data = new GameEventCreatureData(Service, Service.Rows, () => World.Maps);
            Service.AddEffects(Data);
            Creatures.EventData = Data;
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem Creatures { get; }

        public Clock Clock { get; } = new(new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero));

        public GameEventService Service { get; }

        public GameEventCreatureData Data { get; }

        public Player Join()
        {
            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            World.AddPlayer(player);
            World.RunTick(50);
            return player;
        }

        public Creature Spawned(uint guid) => Creatures.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, Day, guid))!;

        public void At(int hour, int minute = 0)
        {
            Clock.Now = new DateTimeOffset(2026, 10, 3, hour, minute, 0, TimeSpan.Zero);
            Service.Update();
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void WhileTheEventRuns_AListedCreatureTakesItsEntryAndModel_AndReturnsToItsOwnAfterwards()
    {
        using var rig = new Rig();
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());
        Creature swapped = rig.Spawned(Swapped);
        Creature reskinned = rig.Spawned(Reskinned);
        Assert.Equal((Day, 903u, 2), (swapped.Entry, swapped.DisplayId, swapped.Level));

        rig.At(12, 30); // event 27 runs 12:00-14:00

        Assert.Equal(Night, swapped.Entry);
        Assert.Equal("Night Wolf", swapped.Template.Name);
        Assert.Equal(905u, swapped.DisplayId);
        Assert.Equal(9, swapped.Level);
        Assert.Equal(400u, swapped.MaxHealth);
        Assert.Equal(Day, reskinned.Entry);          // modelid only: the entry stays
        Assert.Equal(Forced, reskinned.DisplayId);
        Assert.Equal(903u, rig.Spawned(Plain).DisplayId);

        rig.At(14, 30);

        Assert.Equal((Day, 903u, 2), (swapped.Entry, swapped.DisplayId, swapped.Level));
        Assert.Equal("Day Wolf", swapped.Template.Name);
        Assert.Equal(903u, reskinned.DisplayId);
    }

    [Fact]
    public void ACreatureThatRespawnsMidEvent_KeepsTheEventsEntry()
    {
        using var rig = new Rig();
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());
        rig.At(12, 30);
        Creature swapped = rig.Spawned(Swapped);
        rig.Creatures.KillCreature(swapped);
        Assert.NotEqual(CreatureDeathState.Alive, swapped.DeathState);

        rig.Creatures.ForceRespawn(swapped);

        Assert.Equal(CreatureDeathState.Alive, swapped.DeathState);
        Assert.Equal(Night, swapped.Entry);
        Assert.Equal(9, swapped.Level);
        Assert.Equal(400u, swapped.Health);
    }

    [Fact]
    public void AGridLoadedMidEvent_CreatesTheCreatureWithTheEventData_AndOneAfterItStoppedWithout()
    {
        using var rig = new Rig();
        Player player = rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());
        rig.At(12, 30);
        ArcaneCore.Game.Maps.Grid.GridCoord coord = rig.Map.Grids.CellOf(rig.Spawned(Swapped))!.Value.Grid;

        player.Relocate(5000, 5000, 83.5f, 0, 0);
        Assert.True(rig.Map.Grids.UnloadGrid(coord, force: true));
        player.Relocate(0, 0, 83.5f, 0, 0);
        rig.World.RunTick(50);
        Assert.Equal(Night, rig.Spawned(Swapped).Entry);   // created while the event runs

        player.Relocate(5000, 5000, 83.5f, 0, 0);
        Assert.True(rig.Map.Grids.UnloadGrid(coord, force: true));
        rig.At(15);
        player.Relocate(0, 0, 83.5f, 0, 0);
        rig.World.RunTick(50);
        Assert.Equal(Day, rig.Spawned(Swapped).Entry);     // created after it stopped
    }

    [Fact]
    public void AnEntryThatHasNoTemplate_IsIgnored_AndTheCreatureKeepsItsOwn()
    {
        using var rig = new Rig([new GameEventCreatureDataRecord(Swapped, 27, 424242, 0, 0, 0, 0)]);
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());

        rig.At(12, 30);

        Creature swapped = rig.Spawned(Swapped);
        Assert.Equal(Day, swapped.Entry);
        Assert.Null(swapped.EventTemplate);
    }

    [Fact]
    public void ACreatureListedUnderTwoEvents_TakesTheDataOfTheFirstRunningOne()
    {
        using var rig = new Rig();
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());

        rig.At(13, 30); // both run: event 27 comes first, and swaps in the night wolf (event 28 would swap back to the day wolf)
        Assert.Equal(Night, rig.Spawned(Swapped).Entry);

        rig.At(14, 30); // event 27 is over, 28 (13:00-15:00) still runs: its data applies
        Assert.Equal(Day, rig.Spawned(Swapped).Entry);
        Assert.NotNull(rig.Spawned(Swapped).EventTemplate);
    }

    [Fact]
    public void EquipmentAndSpellRows_AreCounted_NotApplied()
    {
        using var rig = new Rig();
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());

        rig.At(12, 30);

        Assert.Equal(1, rig.Data.EquipmentRows);
        Assert.Equal(1, rig.Data.SpellRows);
        Creature casting = rig.Spawned(Casting);
        Assert.Equal(Day, casting.Entry);        // the row changes nothing the engine can apply
        Assert.Equal(903u, casting.DisplayId);
    }

    [Fact]
    public void WithoutAProvider_NoCreatureIsTouched()
    {
        using var rig = new Rig();
        rig.Creatures.EventData = null;
        rig.Join();
        rig.Service.Initialize(new HashSet<ushort>());

        rig.At(12, 30);

        Assert.Equal(Day, rig.Spawned(Swapped).Entry);
    }
}
