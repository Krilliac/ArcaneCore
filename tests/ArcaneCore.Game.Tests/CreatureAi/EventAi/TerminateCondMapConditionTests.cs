using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// SCRIPT_COMMAND_TERMINATE_COND (cmangos ScriptMgr.cpp:2720-2758) with the ClassicDB z2815 condition rows its 44 loaded uses name: 41 are
/// about the map, not a player - 36 DEAD_OR_AWAY (317, 318), 39 SPAWN_COUNT (606) and an OR over 37 CREATURE_IN_RANGE (1386)
/// (Conditions.cpp:299-308, 424-466, 484-489). The script is the escort shape of quest_start 667/1090/3382/5713: the giver is the source,
/// the player the target, the emote after the gate shows whether the script went on.
/// </summary>
public sealed class TerminateCondMapConditionTests
{
    private const uint ScriptId = 667;
    private const uint FailQuest = 667;
    private const uint Giver = 2768;
    private const uint Demetria = 12339;
    private const uint Lathoric = 8391, Dorius = 8421, Grark = 8400;

    private static readonly ConditionRecord[] Rows =
    [
        new(317, 36, 0, 60, 0, 0, 0),   // player dead or out of 60 yards
        new(318, 36, 3, 0, 0, 0, 0),    // the creature source dead
        new(606, 39, Demetria, 1, 0, 0, 0),
        new(1380, 37, Grark, 80, 0, 0, 0),
        new(1381, 37, Lathoric, 80, 0, 0, 0),
        new(1382, 37, Dorius, 10, 0, 0, 0),
        new(1383, -3, 1380, 0, 0, 0, 0),   // NOT Grark within 80
        new(1384, -2, 1381, 1382, 0, 0, 0), // Lathoric within 80 OR Dorius within 10
        new(1386, -2, 1383, 1384, 0, 0, 0),
    ];

    private static RelayScriptStep Step(uint delay, uint command, uint dataLong, uint dataLong2 = 0)
        => new(ScriptId, delay, 0, command, dataLong, dataLong2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class Quests : IScriptQuestEvents
    {
        public List<uint> GroupFailed { get; } = [];
        public void AreaExploredOrEventHappens(Player player, uint questId) { }
        public void FailQuest(Player player, uint questId) { }
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) { }
        public void GroupEventFailHappens(Player player, uint questId) => GroupFailed.Add(questId);
        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
    }

    /// <summary>The world's shape: ConditionFeature forwards to the table evaluator it holds, it is not one itself.</summary>
    private sealed class Forwarding(ConditionEvaluator inner) : IConditionEvaluator, IConditionTableEvaluator
    {
        public ConditionEvaluator Current => inner;
        public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source) => inner.IsSatisfied(conditionId, player, source);
    }

    private static (WorldRuntime World, Map Map, CreatureMapSystem System, Quests Quests) Create(RelayScriptStep[] steps, uint[] extraTemplates,
        Action<CreatureTemplateBuilder>? extraConfigure = null)
    {
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([.. steps.Select(s => (DbScriptKind.QuestStart, s))]) };
        CreatureTemplate[] templates =
        [
            Template(Giver, b => b.Faction = 35),
            .. extraTemplates.Select(entry => Template(entry, b => { b.Faction = 35; extraConfigure?.Invoke(b); })),
        ];
        var content = new CreatureContent(templates, [Spawn(1, Giver, 0, 0)], [], [], [], ai);
        var quests = new Quests();
        var conditions = new Forwarding(new ConditionEvaluator(ConditionTable.Build(Rows), new ConditionContext()));
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Conditions = conditions, ScriptQuests = quests });
        return (world, map, system, quests);
    }

    [Theory]
    [InlineData(10f, false, 10u, false)]  // near and both alive: the escort goes on
    [InlineData(200f, false, 0u, true)]   // the player out of 60 yards: 317 stops it and fails the quest
    [InlineData(10f, true, 0u, true)]     // the escort dead: 318 stops it and fails the quest
    public void DeadOrAway_StopsTheEscortScript_AndFailsTheQuest(float playerX, bool killGiver, uint finalEmote, bool failed)
    {
        (WorldRuntime world, Map map, CreatureMapSystem system, Quests quests) = Create(
            [Step(50, 34, 318, FailQuest), Step(50, 34, 317, FailQuest), Step(100, 1, 10)], []);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, playerX, 0);
            Creature giver = Assert.Single(system.Creatures);
            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            if (killGiver)
            {
                map.Combat.Kill(player, giver);
            }

            Run(world, 200);
            Assert.Equal(finalEmote, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
            Assert.Equal(failed ? [FailQuest] : [], quests.GroupFailed);
        }
    }

    [Theory]
    [InlineData(false, 10u)] // no Demetria: the script goes on to summon her
    [InlineData(true, 0u)]   // "Terminate if Demetria is already spawned" (quest_start 6148)
    public void SpawnCount_StopsTheScriptWhenTheCountedCreatureIsAlreadyThere(bool demetriaSpawned, uint finalEmote)
    {
        (WorldRuntime world, _, CreatureMapSystem system, Quests quests) = Create(
            [Step(0, 34, 606), Step(100, 1, 10)], [Demetria], b => b.ExtraFlags = 0x00200000);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 10, 0);
            Creature giver = Assert.Single(system.Creatures, c => c.Template.Entry == Giver);
            if (demetriaSpawned)
            {
                system.SpawnTemporary(Template(Demetria, b => { b.Faction = 35; b.ExtraFlags = 0x00200000; }), 30, 30, 83.5f, 0);
            }

            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 200);
            Assert.Equal(finalEmote, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
            Assert.Empty(quests.GroupFailed);
        }
    }

    [Theory]
    [InlineData(new uint[] { Grark }, 10u)]            // Grark near the player, no Lathoric or Dorius: the script goes on
    [InlineData(new uint[0], 0u)]                      // no Grark: NOT(37) holds and the OR stops it
    [InlineData(new uint[] { Grark, Lathoric }, 0u)]   // Lathoric near the player stops it
    public void OrOverCreatureInRange_IsDecidedAroundThePlayer(uint[] nearby, uint finalEmote)
    {
        (WorldRuntime world, _, CreatureMapSystem system, _) = Create([Step(0, 34, 1386), Step(100, 1, 10)], [Grark, Lathoric, Dorius]);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 100, 0);
            Creature giver = Assert.Single(system.Creatures, c => c.Template.Entry == Giver);
            foreach (uint entry in nearby)
            {
                system.SpawnTemporary(Template(entry, b => b.Faction = 35), 105, 0, 83.5f, 0);
            }

            Assert.True(system.StartDbScript(DbScriptKind.QuestStart, ScriptId, giver, player));
            Run(world, 200);
            Assert.Equal(finalEmote, giver.GetUInt32(UpdateFields.UnitNpcEmotestate));
        }
    }
}
