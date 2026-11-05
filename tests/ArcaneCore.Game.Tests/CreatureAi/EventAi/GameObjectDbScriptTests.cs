using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
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
