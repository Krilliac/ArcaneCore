using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>SCRIPT_COMMAND_TELEPORT_TO (6) in the DB script runner (cmangos ScriptMgr.cpp:1925-1933).</summary>
public sealed class RelayScriptMailTeleportTests
{
    private const uint ScriptId = 5162;

    private sealed class RecordingTeleports : ITeleportSink
    {
        public List<(Unit Unit, uint Map, float X, float Y, float Z, float O)> Calls { get; } = [];

        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        {
            Calls.Add((unit, mapId, x, y, z, orientation));
            return true;
        }

        public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation) => true;
    }

    private static RelayScriptStep TeleportStep(uint delay) => new RelayScriptStep(ScriptId, delay, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
        with { Command = 6, X = 1535f, Y = -2629f, Z = 380f, Orientation = 1.5f };

    private static (WorldRuntime World, CreatureMapSystem System) Build(RecordingTeleports? teleports)
    {
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestStart, TeleportStep(11000))]) };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(
            new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai),
            new CreatureAiServices { Teleports = teleports });
        return (world, system);
    }

    [Fact]
    public void TeleportToCommand_SendsThePlayerTargetToTheStepPointAfterItsDelay()
    {
        var teleports = new RecordingTeleports();
        (WorldRuntime world, CreatureMapSystem system) = Build(teleports);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 10000);
            Assert.Empty(teleports.Calls);
            Run(world, 1500);
            (Unit unit, uint map, float x, float y, float z, float o) = Assert.Single(teleports.Calls);
            Assert.Same(player, unit);
            Assert.Equal((0u, 1535f, -2629f, 380f, 1.5f), (map, x, y, z, o));
        }
    }

    [Fact]
    public void TeleportToCommand_WithoutAPlayerDoesNothing()
    {
        var teleports = new RecordingTeleports();
        (WorldRuntime world, CreatureMapSystem system) = Build(teleports);
        using (world)
        {
            _ = AddPlayer(world, 1, 0, 10); // a player in range spawns the wolf; the script gets no player target
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, null));
            Run(world, 12000);
            Assert.Empty(teleports.Calls);
        }
    }
}
