using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone script batch 16: the Gizelton caravan of Desolace (desolace.cpp npc_cork_gizelton / npc_rigger_gizelton), against a real map with
/// a recording quest service. The escort paths 11625 / 11626 and the caravan loop 19019 are short stand-ins laid along the x axis.
/// </summary>
public sealed class ZoneScriptBatch16Tests
{
    private const float Z = 83.5f;
    private const uint HugeHealth = 1_000_000;

    private sealed class Quests : IScriptQuestEvents, IEventAiQuestEvents
    {
        public HashSet<uint> Incomplete { get; } = [];

        public List<(Player Player, uint QuestId)> Completed { get; } = [];

        public List<(Player Player, uint QuestId)> Failed { get; } = [];

        public bool IsQuestIncomplete(Player player, uint questId) => Incomplete.Contains(questId);

        public void AreaExploredOrEventHappens(Player player, uint questId) => Completed.Add((player, questId));

        public void FailQuest(Player player, uint questId)
        {
        }

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source)
        {
        }

        public void GroupEventFailHappens(Player player, uint questId) => Failed.Add((player, questId));

        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];

        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
        {
            Assert.True(rewardGroup);
            Completed.Add((player, questId));
        }

        public void KillCredit(Player player, uint creatureEntry, Creature source)
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            CreatureContent content = new(
                [
                    Template(CorkGizeltonAI.Entry, Tpl("Cork Gizelton")), Template(RiggerGizeltonAI.Entry, Tpl("Rigger Gizelton")),
                    Template(CorkGizeltonAI.NpcCaravanKodo, Tpl("Caravan Kodo")),
                ],
                [
                    Spawn(1, CorkGizeltonAI.Entry, 0f, 0f, Z), Spawn(2, RiggerGizeltonAI.Entry, -3f, 2f, Z),
                    Spawn(3, CorkGizeltonAI.NpcCaravanKodo, -6f, 0f, Z), Spawn(4, CorkGizeltonAI.NpcCaravanKodo, -9f, 0f, Z),
                ],
                [], [], [], new CreatureAiContent([], []),
                entryWaypoints: [.. Path(CorkGizeltonAI.PathCork, 19), .. Path(CorkGizeltonAI.PathRigger, 21), .. Path(CorkGizeltonAI.CaravanPathId, 120)]);
            var services = new CreatureAiServices { ScriptQuests = Quest, QuestEvents = Quest };
            (World, Map, System) = CreateAiSystem(content, services);
            (Player, _) = AddPlayer(World, 1, 1f, 0f);
            Player.Relocate(1f, 0f, Z, 0, 0);
            Player.MaxHealth = HugeHealth;
            Player.Health = HugeHealth;
            Cork = Assert.Single(System.Creatures, c => c.Template.Entry == CorkGizeltonAI.Entry);
            Rigger = Assert.Single(System.Creatures, c => c.Template.Entry == RiggerGizeltonAI.Entry);
            Kodos = [.. System.Creatures.Where(c => c.Template.Entry == CorkGizeltonAI.NpcCaravanKodo)];
        }

        public Quests Quest { get; } = new();

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public Creature Cork { get; }

        public Creature Rigger { get; }

        public Creature[] Kodos { get; }

        public CorkGizeltonAI CorkAi => Assert.IsType<CorkGizeltonAI>(Cork.AI);

        public void RunUntil(Func<bool> done, int limitMs = 60_000)
        {
            for (int elapsed = 0; elapsed < limitMs && !done(); elapsed += 100)
            {
                Player.Relocate(Cork.X + 1f, Cork.Y + 1f, Cork.Z, 0, 0);
                World.RunTick(100);
            }

            Assert.True(done(), $"not done within {limitMs} ms");
        }

        public void Dispose() => World.Dispose();
    }

    /// <summary>A waypoint_path of <paramref name="count"/> points half a yard apart from the origin.</summary>
    private static IEnumerable<(uint, uint, CreatureWaypoint)> Path(uint pathId, uint count)
    {
        for (uint point = 1; point <= count; point++)
        {
            yield return (CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | pathId, new CreatureWaypoint(point, point * 0.5f, 0f, Z, 0f, 0));
        }
    }

    private static Action<CreatureTemplateBuilder> Tpl(string name) => b =>
    {
        b.Name = name;
        b.Faction = 35;
        b.MinLevelHealth = HugeHealth;
        b.MaxLevelHealth = HugeHealth;
    };

    [Theory]
    [InlineData(CorkGizeltonAI.Entry, typeof(CorkGizeltonAI))]
    [InlineData(RiggerGizeltonAI.Entry, typeof(RiggerGizeltonAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        using var rig = new Rig();
        Assert.IsType(ai, Assert.Single(rig.System.Creatures, c => c.Template.Entry == entry).AI);
    }

    [Fact]
    public void TheEscortQuestsAreListedAsScripted()
    {
        IReadOnlyCollection<uint> quests = new CreatureAiFactory().ScriptedEventQuests;
        Assert.Contains(CorkGizeltonAI.QuestBodyguardForHire, quests);
        Assert.Contains(CorkGizeltonAI.QuestGizeltonCaravan, quests);
    }

    [Fact]
    public void Cork_BodyguardForHireStartsHisEscortOnPath11625_WatchingTheCaravan_AnotherQuestDoesNothing()
    {
        using var rig = new Rig();
        CorkGizeltonAI ai = rig.CorkAi;

        ai.OnQuestAccept(rig.Player, CorkGizeltonAI.QuestGizeltonCaravan); // Rigger's quest, not Cork's
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));

        ai.OnQuestAccept(rig.Player, CorkGizeltonAI.QuestBodyguardForHire);

        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.False(ai.SouthQuest);
        Assert.False(ai.IsRunning);
        Assert.Equal(19, ai.WaypointCount);
        Assert.Equal(3, ai.Caravan.Count);
        Assert.Contains(rig.Rigger, ai.Caravan);
    }

    [Fact]
    public void Cork_AtPoint19TheGroupGetsTheEvent_AndTheCaravanRunsOnFromPoint33OfItsLoop()
    {
        using var rig = new Rig();
        rig.Quest.Incomplete.Add(CorkGizeltonAI.QuestBodyguardForHire);
        CorkGizeltonAI ai = rig.CorkAi;
        ai.OnQuestAccept(rig.Player, CorkGizeltonAI.QuestBodyguardForHire);

        rig.RunUntil(() => rig.Quest.Completed.Count > 0);
        Assert.Equal([(rig.Player, CorkGizeltonAI.QuestBodyguardForHire)], rig.Quest.Completed);

        rig.RunUntil(() => rig.Cork.Motion.Default is WaypointMovementGenerator, 2_000);
        var loop = Assert.IsType<WaypointMovementGenerator>(rig.Cork.Motion.Default);
        Assert.Equal((int)CorkGizeltonAI.CaravanPointAfterNorthEscort - 1, loop.CurrentIndex);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(rig.Cork.ScriptRun);
        Assert.Empty(rig.Quest.Failed);
    }

    [Fact]
    public void Rigger_GizeltonCaravanSendsTheNearestCorkOnPath11626_CreditAt21_ThenTheLoopFromPoint100()
    {
        using var rig = new Rig();
        rig.Quest.Incomplete.Add(CorkGizeltonAI.QuestGizeltonCaravan);
        var rigger = Assert.IsType<RiggerGizeltonAI>(rig.Rigger.AI);
        CorkGizeltonAI ai = rig.CorkAi;

        rigger.OnQuestAccept(rig.Player, CorkGizeltonAI.QuestBodyguardForHire);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));

        rigger.OnQuestAccept(rig.Player, CorkGizeltonAI.QuestGizeltonCaravan);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(ai.SouthQuest);
        Assert.Equal(21, ai.WaypointCount);

        rig.RunUntil(() => rig.Quest.Completed.Count > 0);
        Assert.Equal([(rig.Player, CorkGizeltonAI.QuestGizeltonCaravan)], rig.Quest.Completed);
        Assert.False(ai.SouthQuest);

        rig.RunUntil(() => rig.Cork.Motion.Default is WaypointMovementGenerator, 2_000);
        var loop = Assert.IsType<WaypointMovementGenerator>(rig.Cork.Motion.Default);
        Assert.Equal((int)CorkGizeltonAI.CaravanPointAfterSouthEscort - 1, loop.CurrentIndex);
    }

    [Fact]
    public void Cork_AKodoDiesOnTheEscort_BothQuestsFailForTheGroup_AndTheCaravanDisappears()
    {
        using var rig = new Rig();
        CorkGizeltonAI ai = rig.CorkAi;
        ai.OnQuestAccept(rig.Player, CorkGizeltonAI.QuestBodyguardForHire);
        rig.World.RunTick(100);

        rig.Map.Combat.Kill(rig.Player, rig.Kodos[0]);
        rig.RunUntil(() => ai.ScriptDone, 5_000);

        Assert.True(ai.StatusFailed);
        Assert.Equal([CorkGizeltonAI.QuestBodyguardForHire, CorkGizeltonAI.QuestGizeltonCaravan], rig.Quest.Failed.Select(f => f.QuestId));
        Assert.False(rig.Cork.IsAlive);
        Assert.False(rig.Rigger.IsAlive);
        Assert.False(rig.Kodos[1].IsAlive);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Empty(rig.Quest.Completed);
    }

    [Fact]
    public void Cork_ADeathOutsideAnEscortFailsNothing()
    {
        using var rig = new Rig();
        CorkGizeltonAI ai = rig.CorkAi;
        rig.Map.Combat.Kill(rig.Player, rig.Kodos[0]);
        for (int i = 0; i < 20; i++)
        {
            rig.World.RunTick(100);
        }

        Assert.False(ai.StatusFailed);
        Assert.Empty(rig.Quest.Failed);
        Assert.True(rig.Cork.IsAlive);
    }
}
