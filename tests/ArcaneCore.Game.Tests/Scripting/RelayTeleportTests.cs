using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Scripting;

/// <summary>
/// SCRIPT_COMMAND_TELEPORT_TO (6) for non-player sources and the datalong2 options (vmangos ScriptCommands.cpp:273-295,
/// Unit::NearTeleportTo Unit.cpp:9801-9825).
/// </summary>
public sealed class RelayTeleportTests
{
    private const uint ScriptId = 5163;

    private sealed class RecordingTeleports : ITeleportSink
    {
        public List<(Unit Unit, uint Map, float X, float Y, float Z, bool KeepCombat)> Calls { get; } = [];

        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation) => Teleport(unit, mapId, x, y, z, orientation, true);

        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation, bool keepCombat)
        {
            Calls.Add((unit, mapId, x, y, z, keepCombat));
            return true;
        }

        public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation) => true;
    }

    private static RelayScriptStep Step(uint dataLong, uint dataLong2) => new RelayScriptStep(ScriptId, 1000, 0, 6, dataLong, dataLong2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
        with { X = 1535f, Y = -2629f, Z = 380f, Orientation = 1.5f };

    private static (WorldRuntime World, CreatureMapSystem System) Build(ITeleportSink teleports, RelayScriptStep step)
    {
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestStart, step)]) };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(
            new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai),
            new CreatureAiServices { Teleports = teleports });
        return (world, system);
    }

    [Fact]
    public void NpcSource_IsMovedToTheStepPoint_OnItsOwnMap()
    {
        (WorldRuntime world, CreatureMapSystem system) = Build(new NearTeleportSink(), Step(dataLong: 1, dataLong2: 0));
        using (world)
        {
            _ = AddPlayer(world, 1, 0, 10);
            Creature npc = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, npc, null));
            Run(world, 2000);
            Assert.InRange(npc.X, 1485f, 1585f); // a wolf may wander a little in the ticks after the teleport
            Assert.InRange(npc.Y, -2679f, -2579f);
            Assert.Equal(0u, npc.MapId); // datalong (map 1) does not apply to a near teleport
        }
    }

    [Fact]
    public void NpcSource_IsHandedToTheSinkWithItsOwnMapId()
    {
        var teleports = new RecordingTeleports();
        (WorldRuntime world, CreatureMapSystem system) = Build(teleports, Step(dataLong: 1, dataLong2: 0));
        using (world)
        {
            _ = AddPlayer(world, 1, 0, 10);
            Creature npc = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, npc, null));
            Run(world, 2000);
            (Unit unit, uint map, float x, _, _, _) = Assert.Single(teleports.Calls);
            Assert.Same(npc, unit);
            Assert.Equal((npc.MapId, 1535f), (map, x));
        }
    }

    [Fact]
    public void KeepCombatOption_IsHonouredForAPlayerTarget()
    {
        var teleports = new RecordingTeleports();
        (WorldRuntime world, CreatureMapSystem system) = Build(teleports, Step(dataLong: 0, dataLong2: 0x04));
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 2000);
            (Unit unit, _, _, _, _, bool keepCombat) = Assert.Single(teleports.Calls);
            Assert.Same(player, unit);
            Assert.True(keepCombat);
        }
    }

    [Fact]
    public void WithoutOptions_APlayerTeleportLeavesCombat()
    {
        var teleports = new RecordingTeleports();
        (WorldRuntime world, CreatureMapSystem system) = Build(teleports, Step(dataLong: 0, dataLong2: 0));
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 2000);
            Assert.False(Assert.Single(teleports.Calls).KeepCombat);
        }
    }

    [Fact]
    public void UnsupportedOptionBits_StillTeleportWithTheSupportedOnes()
    {
        var teleports = new RecordingTeleports();
        (WorldRuntime world, CreatureMapSystem system) = Build(teleports, Step(dataLong: 0, dataLong2: 0x04 | 0x10));
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 2000);
            Assert.True(Assert.Single(teleports.Calls).KeepCombat);
        }
    }
}
