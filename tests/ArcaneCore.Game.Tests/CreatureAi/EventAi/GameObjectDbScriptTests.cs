using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using GameObjectTestKit = ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// The game object DB scripts (<c>dbscripts_on_go_template_use</c>, <c>dbscripts_on_go_use</c>; mangos-classic GameObject::Use,
/// GameObject.cpp:1490, :1506, :1703), SCRIPT_COMMAND_INTERRUPT_SPELL (47, ScriptMgr.cpp:2983-2991) and the CAST_SPELL step of a game object
/// source with COMMAND_ADDITIONAL (ScriptMgr.cpp:2169-2181). Rows and ids are synthetic. Written without a build: unproven until the gate.
/// </summary>
public sealed class GameObjectDbScriptTests
{
    private const uint ObjectEntry = 50000;
    private const uint ObjectGuid = 50001;
    private const uint TemplateVariable = 19030;
    private const uint GuidVariable = 19031;
    private const uint GatedQuest = 19032; // the object system of these tests has no quest journal, so no player has it

    private sealed class InterruptRecorder : IUnitSpellQueries
    {
        public List<(Unit Unit, uint Slot)> Interrupts { get; } = [];

        public int GetAuraStacks(Unit unit, uint spellId) => 0;

        public bool IsCasting(Unit unit) => false;

        public bool InterruptSpell(Unit unit, uint currentSpellType)
        {
            Interrupts.Add((unit, currentSpellType));
            return true;
        }
    }

    private static RelayScriptStep SetVariable(uint id, uint variable, int value) => new(
        id, 0, 0, 53, 0, 0, 0, 0, 0, 0, (int)variable, value, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static RelayScriptStep Command(uint id, uint command, uint dataLong, uint flags = 0) => new(
        id, 0, 0, command, dataLong, 0, 0, 0, 0, flags, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class WarningLog : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private const string QuestEndTable = "(script of table dbscripts_on_quest_end)";

    private static RelayScriptStep Delayed(uint id, uint delayMs, uint command, uint dataLong = 0) => new(
        id, delayMs, 0, command, dataLong, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static (WorldRuntime World, CreatureMapSystem System, WarningLog Log) LoggedSystem(
        IEnumerable<(DbScriptKind Kind, RelayScriptStep Step)> dbScripts, IEnumerable<RelayScriptStep>? relays = null)
    {
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(dbScripts),
            RelayScripts = new RelayScriptCatalog(relays ?? [], []),
        };
        var log = new WarningLog();
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0, 0);
        var system = new CreatureMapSystem(map, new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai),
            random: new Random(1), logger: log, aiServices: new CreatureAiServices());
        map.AddUpdater(system);
        return (world, system, log);
    }

    [Fact]
    public void SkippedStep_OfANonRelayScript_NamesItsTable_AndARelayKeepsThePlainWording()
    {
        (WorldRuntime world, CreatureMapSystem system, WarningLog log) = LoggedSystem(
            [(DbScriptKind.QuestEnd, Delayed(77, 0, 999))], [Delayed(88, 0, 998)]);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, 77, giver, player));
            Run(world, 100);
            string quest = Assert.Single(log.Warnings, w => w.Contains("command 999", StringComparison.Ordinal));
            Assert.EndsWith(QuestEndTable, quest, StringComparison.Ordinal);

            Assert.True(system.StartRelayScript(88, giver, player));
            Run(world, 100);
            string relay = Assert.Single(log.Warnings, w => w.Contains("command 998", StringComparison.Ordinal));
            Assert.DoesNotContain("script of table", relay, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheTableName_SurvivesANestedRelayStart_AndADelayedStep()
    {
        // quest end 77: step 1 starts relay 88 (whose step is skipped as a relay), step 2 is skipped at once, step 3 is skipped after 500 ms.
        (WorldRuntime world, CreatureMapSystem system, WarningLog log) = LoggedSystem(
            [
                (DbScriptKind.QuestEnd, Delayed(77, 0, 45, 88)),
                (DbScriptKind.QuestEnd, Delayed(77, 0, 997)),
                (DbScriptKind.QuestEnd, Delayed(77, 500, 996)),
            ],
            [Delayed(88, 0, 995)]);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, 77, giver, player));
            Run(world, 100);

            string nested = Assert.Single(log.Warnings, w => w.Contains("command 995", StringComparison.Ordinal));
            Assert.DoesNotContain("script of table", nested, StringComparison.Ordinal);
            string after = Assert.Single(log.Warnings, w => w.Contains("command 997", StringComparison.Ordinal));
            Assert.EndsWith(QuestEndTable, after, StringComparison.Ordinal);
            Assert.DoesNotContain(log.Warnings, w => w.Contains("command 996", StringComparison.Ordinal));

            Run(world, 600);
            string delayed = Assert.Single(log.Warnings, w => w.Contains("command 996", StringComparison.Ordinal));
            Assert.EndsWith(QuestEndTable, delayed, StringComparison.Ordinal);
        }
    }

    /// <summary>The template script and the guid script of the test object, each one step <paramref name="delayMs"/> away, so a started run shows as a pending step.</summary>
    private static DbScriptCatalog DelayedUseScripts(uint delayMs) => new(
    [
        (DbScriptKind.GameObjectTemplate, new RelayScriptStep(ObjectEntry, delayMs, 0, 53, 0, 0, 0, 0, 0, 0, (int)TemplateVariable, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
        (DbScriptKind.GameObject, new RelayScriptStep(ObjectGuid, delayMs, 0, 53, 0, 0, 0, 0, 0, 0, (int)GuidVariable, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
    ]);

    /// <summary>The auto-close column of <paramref name="type"/>, in template units (seconds * 0x10000, GameObjectInfoView.AutoCloseSeconds).</summary>
    private static (int Index, uint Value)[] AutoClose(GameObjectType type, uint seconds)
        => seconds == 0 ? [] : [(type == GameObjectType.Goober ? 3 : 2, seconds * 0x10000)];

    private static GameObjectMapSystem AddObject(Map map, GameObjectType type, params (int Index, uint Value)[] data)
    {
        var objects = new GameObjectMapSystem(map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(ObjectEntry, type, data)], [GameObjectTestKit.GoSpawn(ObjectGuid, ObjectEntry, 0, 10)], [], [], []));
        map.AddUpdater(objects);
        return objects;
    }

    [Theory]
    [InlineData(5u, 0u, GameObjectUseResult.OnCooldown)] // a goober on cooldown (data6 seconds)
    [InlineData(0u, 10u, GameObjectUseResult.InUse)] // a goober waiting for its auto-close time (data3)
    public void AGooberInUse_RefusesTheClick_AndTheScriptPathRefusal_RunsNoTemplateScriptEither(uint cooldown, uint autoClose, GameObjectUseResult scriptPathRefusal)
    {
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog([(DbScriptKind.GameObjectTemplate, SetVariable(ObjectEntry, TemplateVariable, 1))]),
        };
        (WorldRuntime world, Map map, _) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            (int Index, uint Value)[] data = autoClose > 0 ? AutoClose(GameObjectType.Goober, autoClose) : [(6, cooldown)];
            GameObjectMapSystem objects = AddObject(map, GameObjectType.Goober, data);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid));
            Assert.Equal(1, ConditionRuntimeState.For(world).GetMapVariable(map, TemplateVariable));

            ConditionRuntimeState.For(world).SetMapVariable(map, TemplateVariable, 0);
            Assert.Equal(GameObjectUseResult.InUse, objects.Use(player, go.Guid));
            Assert.Equal(0, ConditionRuntimeState.For(world).GetMapVariable(map, TemplateVariable));

            Assert.Equal(scriptPathRefusal, objects.UseByUnit(player, go));
            Assert.Equal(0, ConditionRuntimeState.For(world).GetMapVariable(map, TemplateVariable));
        }
    }

    [Theory]
    [InlineData(GameObjectType.Door, 0u)]
    [InlineData(GameObjectType.Door, 60u)]
    [InlineData(GameObjectType.Button, 0u)]
    [InlineData(GameObjectType.Button, 60u)]
    [InlineData(GameObjectType.Goober, 0u)]
    [InlineData(GameObjectType.Goober, 60u)]
    public void ASecondClickOnAnObjectInUse_IsRefused_AndStartsNeitherScript(GameObjectType type, uint autoCloseSeconds)
    {
        // HandleGameObjectUseOpcode returns for GO_FLAG_IN_USE before GameObject::Use (SpellHandler.cpp:295-296). Set for a door or button by
        // SwitchDoorOrButton (GameObject.cpp:1385-1388) and for a goober at :1650.
        var ai = new CreatureAiContent([], []) { DbScripts = DelayedUseScripts(5000) };
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            GameObjectMapSystem objects = AddObject(map, type, AutoClose(type, autoCloseSeconds));
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid));
            Assert.True(go.Flags.HasFlag(GameObjectFlags.InUse));
            Assert.Equal(2, system.PendingDbScriptSteps);

            Assert.Equal(GameObjectUseResult.InUse, objects.Use(player, go.Guid));
            Assert.Equal(GameObjectUseResult.InUse, objects.Use(player, go.Guid));
            Assert.Equal(2, system.PendingDbScriptSteps);
        }
    }

    [Fact]
    public void RepeatClicksOnAQuestGatedGoober_ByAPlayerWithoutTheQuest_RunTheTemplateScriptOnce_UntilTheAutoCloseTime()
    {
        // GameObject::Use sets GO_FLAG_IN_USE (GameObject.cpp:1650) before the quest gate (:1691-1693), so the opcode refuses the repeats
        // (SpellHandler.cpp:295-296) until GameObject::Update clears the flag (:549-552). The guid script is behind the gate and never starts.
        var ai = new CreatureAiContent([], []) { DbScripts = DelayedUseScripts(5000) };
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            GameObjectMapSystem objects = AddObject(map, GameObjectType.Goober, [(1, GatedQuest), .. AutoClose(GameObjectType.Goober, 1)]);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.NeedsQuest, objects.Use(player, go.Guid));
            Assert.True(go.Flags.HasFlag(GameObjectFlags.InUse));
            Assert.Equal(1, system.PendingDbScriptSteps);

            Assert.Equal(GameObjectUseResult.InUse, objects.Use(player, go.Guid));
            Assert.Equal(GameObjectUseResult.InUse, objects.Use(player, go.Guid));
            Assert.Equal(1, system.PendingDbScriptSteps);

            Run(world, 2500); // whole-second clock: the goober closes 1 to 2 seconds after the use
            Assert.False(go.Flags.HasFlag(GameObjectFlags.InUse));
            Assert.Equal(GameObjectUseResult.NeedsQuest, objects.Use(player, go.Guid));
            Assert.Equal(2, system.PendingDbScriptSteps);
        }
    }

    [Fact]
    public void TheCooldownOfAQuestGatedGoober_StartsOnAClickWithoutTheQuest_AndHoldsBackTheTemplateScript()
    {
        // m_cooldownTime is set at GameObject.cpp:1485, above the template script at :1490 and the quest gate at :1691-1693. No auto-close time
        // here, so the in-use flag goes first and the cooldown alone refuses the later click.
        var ai = new CreatureAiContent([], []) { DbScripts = DelayedUseScripts(5000) };
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            GameObjectMapSystem objects = AddObject(map, GameObjectType.Goober, (1, GatedQuest), (6, 10));
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.NeedsQuest, objects.Use(player, go.Guid));
            Assert.Equal(1, system.PendingDbScriptSteps);

            Run(world, 2500);
            Assert.False(go.Flags.HasFlag(GameObjectFlags.InUse));
            Assert.Equal(GameObjectUseResult.OnCooldown, objects.Use(player, go.Guid));
            Assert.Equal(1, system.PendingDbScriptSteps);
        }
    }

    [Fact]
    public void TheScriptPath_ByAPlayerWithoutTheQuest_RunsTheTemplateScriptOnce_WhileTheGooberWaitsForItsAutoCloseTime()
    {
        // UseGooberByUnit hands a player to UseGoober, so the first use marks the goober before the quest gate and the not-ready gate refuses the next.
        var ai = new CreatureAiContent([], []) { DbScripts = DelayedUseScripts(5000) };
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            GameObjectMapSystem objects = AddObject(map, GameObjectType.Goober, [(1, GatedQuest), .. AutoClose(GameObjectType.Goober, 60)]);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.NeedsQuest, objects.UseByUnit(player, go));
            Assert.Equal(1, system.PendingDbScriptSteps);

            Assert.Equal(GameObjectUseResult.InUse, objects.UseByUnit(player, go));
            Assert.Equal(1, system.PendingDbScriptSteps);
        }
    }

    [Fact]
    public void ADoorWithoutAnAutoCloseTime_StaysInUse_SoLaterClicksStartNothing()
    {
        // GameObject::Update resets a door only with an auto-close time (GameObject.cpp:521-525).
        var ai = new CreatureAiContent([], []) { DbScripts = DelayedUseScripts(5000) };
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            GameObjectMapSystem objects = AddObject(map, GameObjectType.Door);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid));
            Run(world, 3000);
            Assert.Equal(GameObjectUseResult.InUse, objects.Use(player, go.Guid));
            Assert.Equal(2, system.PendingDbScriptSteps);
        }
    }

    [Theory]
    [InlineData(GameObjectType.Door)]
    [InlineData(GameObjectType.Button)]
    public void TheSpellAndScriptPaths_HaveNoInUseGuard_SoTheyStartTheScriptsOnAnObjectInUse(GameObjectType type)
    {
        // The in-use return is in the opcode handler, not in GameObject::Use, which Spell::SendLoot and the script use call directly.
        var ai = new CreatureAiContent([], []) { DbScripts = DelayedUseScripts(5000) };
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            GameObjectMapSystem objects = AddObject(map, type);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid));
            Assert.Equal(2, system.PendingDbScriptSteps);

            Assert.Equal(GameObjectUseResult.InUse, objects.UseByUnit(player, go));
            Assert.Equal(4, system.PendingDbScriptSteps);

            Assert.Equal(GameObjectUseResult.InUse, objects.OpenLock(player, go.Guid, LockType.Open));
            Assert.Equal(6, system.PendingDbScriptSteps);
        }
    }

    [Fact]
    public void AClickOnAGenericObject_StartsNoTemplateScript()
    {
        // HandleGameObjectUseOpcode never hands GAMEOBJECT_TYPE_GENERIC to GameObject::Use (SpellHandler.cpp:289-293).
        var ai = new CreatureAiContent([], []) { DbScripts = DelayedUseScripts(5000) };
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            GameObjectMapSystem objects = AddObject(map, GameObjectType.Generic);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.NotUsable, objects.Use(player, go.Guid));
            Assert.Equal(0, system.PendingDbScriptSteps);
        }
    }

    [Theory]
    [InlineData(DbScriptKind.GameObjectTemplate)]
    [InlineData(DbScriptKind.GameObject)]
    public void ARepeatUseOnceTheObjectIsUsableAgain_WhileADelayedStepIsPending_QueuesASecondRun(DbScriptKind kind)
    {
        // GameObject::Use starts the script with the default SCRIPT_EXEC_PARAM_NONE (GameObject.cpp:1490, :1529; Map.h:233-240), so nothing drops
        // a run for the pending step of an earlier one. The button here resets long before its 5000 ms step is due; only then does a click
        // get through the opcode's in-use return (SpellHandler.cpp:295-296).
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(
            [
                (kind, new RelayScriptStep(kind == DbScriptKind.GameObject ? ObjectGuid : ObjectEntry, 5000, 0, 53, 0, 0, 0, 0, 0, 0,
                    (int)TemplateVariable, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            ]),
        };
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            GameObjectMapSystem objects = AddObject(map, GameObjectType.Button, AutoClose(GameObjectType.Button, 1));
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid));
            Assert.Equal(GameObjectUseResult.InUse, objects.Use(player, go.Guid));
            Assert.Equal(1, system.PendingDbScriptSteps);

            Run(world, 2500); // whole-second clock: the button resets 1 to 2 seconds after the use
            Assert.False(go.Flags.HasFlag(GameObjectFlags.InUse));
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid)); // the second run is due 5000 ms after this use
            Assert.Equal(2, system.PendingDbScriptSteps);

            Run(world, 3000); // the first run's step fires at 5000 ms
            Assert.Equal(1, system.PendingDbScriptSteps);
            Assert.Equal(1, ConditionRuntimeState.For(world).GetMapVariable(map, TemplateVariable));

            ConditionRuntimeState.For(world).SetMapVariable(map, TemplateVariable, 0);
            Run(world, 2500); // the second run's step fires at 7500 ms
            Assert.Equal(0, system.PendingDbScriptSteps);
            Assert.Equal(1, ConditionRuntimeState.For(world).GetMapVariable(map, TemplateVariable));
        }
    }

    [Theory]
    [InlineData(GameObjectType.Goober)]
    [InlineData(GameObjectType.Door)]
    public void PlayerUse_StartsTheTemplateAndTheGuidScript(GameObjectType type)
    {
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(
            [
                (DbScriptKind.GameObjectTemplate, SetVariable(ObjectEntry, TemplateVariable, 1)),
                (DbScriptKind.GameObject, SetVariable(ObjectGuid, GuidVariable, 1)),
            ]),
        };
        (WorldRuntime world, Map map, _) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            var objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(ObjectEntry, type)], [GameObjectTestKit.GoSpawn(ObjectGuid, ObjectEntry, 0, 10)], [], [], []));
            map.AddUpdater(objects);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid));
            Assert.Equal(1, ConditionRuntimeState.For(world).GetMapVariable(map, TemplateVariable));
            Assert.Equal(1, ConditionRuntimeState.For(world).GetMapVariable(map, GuidVariable));
        }
    }

    [Fact]
    public void AGuidScriptOfAnotherSpawn_DoesNotRun_ButTheTemplateScriptDoes()
    {
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(
            [
                (DbScriptKind.GameObjectTemplate, SetVariable(ObjectEntry, TemplateVariable, 1)),
                (DbScriptKind.GameObject, SetVariable(ObjectGuid + 1, GuidVariable, 1)),
            ]),
        };
        (WorldRuntime world, Map map, _) = CreateAiSystem(new CreatureContent([Template()], [], [], [], [], ai));
        using (world)
        {
            var objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(ObjectEntry, GameObjectType.Goober)], [GameObjectTestKit.GoSpawn(ObjectGuid, ObjectEntry, 0, 10)], [], [], []));
            map.AddUpdater(objects);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid));
            Assert.Equal(1, ConditionRuntimeState.For(world).GetMapVariable(map, TemplateVariable));
            Assert.Equal(0, ConditionRuntimeState.For(world).GetMapVariable(map, GuidVariable));
        }
    }

    [Theory]
    [InlineData(3u, true)]
    [InlineData(0u, true)]
    [InlineData(4u, false)]
    public void InterruptSpell_InterruptsTheSourceUnitsSlot_AndRefusesASlotAboveThree(uint slot, bool interrupts)
    {
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestEnd, Command(77, 47, slot))]) };
        var recorder = new InterruptRecorder();
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(
            new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai), new CreatureAiServices { UnitSpells = recorder });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, 77, giver, player));
            Run(world, 100);
            if (interrupts)
            {
                (Unit unit, uint recordedSlot) = Assert.Single(recorder.Interrupts);
                Assert.Same(giver, unit);
                Assert.Equal(slot, recordedSlot);
            }
            else
            {
                Assert.Empty(recorder.Interrupts);
            }
        }
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(8u, 0)]
    public void CastSpell_ByAGameObject_MakesTheUnitTargetCastOnItself_UnlessCommandAdditionalClearsTheTarget(uint flags, int casts)
    {
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.GameObject, Command(ObjectGuid, 15, 12345, flags))]) };
        var spells = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(
            new CreatureContent([Template()], [], [], [], [], ai), new CreatureAiServices { Spells = spells });
        using (world)
        {
            var objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(ObjectEntry, GameObjectType.Goober)], [GameObjectTestKit.GoSpawn(ObjectGuid, ObjectEntry, 0, 10)], [], [], []));
            map.AddUpdater(objects);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.True(system.StartDbScript(DbScriptKind.GameObject, ObjectGuid, go, player));
            Run(world, 100);
            Assert.Equal(casts, spells.UnitCasts.Count);
            if (casts > 0)
            {
                (Unit caster, uint spell, Unit? target, bool triggered) = spells.UnitCasts.Single();
                Assert.Same(player, caster);
                Assert.Same(player, target);
                Assert.Equal(12345u, spell);
                Assert.True(triggered);
            }
        }
    }

    [Theory]
    [InlineData(2u, 2)]
    [InlineData(0u, 0)]
    [InlineData(3u, -1)] // above SHEATH_STATE_RANGED: the loader skips the row, so the state is left as it was
    public void SetSheathe_SetsTheSheathStateOfTheSourceUnit(uint state, int expected)
    {
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestEnd, Command(77, 54, state))]) };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai));
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            giver.SetByte(UpdateFields.UnitFieldBytes2, 0, 1);
            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, 77, giver, player));
            Run(world, 100);
            Assert.Equal(expected < 0 ? 1 : expected, (int)giver.GetByte(UpdateFields.UnitFieldBytes2, 0));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Meetingstone_QueuesTheTargetPlayerForTheArea_OrIsSkippedWithoutAQueue(bool withQueue)
    {
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.Gossip, Command(2000, 200, 1583))]) };
        List<(Player Player, uint Area)> joins = [];
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(
            new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai),
            new CreatureAiServices { JoinMeetingStone = withQueue ? (player, area) =>
            {
                joins.Add((player, area));
                return true;
            }
            : null });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.Gossip, 2000, giver, player));
            Run(world, 100);
            if (withQueue)
            {
                (Player queued, uint area) = Assert.Single(joins);
                Assert.Same(player, queued);
                Assert.Equal(1583u, area);
            }
            else
            {
                Assert.Empty(joins);
            }
        }
    }

    [Fact]
    public void TableOf_NamesTheTableOfEveryKind()
    {
        Assert.Equal("dbscripts_on_go_use", DbScriptCatalog.TableOf(DbScriptKind.GameObject));
        Assert.Equal("dbscripts_on_go_template_use", DbScriptCatalog.TableOf(DbScriptKind.GameObjectTemplate));
        Assert.Equal("dbscripts_on_creature_movement", DbScriptCatalog.TableOf(DbScriptKind.CreatureMovement));
        Assert.Equal("dbscripts_on_relay", DbScriptCatalog.TableOf(DbScriptKind.Relay));
        Assert.Equal(3, (int)DbScriptKind.GameObject);
        Assert.Equal(4, (int)DbScriptKind.GameObjectTemplate);
    }
}
