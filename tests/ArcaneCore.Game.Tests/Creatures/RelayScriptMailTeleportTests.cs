using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>SCRIPT_COMMAND_TELEPORT_TO (6) and SEND_MAIL (38) in the DB script runner (cmangos ScriptMgr.cpp:1925-1933).</summary>
public sealed class RelayScriptMailTeleportTests
{
    private const uint ScriptId = 5162;

    private sealed class RecordingTeleports : ITeleportSink
    {
        public List<(Unit Unit, uint Map, float X, float Y, float Z, float O)> Calls { get; } = [];
        public List<bool> KeepCombat { get; } = [];

        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation) => Teleport(unit, mapId, x, y, z, orientation, true);

        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation, bool keepCombat)
        {
            Calls.Add((unit, mapId, x, y, z, orientation));
            KeepCombat.Add(keepCombat);
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
    public void TeleportToCommand_AsksForALeaveCombatTeleport()
    {
        var teleports = new RecordingTeleports();
        (WorldRuntime world, CreatureMapSystem system) = Build(teleports);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 12000);
            Assert.Equal([false], teleports.KeepCombat);
        }
    }

    /// <summary>The production sink's mapping for players (WorldSpellTeleportSink): keepCombat on the same map is NotLeaveCombat.</summary>
    private sealed class ServiceTeleports(TeleportService service) : ITeleportSink
    {
        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation) => Teleport(unit, mapId, x, y, z, orientation, true);

        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation, bool keepCombat)
            => service.TeleportTo((Player)unit, mapId, x, y, z, orientation, keepCombat && unit.MapId == mapId ? TeleportOptions.NotLeaveCombat : TeleportOptions.None);

        public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation) => true;
    }

    [Fact]
    public void TeleportToCommand_SameMap_TakesAPlayerInCombatOutOfCombat()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        WorldMaps.Of(world).Load(new MapContent([new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")], [], [], [], []));
        var service = new TeleportService(world, _ => { }, _ => { });
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestStart, TeleportStep(1000))]) };
        (_, _, CreatureMapSystem system) = CreateAiSystem(
            new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai),
            new CreatureAiServices { Teleports = new ServiceTeleports(service) },
            world: world);
        using (world)
        {
            (Player player, FakeSession session) = AddPlayer(world, 1, 0, 10);
            player.UnitFlags |= UnitFlags.InCombat;
            Assert.True(player.Combat.IsInCombat);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 2000);
            // CombatStop (TeleportService near branch) sends SMSG_CANCEL_COMBAT and clears the in-combat state.
            Assert.Contains(session.Sent, p => p.Opcode == ArcaneCore.Protocol.WorldOpcode.SmsgCancelCombat);
            Assert.False(player.Combat.IsInCombat);
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

    private sealed class RecordingMail : IScriptMailSink
    {
        public List<(Player Receiver, uint Sender, uint Template, uint Money, uint Delay)> Calls { get; } = [];

        public void Send(Player receiver, uint senderEntry, uint templateId, uint money, uint delaySeconds)
            => Calls.Add((receiver, senderEntry, templateId, money, delaySeconds));
    }

    // Quest 8728 style: mail template 40, 25 copper, datalong2 the alternative sender, dataint the delay in seconds.
    private static RelayScriptStep MailStep(uint sender, int delaySeconds) => new RelayScriptStep(ScriptId, 1000, 0, 38, 40, sender, 25, 0, 0, 0, delaySeconds, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static (WorldRuntime World, CreatureMapSystem System) BuildMail(RecordingMail? mail, RelayScriptStep step)
    {
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestStart, step)]) };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(
            new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai),
            new CreatureAiServices { ScriptMail = mail });
        return (world, system);
    }

    [Fact]
    public void SendMailCommand_MailsThePlayerFromTheAlternativeSenderWithTemplateMoneyAndDelay()
    {
        var mail = new RecordingMail();
        (WorldRuntime world, CreatureMapSystem system) = BuildMail(mail, MailStep(7777, 3600));
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 2000);
            (Player receiver, uint sender, uint template, uint money, uint delay) = Assert.Single(mail.Calls);
            Assert.Same(player, receiver);
            Assert.Equal((7777u, 40u, 25u, 3600u), (sender, template, money, delay));
        }
    }

    [Fact]
    public void SendMailCommand_WithoutAlternativeSenderUsesTheSourceCreatureAndNoNegativeDelay()
    {
        var mail = new RecordingMail();
        (WorldRuntime world, CreatureMapSystem system) = BuildMail(mail, MailStep(0, -5));
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 2000);
            (_, uint sender, _, _, uint delay) = Assert.Single(mail.Calls);
            Assert.Equal((giver.Entry, 0u), (sender, delay));
        }
    }

    [Fact]
    public void SendMailCommand_WithoutAPlayerTargetDoesNothing()
    {
        var mail = new RecordingMail();
        (WorldRuntime world, CreatureMapSystem system) = BuildMail(mail, MailStep(7777, 0));
        using (world)
        {
            _ = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, null));
            Run(world, 2000);
            Assert.Empty(mail.Calls);
        }
    }

    [Fact]
    public void SendMailCommand_WithoutAMailServiceIsSkippedWithoutThrowing()
    {
        (WorldRuntime world, CreatureMapSystem system) = BuildMail(null, MailStep(7777, 0));
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 2000);
        }
    }
}
