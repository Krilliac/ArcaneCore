using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// The EventAI parts classic-db z2815 still could not run (docs/integration/wave3-20261007.md, "Data still needed"), with cmangos semantics
/// (mangos-classic src/game/AI/EventAI/CreatureEventAI.cpp): ACTION_T_SET_INST_DATA (34) and SET_INST_DATA64 (35) into the map's instance
/// script (:1046-1077), ACTION_T_CHANGE_MOVEMENT (48, :1164-1206), EVENT_T_DEATH with a condition (CheckEvent :327-339) and EVENT_T_SPAWNED
/// with the zone condition (SpawnedEventConditionsCheck :1902-1927).
/// </summary>
public sealed class InstanceAndMovementActionTests
{
    private const uint CombatAction = 0x400;

    private static CreatureAiEvent Row(uint id, EventAiEventType type, CreatureAiAction a1, CreatureAiAction a2 = default, uint flags = 0, int p1 = 0,
        int p2 = 0, int p3 = 0, int p4 = 0)
        => new()
        {
            Id = id,
            CreatureId = WolfEntry,
            EventType = (byte)type,
            Flags = flags,
            Chance = 100,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            Param4 = p4,
            Action1 = a1,
            Action2 = a2,
        };

    private static CreatureAiAction Act(byte type, int p1 = 0, int p2 = 0, int p3 = 0) => new(type, p1, p2, p3);

    private static CreatureAiAction Cast(int spell, int flags = 0) => new((byte)EventAiActionType.Cast, spell, (int)EventAiTarget.Self, flags);

    /// <summary>The Hazzali wasp rows' action: cast 11023 at itself with cmangos cast flags 7 (CAST_INTERRUPT_PREVIOUS, CAST_TRIGGERED, CAST_FORCE_CAST).</summary>
    private static CreatureAiAction DeathCast() => Cast(11023, 7);

    /// <summary>An instance script that records what EventAI writes into it.</summary>
    private sealed class RecordingInstance(Map map) : InstanceData(map)
    {
        public Dictionary<uint, uint> Data { get; } = [];

        public Dictionary<uint, ulong> Data64 { get; } = [];

        public override void SetData(uint type, uint data) => Data[type] = data;

        public override uint GetData(uint type) => Data.GetValueOrDefault(type);

        public override void SetData64(uint type, ulong data) => Data64[type] = data;
    }

    private sealed class Conditions(Func<uint, Player, bool> answer) : IConditionEvaluator
    {
        public List<(uint Id, Player Player)> Asked { get; } = [];

        public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source)
        {
            Asked.Add((conditionId, player));
            return answer(conditionId, player);
        }
    }

    private sealed record Scene(WorldRuntime World, Map Map, CreatureMapSystem System, Creature Wolf, Player Player, FakeCaster Spells) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Scene Start(IEnumerable<CreatureAiEvent> rows, InstanceData? script = null, Func<Map, InstanceData>? scriptFor = null,
        CreatureAiServices? services = null, byte movementType = 0, float wander = 0,
        IEnumerable<(uint SpawnGuid, CreatureWaypoint Point)>? spawnPath = null,
        IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? entryPaths = null,
        IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? waypointPaths = null)
    {
        var spells = new FakeCaster();
        var content = new CreatureContent(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)],
            [Spawn(1, WolfEntry, 0, 0, movementType: movementType, wander: wander)],
            spawnPath ?? [], [], [], new CreatureAiContent(rows, []), entryWaypoints: (entryPaths ?? []).Concat(waypointPaths ?? []));
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0, 101);
        if ((script ?? scriptFor?.Invoke(map)) is { } instance)
        {
            instance.Initialize();
            map.AddUpdater(instance);
        }

        CreatureAiServices aiServices = services is null
            ? new CreatureAiServices { Spells = spells }
            : new CreatureAiServices { Spells = spells, Conditions = services.Conditions, ZoneAndAreaOf = services.ZoneAndAreaOf };
        var system = new CreatureMapSystem(map, content, random: new Random(1), aiServices: aiServices);
        map.AddUpdater(system);
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 0, 10, session);
        map.AddPlayer(player); // straight into the instance copy (the login resolver does not know it)
        world.RunTick(0);
        return new Scene(world, map, system, Assert.Single(system.Creatures), player, spells);
    }

    private static void Pull(Scene s) => s.Map.Combat.DealDamage(s.Player, s.Wolf, 1, direct: false);

    // --- 34 SET_INST_DATA / 35 SET_INST_DATA64 ---------------------------------------------------------

    [Fact]
    public void SetInstData_WritesTheFieldIntoTheMapsInstanceScript()
    {
        RecordingInstance? script = null;
        using Scene s = Start([Row(1, EventAiEventType.Aggro, Act(34, 4, (int)EncounterState.InProgress), Cast(900), flags: CombatAction)],
            scriptFor: map => script = new RecordingInstance(map));

        Pull(s);

        Assert.Equal(EncounterState.InProgress, script!.GetData(4));
        Assert.Contains(s.Spells.Casts, c => c.Spell == 900); // the first action succeeded, so the combat-action row ran on
    }

    [Fact]
    public void SetInstData_FailsOnAMapWithoutAnInstanceScript()
    {
        // cmangos: "attempt to set instance data without instance script" returns false; with EFLAG_COMBAT_ACTION the rest of the row waits.
        using Scene s = Start([Row(1, EventAiEventType.Aggro, Act(34, 4, (int)EncounterState.InProgress), Cast(900), flags: CombatAction)]);

        Pull(s);

        Assert.DoesNotContain(s.Spells.Casts, c => c.Spell == 900);
        Assert.Empty(s.Wolf.AI is CreatureEventAI ai ? ai.Unsupported : ["not EventAI"]);
    }

    [Fact]
    public void SetInstData64_WritesTheTargetsGuid_AndFailsWithoutATarget()
    {
        RecordingInstance? script = null;
        using Scene s = Start(
            [
                Row(1, EventAiEventType.Aggro, Act(35, 7, (int)EventAiTarget.Invoker), Cast(901), flags: CombatAction),
                Row(2, EventAiEventType.Aggro, Act(35, 8, (int)EventAiTarget.SecondOnThreat), Cast(902), flags: CombatAction),
            ],
            scriptFor: map => script = new RecordingInstance(map));

        Pull(s);

        Assert.Equal(s.Player.Guid.Value, script!.Data64[7]);
        Assert.False(script.Data64.ContainsKey(8)); // nobody second on the threat list
        Assert.Contains(s.Spells.Casts, c => c.Spell == 901);
        Assert.DoesNotContain(s.Spells.Casts, c => c.Spell == 902);
    }

    // --- 48 CHANGE_MOVEMENT ------------------------------------------------------------------------------

    private static readonly (uint, uint, CreatureWaypoint)[] EntryPath =
        [(WolfEntry, 1u, new CreatureWaypoint(1, 15, 0, 83.5f, 100, 0)), (WolfEntry, 1u, new CreatureWaypoint(2, 15, 15, 83.5f, 100, 0))];

    [Fact]
    public void ChangeMovement_Waypoint_ReplacesTheMovementWithTheEntryPath()
    {
        // The Forest Spirit row: out-of-combat timer, then 48 (2, path, 0).
        using Scene s = Start([Row(1, EventAiEventType.TimerOutOfCombat, Act(48, 2, 1, 0), p1: 1000, p2: 1000)], movementType: 1, wander: 5,
            entryPaths: EntryPath);
        Assert.Equal(MovementGeneratorType.Random, s.Wolf.Motion.DefaultType);

        Run(s.World, 1500);
        Assert.Equal(MovementGeneratorType.Waypoint, s.Wolf.Motion.DefaultType);
        Assert.Empty(s.Wolf.Motion.ActiveTypes);

        Run(s.World, 8000);
        Assert.True(Distance2D(s.Wolf, 0, 0) > 5f);
    }

    [Fact]
    public void ChangeMovement_WaypointZero_IsTheCreaturesDefaultPath_TheSpawnsOwnRows()
    {
        // Alzzin's evade row: 48 (2, 0, 0).
        using Scene s = Start([Row(1, EventAiEventType.TimerOutOfCombat, Act(48, 2, 0, 0), p1: 1000, p2: 1000)],
            spawnPath: [(1u, new CreatureWaypoint(1, 0, 20, 83.5f, 100, 0)), (1u, new CreatureWaypoint(2, 20, 20, 83.5f, 100, 0))]);
        Assert.Equal(MovementGeneratorType.Idle, s.Wolf.Motion.DefaultType);

        Run(s.World, 1500);
        Assert.Equal(MovementGeneratorType.Waypoint, s.Wolf.Motion.DefaultType);
    }

    [Fact]
    public void ChangeMovement_Random_WandersAroundWhereItStands_OverTheDefault()
    {
        // The Hakkari Minion row: spawned, then 48 (1, 15, 0).
        using Scene s = Start([Row(1, EventAiEventType.Spawned, Act(48, 1, 15, 0))]);

        Assert.Equal(MovementGeneratorType.Random, s.Wolf.Motion.CurrentType);
        Assert.Equal(MovementGeneratorType.Idle, s.Wolf.Motion.DefaultType);
        bool moved = false;
        for (int i = 0; i < 100; i++)
        {
            Run(s.World, 200);
            moved |= Distance2D(s.Wolf, 0, 0) > 0.5f;
            Assert.True(Distance2D(s.Wolf, 0, 0) <= 15.01f);
        }

        Assert.True(moved);
    }

    [Fact]
    public void ChangeMovement_Idle_IsPushedOverAWander()
    {
        using Scene s = Start([Row(1, EventAiEventType.TimerOutOfCombat, Act(48, 0, 0, 0), p1: 1000, p2: 1000)], movementType: 1, wander: 5);

        Run(s.World, 1500);

        Assert.Equal(MovementGeneratorType.Idle, s.Wolf.Motion.CurrentType);
        Assert.Equal(MovementGeneratorType.Random, s.Wolf.Motion.DefaultType);
    }

    [Theory]
    [InlineData(3, 1, 0)] // PATH_MOTION_TYPE
    [InlineData(4, 1, 0)] // LINEAR_WP_MOTION_TYPE
    public void ChangeMovement_WhatIsNotSupported_FailsAndChangesNothing(int type, int path, int flags)
    {
        using Scene s = Start([Row(1, EventAiEventType.Aggro, Act(48, type, path, flags), Cast(903), flags: CombatAction)], movementType: 1, wander: 5,
            entryPaths: EntryPath);

        Pull(s);

        Assert.Equal(MovementGeneratorType.Random, s.Wolf.Motion.DefaultType);
        Assert.DoesNotContain(s.Spells.Casts, c => c.Spell == 903);
    }

    [Fact]
    public void ChangeMovement_WaypointPathFlag_UsesTheSharedPathId()
    {
        using Scene s = Start([Row(1, EventAiEventType.TimerOutOfCombat, Act(48, 2, 51, 2), p1: 1000, p2: 1000)],
            waypointPaths: [(CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | 51u,
                new CreatureWaypoint(1, 15, 0, 83.5f, 100, 0))]);
        Run(s.World, 1500);
        Assert.Equal(MovementGeneratorType.Waypoint, s.Wolf.Motion.DefaultType);
        Assert.Equal(15f, s.Wolf.Spline!.EndX);
    }

    // --- EVENT_T_DEATH with a condition --------------------------------------------------------------------

    [Fact]
    public void DeathWithACondition_Fires_WhenTheKillersPlayerMeetsIt()
    {
        var conditions = new Conditions((id, _) => id == 100);
        using Scene s = Start([Row(1, EventAiEventType.Death, DeathCast(), p1: 100)], services: new CreatureAiServices { Conditions = conditions });

        s.Map.Combat.Kill(s.Player, s.Wolf);

        Assert.Contains(s.Spells.Casts, c => c.Spell == 11023);
        Assert.Equal([(100u, s.Player)], conditions.Asked);
        Assert.Empty(((CreatureEventAI)s.Wolf.AI!).Unsupported);
    }

    [Fact]
    public void DeathWithACondition_DoesNotFire_WhenTheConditionFails_OrThereIsNoKiller()
    {
        var no = new Conditions((_, _) => false);
        using (Scene s = Start([Row(1, EventAiEventType.Death, DeathCast(), p1: 100)], services: new CreatureAiServices { Conditions = no }))
        {
            s.Map.Combat.Kill(s.Player, s.Wolf);
            Assert.DoesNotContain(s.Spells.Casts, c => c.Spell == 11023);
        }

        var yes = new Conditions((_, _) => true);
        using (Scene s = Start([Row(1, EventAiEventType.Death, DeathCast(), p1: 100)], services: new CreatureAiServices { Conditions = yes }))
        {
            s.System.KillCreature(s.Wolf); // no killer: cmangos "if (!actionInvoker) return false"
            Assert.DoesNotContain(s.Spells.Casts, c => c.Spell == 11023);
            Assert.Empty(yes.Asked);
        }
    }

    // --- EVENT_T_SPAWNED with the zone condition --------------------------------------------------------------

    [Theory]
    [InlineData(493u, 2000u, true)]  // the zone (Moonglade)
    [InlineData(1u, 493u, true)]     // or the area
    [InlineData(1u, 2u, false)]
    [InlineData(0u, 0u, false)]      // the terrain does not know the place
    public void SpawnedWithTheZoneCondition_MatchesTheZoneOrTheArea(uint zone, uint area, bool fires)
    {
        // The reveler rows: spawned, condition 2, value 493, then SET_FACTION 35.
        using Scene s = Start([Row(1, EventAiEventType.Spawned, Act(2, 35), p1: 2, p2: 493)],
            services: new CreatureAiServices { ZoneAndAreaOf = _ => (zone, area) });

        Assert.Equal(fires ? 35u : s.Wolf.Template.Faction, s.Wolf.FactionTemplate);
        Assert.Empty(((CreatureEventAI)s.Wolf.AI!).Unsupported);
    }
}
