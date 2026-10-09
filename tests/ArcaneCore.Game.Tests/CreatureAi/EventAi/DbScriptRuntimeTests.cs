using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using GameObjectTestKit = ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>Quest, gossip and event DB scripts use the relay command executor and map clock, with independent script namespaces.</summary>
public sealed class DbScriptRuntimeTests
{
    private const uint ScriptId = 54; // an id used by several ClassicDB namespaces; the emote steps below are synthetic.

    private static RelayScriptStep Emote(uint delay, uint emote) => new(
        ScriptId, delay, 0, 1, emote, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class Conditions(bool result) : IConditionEvaluator
    {
        public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source) => result;
    }

    [Fact]
    public void GooberEventId_StartsTheEventNamespaceWithThePlayerAsSource()
    {
        const uint eventId = 9042;
        RelayScriptStep credit = Emote(0, 0) with { Id = eventId, Command = 8, DataLong = 2044 };
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.Event, credit)]) };
        CreatureContent content = new([Template()], [], [], [], [], ai);
        var objectives = new ObjectiveRecorder();
        (WorldRuntime world, Map map, _) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = objectives });
        using (world)
        {
            var objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(50000, GameObjectType.Goober, (2, eventId))],
                [GameObjectTestKit.GoSpawn(50001, 50000, 0, 10)], [], [], []));
            map.AddUpdater(objects);
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            GameObject go = Assert.Single(objects.GameObjects);
            Assert.Equal(GameObjectUseResult.Ok, objects.Use(player, go.Guid));
            Assert.Equal([2044u], objectives.KillCredit);
        }
    }

    [Theory]
    [InlineData(true, 0u, 0u)]
    [InlineData(false, 0u, 10u)]
    [InlineData(true, 8u, 10u)]
    public void TerminateCond_StopsOnlyWhenTheConditionMatches(bool condition, uint flags, uint finalEmote)
    {
        RelayScriptStep gate = Emote(0, 0) with { Command = 34, DataLong = 901, DataFlags = flags };
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([
            (DbScriptKind.QuestStart, gate), (DbScriptKind.QuestStart, Emote(100, 10))]) };
        CreatureContent content = new([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Conditions = new Conditions(condition) });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 100);
            Assert.Equal(finalEmote, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
        }
    }

    [Fact]
    public void TerminateCond_FailsTheQuestForThePlayersGroup()
    {
        RelayScriptStep gate = Emote(0, 0) with { Command = 34, DataLong = 901, DataLong2 = 667 };
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestStart, gate)]) };
        CreatureContent content = new([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai);
        var objectives = new ObjectiveRecorder();
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Conditions = new Conditions(true), ScriptQuests = objectives });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, Assert.Single(system.Creatures), player));
            Assert.Equal([667u], objectives.GroupFailed);
        }
    }

    [Fact]
    public void ScriptWithoutAWorldSource_RunsOnlyTheStepsWhoseBuddyIsTheSource()
    {
        // An item-started quest (cmangos ScriptAction::HandleScriptStep, ScriptMgr.cpp:1720-1760: an item is no world object): the kill
        // credit without a buddy has no source and is skipped; the emote with a buddy entry finds the wolf near the player and runs on it.
        RelayScriptStep plain = Emote(0, 0) with { Command = 8, DataLong = 2044 };
        RelayScriptStep buddied = Emote(0, 26) with { BuddyEntry = WolfEntry, SearchRadius = 30 };
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestStart, plain), (DbScriptKind.QuestStart, buddied)]) };
        var objectives = new ObjectiveRecorder();
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(
            new CreatureContent([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai), new CreatureAiServices { ScriptQuests = objectives });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, source: null, player));
            Assert.Empty(objectives.KillCredit);
            Assert.Equal(26u, Assert.Single(system.Creatures).GetUInt32(UpdateFields.UnitNpcEmotestate));
        }
    }

    private sealed class ObjectiveRecorder : IScriptQuestEvents
    {
        public List<uint> Explored { get; } = [];
        public List<uint> Failed { get; } = [];
        public List<uint> GroupFailed { get; } = [];
        public List<uint> KillCredit { get; } = [];

        public void AreaExploredOrEventHappens(Player player, uint questId) => Explored.Add(questId);
        public void FailQuest(Player player, uint questId) => Failed.Add(questId);
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) => KillCredit.Add(creatureEntry);
        public void GroupEventFailHappens(Player player, uint questId) => GroupFailed.Add(questId);
        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
    }

    [Fact]
    public void ScriptKindsWithTheSameId_RunIndependently_UsingTheMapClock()
    {
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(
            [
                (DbScriptKind.QuestStart, Emote(0, 10)),
                (DbScriptKind.QuestEnd, Emote(500, 13)),
                (DbScriptKind.Gossip, Emote(0, 26)),
                (DbScriptKind.Event, Emote(0, 27)),
            ]),
        };
        CreatureContent content = new([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);

            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Assert.Equal(10u, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
            Assert.True(system.StartDbScript(DbScriptKind.QuestEnd, ScriptId, giver, player));
            Assert.False(system.StartDbScript(DbScriptKind.QuestEnd, ScriptId, giver, player));
            Assert.True(system.StartDbScript(DbScriptKind.Gossip, ScriptId, giver, player));
            Assert.Equal(26u, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
            Assert.True(system.StartDbScript(DbScriptKind.Event, ScriptId, giver, player));
            Assert.Equal(27u, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
            Run(world, 450);
            Assert.Equal(27u, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
            Run(world, 50);
            Assert.Equal(13u, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
        }
    }

    [Fact]
    public void ClassicDbQuest68_SpawnsAForlornSpirit_ThenOrdersItToAttackThePlayer()
    {
        // ClassicDB z2815 dbscripts_on_quest_start 68: TEMP_SPAWN_CREATURE 2044 at delay 0,
        // ATTACK_START by the buddy entry at 3,000 ms. The location is synthetic for this map test.
        RelayScriptStep spawn = new(68, 0, 0, 10, 2044, 300_000, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 5, 0, 83.5f, 0, 0, 0);
        RelayScriptStep attack = new(68, 3_000, 0, 26, 0, 0, 0, 2044, 25, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog([(DbScriptKind.QuestStart, spawn), (DbScriptKind.QuestStart, attack)]),
        };
        CreatureContent content = new([Template(), Template(2044)], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, 68, giver, player));
            Creature spirit = Assert.Single(system.Creatures, c => c.Entry == 2044);
            Assert.Null(spirit.Combat.Victim);
            Run(world, 2_900);
            Assert.Null(spirit.Combat.Victim);
            Run(world, 100);
            Assert.Same(player, spirit.Combat.Victim);
        }
    }

    [Fact]
    public void QuestCreditCommands_UseTheQuestObjectiveAdapter_AndCheckDistance()
    {
        // ClassicDB gossip script 21 is QUEST_EXPLORED 6981 with no distance; the distances 12 and 5 here are synthetic.
        RelayScriptStep explore = new(21, 0, 0, 7, 6981, 12, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        RelayScriptStep distant = explore with { Id = 22, DataLong2 = 5 };
        RelayScriptStep credit = explore with { Id = 23, Command = 8, DataLong = 2044, DataLong2 = 0 };
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog(
                [(DbScriptKind.Gossip, explore), (DbScriptKind.Gossip, distant), (DbScriptKind.Gossip, credit)]),
        };
        CreatureContent content = new([Template()], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai);
        var objectives = new ObjectiveRecorder();
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { ScriptQuests = objectives });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.Gossip, 21, giver, player));
            Assert.True(system.StartDbScript(DbScriptKind.Gossip, 22, giver, player));
            Assert.True(system.StartDbScript(DbScriptKind.Gossip, 23, giver, player));

            Assert.Equal([6981u], objectives.Explored);
            Assert.Equal([6981u], objectives.Failed);
            Assert.Equal([2044u], objectives.KillCredit);
        }
    }
}
