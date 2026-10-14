using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Regression tests for <c>SCRIPT_COMMAND_MOVEMENT</c> (20) with <c>LINEAR_WP_MOTION_TYPE</c> (<c>datalong = 3</c>, used by
/// <c>dbscripts_on_quest_end</c> 412 and 1951) and the <see cref="Creature.NpcFlags"/> / <see cref="Unit.UnitFlags"/> restoration
/// safeguards when a scripted waypoint path is missing, blocked by combat, unsupported, or lacks a waypoint restore script.
/// </summary>
public sealed class CreatureRelayScriptTests
{
    private const uint RazzleEntry = 1269;
    private const uint OzzieEntry = 1268;
    private const uint FarEntry = 1270;
    private const uint Quest412 = 412;
    private const uint NpcFlagQuestGiver = 2;

    private static RelayScriptStep Step(
        uint id,
        uint priority,
        uint command,
        uint dataLong = 0,
        uint dataLong2 = 0,
        uint buddyEntry = 0,
        uint searchRadius = 0)
        => new(id, 0, priority, command, dataLong, dataLong2, 0, buddyEntry, searchRadius, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void RelayMovement_LinearWaypoint_StartsLinearWaypointGenerator()
    {
        RelayScriptStep moveStep = Step(Quest412, 1, command: 20, dataLong: 3, dataLong2: 1);
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog([(DbScriptKind.QuestEnd, moveStep)]),
        };
        (uint Entry, uint PathId, CreatureWaypoint Point)[] entryPaths =
        [
            (RazzleEntry, 1u, new CreatureWaypoint(1, 10, 0, 83.5f, 100, 0)),
            (RazzleEntry, 1u, new CreatureWaypoint(2, 20, 0, 83.5f, 100, 0)),
        ];
        var content = new CreatureContent(
            [Template(RazzleEntry) with { NpcFlags = NpcFlagQuestGiver, Civilian = true }],
            [Spawn(1, RazzleEntry, 0, 0)],
            [],
            [],
            [],
            ai,
            entryWaypoints: entryPaths);

        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 5);
            Creature razzle = Assert.Single(system.Creatures);

            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, Quest412, razzle, player));

            WaypointMovementGenerator generator = Assert.IsType<WaypointMovementGenerator>(razzle.Motion.Default);
            Assert.True(generator.IsLinear);
            Assert.Equal(MovementGeneratorType.Waypoint, razzle.Motion.DefaultType);
        }
    }

    [Fact]
    public void QuestEndScript_WhenLinearWaypointPathIsMissing_RestoresStrippedQuestGiverFlagsOnMoverAndNearbyBuddy()
    {
        // Mirrors dbscripts_on_quest_end 412 (Operation Recombobulation): removes NPC_FLAG_QUESTGIVER (2) from Razzle (1269) and
        // Ozzie (1268 within 30 yd), then starts LINEAR_WP_MOTION_TYPE (datalong=3) on path 1. When path 1 is missing from
        // creature_movement_template, both quest givers must have their template NpcFlags and UnitFlags restored immediately.
        const UnitFlags OocNotAttackable = (UnitFlags)0x00000100u;
        RelayScriptStep[] steps =
        [
            Step(Quest412, 1, command: 21, dataLong: 1),
            Step(Quest412, 2, command: 29, dataLong: NpcFlagQuestGiver, dataLong2: 0),
            Step(Quest412, 3, command: 29, dataLong: NpcFlagQuestGiver, dataLong2: 0, buddyEntry: OzzieEntry, searchRadius: 30),
            Step(Quest412, 4, command: 48, dataLong: (uint)OocNotAttackable, dataLong2: 0),
            Step(Quest412, 6, command: 20, dataLong: 3, dataLong2: 1),
        ];
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(steps.Select(s => (DbScriptKind.QuestEnd, s))),
        };
        var content = new CreatureContent(
            [
                Template(RazzleEntry) with { NpcFlags = NpcFlagQuestGiver, UnitFlags = (uint)OocNotAttackable, Civilian = true },
                Template(OzzieEntry) with { NpcFlags = NpcFlagQuestGiver, Civilian = true },
                Template(FarEntry) with { NpcFlags = NpcFlagQuestGiver, Civilian = true },
            ],
            [
                Spawn(1, RazzleEntry, 0, 0),
                Spawn(2, OzzieEntry, 25, 0),
                Spawn(3, FarEntry, 45, 0),
            ],
            [],
            [],
            [],
            ai);

        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 4);
            Creature razzle = system.Creatures.Single(c => c.Template.Entry == RazzleEntry);
            Creature ozzie = system.Creatures.Single(c => c.Template.Entry == OzzieEntry);
            Creature far = system.Creatures.Single(c => c.Template.Entry == FarEntry);
            far.NpcFlags = 0;

            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, Quest412, razzle, player));

            Assert.Equal(NpcFlagQuestGiver, razzle.NpcFlags);
            Assert.Equal(OocNotAttackable, razzle.UnitFlags & OocNotAttackable);
            Assert.Equal(NpcFlagQuestGiver, ozzie.NpcFlags);
            Assert.Equal(0u, far.NpcFlags);
        }
    }

    [Fact]
    public void RelayMovement_WhenMoverInCombatOrUnsupportedType_RestoresStrippedNpcFlags()
    {
        const uint UnsupportedScriptId = 413;
        RelayScriptStep[] steps =
        [
            Step(Quest412, 1, command: 29, dataLong: NpcFlagQuestGiver, dataLong2: 0),
            Step(Quest412, 2, command: 20, dataLong: 3, dataLong2: 1),
            Step(UnsupportedScriptId, 1, command: 29, dataLong: NpcFlagQuestGiver, dataLong2: 0),
            Step(UnsupportedScriptId, 2, command: 20, dataLong: 99, dataLong2: 1),
        ];
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(steps.Select(s => (DbScriptKind.QuestEnd, s))),
        };
        (uint Entry, uint PathId, CreatureWaypoint Point)[] entryPaths =
        [
            (RazzleEntry, 1u, new CreatureWaypoint(1, 10, 0, 83.5f, 100, 999)),
        ];
        var content = new CreatureContent(
            [Template(RazzleEntry) with { NpcFlags = NpcFlagQuestGiver, Civilian = false }],
            [Spawn(1, RazzleEntry, 0, 0)],
            [],
            [],
            [],
            ai,
            entryWaypoints: entryPaths);

        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 4);
            Creature razzle = Assert.Single(system.Creatures);

            // 1. In-combat mover skips MOVEMENT but still restores stripped NpcFlags immediately.
            razzle.UnitFlags |= UnitFlags.InCombat;
            Assert.True(razzle.Combat.IsInCombat);
            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, Quest412, razzle, player));
            Assert.Equal(NpcFlagQuestGiver, razzle.NpcFlags);

            // 2. Out-of-combat mover with unsupported movement type also restores stripped NpcFlags.
            razzle.UnitFlags &= ~UnitFlags.InCombat;
            Assert.False(razzle.Combat.IsInCombat);
            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, UnsupportedScriptId, razzle, player));
            Assert.Equal(NpcFlagQuestGiver, razzle.NpcFlags);
        }
    }

    [Fact]
    public void RelayScript_WhenQuestGiverFlagsStrippedWithoutWaypointRestore_SelfHealsAfterSafeguardWindow()
    {
        // A script that strips NpcFlags.QuestGiver without starting a scripted waypoint path (or whose waypoint path has no
        // restore script) must automatically self-diagnose and restore NpcFlags after StrippedFlagSafeguardMs (60s).
        RelayScriptStep[] steps =
        [
            Step(Quest412, 1, command: 29, dataLong: NpcFlagQuestGiver, dataLong2: 0),
        ];
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(steps.Select(s => (DbScriptKind.QuestEnd, s))),
        };
        var content = new CreatureContent(
            [Template(RazzleEntry) with { NpcFlags = NpcFlagQuestGiver, Civilian = true }],
            [Spawn(1, RazzleEntry, 0, 0)],
            [],
            [],
            [],
            ai);

        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 4);
            Creature razzle = Assert.Single(system.Creatures);

            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, Quest412, razzle, player));
            Assert.Equal(0u, razzle.NpcFlags);

            // Halfway through the safeguard window (30s): still stripped.
            system.Update(map, CreatureMapSystem.StrippedFlagSafeguardMs / 2);
            Assert.Equal(0u, razzle.NpcFlags);

            // Reaching the 60s safeguard window: self-diagnosed and restored.
            system.Update(map, CreatureMapSystem.StrippedFlagSafeguardMs / 2);
            Assert.Equal(NpcFlagQuestGiver, razzle.NpcFlags);
        }
    }
}
