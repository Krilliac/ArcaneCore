using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The scripted escort (vmangos npc_escortAI, AI/ScriptedEscortAI.cpp) and the per-map script AI of an entry (vmangos ScriptName before the
/// AIName, AI/CreatureAISelector.cpp:37-50), on a real creature system with simulated time: the path is walked point by point after the first
/// delay with each point's wait, a pause holds it, a fight breaks it off and the escort runs back to where the fight began before it goes on,
/// <see cref="EscortAI.Stop"/> ends it, and at the end of the path the creature disappears.
/// </summary>
public sealed class EscortAiTests
{
    private const uint EscortEntry = 47001;
    private const uint OtherEntry = 47002;
    private const float Z = 83.5f;

    private sealed class TestEscortAI(Creature creature) : EscortAI(creature)
    {
        public List<uint> Reached { get; } = [];

        public int Resets { get; private set; }

        public Action<TestEscortAI, uint>? OnReached { get; set; }

        protected override void WaypointReached(uint pointId)
        {
            Reached.Add(pointId);
            OnReached?.Invoke(this, pointId);
        }

        protected override void Reset() => Resets++;
    }

    private sealed record Rig(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, Creature Escort) : IDisposable
    {
        public TestEscortAI Ai => (TestEscortAI)Escort.AI!;

        public void Dispose() => World.Dispose();
    }

    private static readonly CreatureWaypoint[] s_path =
    [
        new(1, 10, 0, Z, 0, 0),
        new(2, 15, 0, Z, 0, 1000),
        new(3, 20, 0, Z, 0, 0),
    ];

    private static Rig Setup(bool withPath = true, ICreatureHostility? hostility = null,
        IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? scriptWaypoints = null, IScriptQuestEvents? quests = null)
    {
        // With a hostile world the other creature stays out (it would fight the escort) and the player starts out of aggro reach.
        bool hostile = hostility is not null;
        CreatureContent content = new(
            [Template(EscortEntry, b => { b.Name = "Escort"; b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver); }), Template(OtherEntry)],
            hostile ? [Spawn(1, EscortEntry, 5, 0, Z)] : [Spawn(1, EscortEntry, 5, 0, Z), Spawn(2, OtherEntry, 0, 5, Z)],
            [], [], [], entryWaypoints: withPath ? s_path.Select(p => (EscortEntry, EscortAI.EscortPathId, p)) : [],
            scriptWaypoints: scriptWaypoints);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = hostility ?? new NeverHostile(), ScriptQuests = quests });
        (Player player, _) = AddPlayer(world, 1, hostile ? 150 : 0, 0);
        system.RegisterEntryAi(EscortEntry, c => new TestEscortAI(c));
        Creature escort = Assert.Single(system.Creatures, c => c.Entry == EscortEntry);
        return new Rig(world, map, system, player, escort);
    }

    [Fact]
    public void AnEntryScript_IsTheAiOfTheMapsCreaturesOfThatEntry_UntilItIsUnregistered()
    {
        using Rig rig = Setup();
        Creature other = Assert.Single(rig.System.Creatures, c => c.Entry == OtherEntry);

        Assert.IsType<TestEscortAI>(rig.Escort.AI);
        Assert.IsNotType<TestEscortAI>(other.AI);
        Assert.Equal(1, rig.Ai.Resets); // the spawn resets it once (vmangos ScriptedAI constructor)

        rig.System.UnregisterEntryAi(EscortEntry);
        Assert.IsNotType<TestEscortAI>(rig.Escort.AI);
    }

    [Fact]
    public void Start_WalksThePathPointByPoint_AfterTheFirstDelay_AndDisappearsAtTheEnd()
    {
        using Rig rig = Setup();

        Assert.True(rig.Ai.Start(run: true));
        Assert.True(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(0u, rig.Escort.NpcFlags); // the NPC flags go with the start

        Run(rig.World, 2000);
        Assert.False(rig.Escort.IsMoving); // the first point waits 2.5 s
        Run(rig.World, 600);
        Assert.True(rig.Escort.IsMoving);

        RunUntil(rig, () => rig.Ai.Reached.Count == 2);
        Assert.Equal([1u, 2u], rig.Ai.Reached);
        Assert.Equal(15f, rig.Escort.X, 0.5f);
        Run(rig.World, 900);
        Assert.False(rig.Escort.IsMoving); // point 2 waits a second
        RunUntil(rig, () => rig.Ai.Reached.Count == 3);
        Assert.Equal([1u, 2u, 3u], rig.Ai.Reached);

        RunUntil(rig, () => !rig.Escort.IsAlive); // the end of the line: the creature disappears
        Assert.Equal(CreatureDeathState.Dead, rig.Escort.DeathState);
    }

    [Fact]
    public void Start_WithoutPoints_DoesNotEscort()
    {
        using Rig rig = Setup(withPath: false);

        Assert.False(rig.Ai.Start(run: true));
        Assert.False(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.NotEqual(0u, rig.Escort.NpcFlags);
        Run(rig.World, 5000);
        Assert.False(rig.Escort.IsMoving);
    }

    [Fact]
    public void Start_PrefersScriptWaypointOverTheCreaturesDefaultPatrol()
    {
        using Rig rig = Setup(scriptWaypoints:
            [(EscortEntry, 0, new CreatureWaypoint(1, 30, 0, Z, 0, 0))]);
        Assert.True(rig.Ai.Start());
        Assert.Equal(1, rig.Ai.WaypointCount);
        RunUntil(rig, () => rig.Ai.Reached.Count == 1);
        Assert.Equal(30f, rig.Escort.X, 0.5f);
    }

    [Fact]
    public void EscortQuest_FailsForTheGroupWhenThePlayerLeavesRange_AndTheEscortDisappears()
    {
        var quests = new EscortQuests();
        using Rig rig = Setup(quests: quests);
        Assert.True(rig.Ai.Start(player: rig.Player, questId: 1393)); // Galen's Escape (quest id only; the path is synthetic)

        rig.Player.Relocate(200, 0, Z, 0, 0);
        Run(rig.World, 1_200);

        Assert.Equal([(rig.Player, 1393u)], quests.GroupFailed);
        Assert.False(rig.Escort.IsAlive); // ResetEscort: the escort disappears (no instant respawn asked)
        Assert.NotEqual(0u, rig.Escort.NpcFlags & (uint)NpcFlags.QuestGiver);

        Run(rig.World, 2_000);
        Assert.Single(quests.GroupFailed); // the quest fails once: the dead escort no longer checks
    }

    [Fact]
    public void EscortQuest_FailsForTheGroupWhenTheEscortDies_Once()
    {
        var quests = new EscortQuests();
        using Rig rig = Setup(quests: quests);
        Assert.True(rig.Ai.Start(player: rig.Player, questId: 1393));

        rig.Ai.OnDeath(null);
        rig.Ai.OnDeath(null);

        Assert.Equal([(rig.Player, 1393u)], quests.GroupFailed);
    }

    [Fact]
    public void AnEscortWithoutAPlayer_NeverFailsAQuest()
    {
        var quests = new EscortQuests();
        using Rig rig = Setup(quests: quests);
        Assert.True(rig.Ai.Start());

        rig.Player.Relocate(200, 0, Z, 0, 0);
        Run(rig.World, 1_200);
        rig.Ai.OnDeath(null);

        Assert.Empty(quests.GroupFailed);
        Assert.True(rig.Escort.IsAlive);
    }

    [Fact]
    public void EscortQuest_StaysActiveWhileAnOnlineGroupMemberIsNear()
    {
        var quests = new EscortQuests();
        using Rig rig = Setup(quests: quests);
        (Player member, _) = AddPlayer(rig.World, 2, 0, 1);
        quests.Group = [rig.Player, member];
        Assert.True(rig.Ai.Start(player: rig.Player, questId: 1393));

        rig.Player.Relocate(200, 0, Z, 0, 0);
        Run(rig.World, 1_200);
        Assert.Empty(quests.GroupFailed);
        Assert.True(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));

        member.Relocate(200, 0, Z, 0, 0);
        Run(rig.World, 1_100);
        Assert.Equal([(rig.Player, 1393u)], quests.GroupFailed);
    }

    /// <summary>The quest log the escort reaches: records group failures and answers the player's group.</summary>
    private sealed class EscortQuests : IScriptQuestEvents
    {
        public List<(Player Player, uint QuestId)> GroupFailed { get; } = [];

        public IReadOnlyList<Player> Group { get; set; } = [];

        public void AreaExploredOrEventHappens(Player player, uint questId) { }
        public void FailQuest(Player player, uint questId) { }
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) { }
        public void GroupEventFailHappens(Player player, uint questId) => GroupFailed.Add((player, questId));
        public IReadOnlyList<Player> GroupMembersOf(Player player) => Group;
    }

    [Fact]
    public void APause_HoldsTheEscortAtItsPoint_UntilItIsResumed()
    {
        using Rig rig = Setup();
        rig.Ai.OnReached = (ai, point) =>
        {
            if (point == 1)
            {
                ai.SetEscortPaused(true);
            }
        };

        Assert.True(rig.Ai.Start(run: true));
        RunUntil(rig, () => rig.Ai.Reached.Count == 1);
        Run(rig.World, 10_000);
        Assert.Equal([1u], rig.Ai.Reached);
        Assert.Equal(10f, rig.Escort.X, 0.5f);

        rig.Ai.SetEscortPaused(false);
        RunUntil(rig, () => rig.Ai.Reached.Count == 2);
        Assert.Equal(2u, rig.Ai.Reached[1]);
    }

    [Fact]
    public void Stop_EndsTheEscortWhereItStands()
    {
        using Rig rig = Setup();
        rig.Ai.OnReached = (ai, point) =>
        {
            if (point == 1)
            {
                ai.Stop();
            }
        };

        Assert.True(rig.Ai.Start(run: true));
        RunUntil(rig, () => rig.Ai.Reached.Count == 1);
        Run(rig.World, 10_000);
        Assert.False(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal([1u], rig.Ai.Reached);
        Assert.True(rig.Escort.IsAlive);
        Assert.Equal(10f, rig.Escort.X, 0.5f);
    }

    [Fact]
    public void AFight_BreaksTheEscortOff_ThenItRunsBackToWhereTheFightBegan_AndGoesOn()
    {
        using Rig rig = Setup(hostility: new AlwaysHostile());
        Assert.True(rig.Ai.Start(run: true));
        RunUntil(rig, () => rig.Ai.Reached.Count == 1);

        // The fight starts 2 yd past point 1, towards point 2.
        RunUntil(rig, () => rig.Escort.X >= 12f);
        float startX = rig.Escort.X;
        rig.Player.Relocate(rig.Escort.X + 1, 0, Z, 0, 0);
        rig.Map.Combat.DealDamage(rig.Player, rig.Escort, 1, direct: false);
        Assert.True(rig.Escort.Combat.IsInCombat);
        Assert.Equal(startX, rig.Ai.CombatStartPosition.X, 0.5f);

        rig.Player.Relocate(150, 0, Z, 0, 0);
        rig.Escort.Relocate(startX - 4f, 3f, Z, 0, 0); // the fight moved it away
        int resets = rig.Ai.Resets;
        rig.System.EnterEvadeMode(rig.Escort);

        Assert.Equal(resets + 1, rig.Ai.Resets);
        Assert.True(rig.Ai.HasEscortState(EscortAI.EscortState.Returning));
        Assert.False(rig.Escort.IsInEvadeMode); // not sent home
        Assert.Equal(MovementGeneratorType.Point, rig.Escort.Motion.CurrentType);

        RunUntil(rig, () => !rig.Ai.HasEscortState(EscortAI.EscortState.Returning));
        Assert.Equal(startX, rig.Escort.X, 0.5f);
        Assert.Equal(0f, rig.Escort.Y, 0.5f);

        RunUntil(rig, () => rig.Ai.Reached.Count == 3);
        Assert.Equal([1u, 2u, 3u], rig.Ai.Reached);
    }

    /// <summary>
    /// An escort's evade is an evade: cMaNGOS npc_escortAI has no EnterEvadeMode of its own, so it takes CreatureAI::EnterEvadeMode →
    /// UnitAI::EnterEvadeMode, which ends in Unit::TriggerEvadeEvents (mangos-classic AI/BaseAI/UnitAI.cpp:113-130) whether or not the
    /// creature walks home. <see cref="CreatureMapSystem.Evaded"/> is raised once although the escort runs back to the combat start instead.
    /// </summary>
    [Fact]
    public void AnEscortEvade_RunningBackToTheCombatStart_RaisesEvadedOnce()
    {
        using Rig rig = Setup(hostility: new AlwaysHostile());
        Assert.True(rig.Ai.Start(run: true));
        RunUntil(rig, () => rig.Ai.Reached.Count == 1);
        rig.Player.Relocate(rig.Escort.X + 1, 0, Z, 0, 0);
        rig.Map.Combat.DealDamage(rig.Player, rig.Escort, 1, direct: false);
        Assert.True(rig.Escort.Combat.IsInCombat);
        rig.Player.Relocate(150, 0, Z, 0, 0);
        var seen = new List<Creature>();
        rig.System.Evaded += seen.Add;

        rig.System.EnterEvadeMode(rig.Escort);

        Assert.True(rig.Ai.HasEscortState(EscortAI.EscortState.Returning)); // the escort's own evade, not the run home
        Assert.False(rig.Escort.IsInEvadeMode);
        Assert.Same(rig.Escort, Assert.Single(seen));
    }

    /// <summary>A following creature's evade (it walks back to where the fight began, FollowerAI::EnterEvadeMode) raises Evaded once.</summary>
    [Fact]
    public void AFollowerEvade_WalkingBackToTheCombatStart_RaisesEvadedOnce()
    {
        using Rig rig = Setup(withPath: false);
        rig.System.RegisterEntryAi(OtherEntry, c => new TestFollowerAI(c));
        Creature follower = Assert.Single(rig.System.Creatures, c => c.Entry == OtherEntry);
        var ai = Assert.IsType<TestFollowerAI>(follower.AI);
        ai.StartFollow(rig.Player);
        Assert.True(ai.HasFollowState(FollowerAI.FollowState.InProgress));
        var seen = new List<Creature>();
        rig.System.Evaded += seen.Add;
        int resets = ai.Resets;

        rig.System.EnterEvadeMode(follower);

        Assert.False(follower.IsInEvadeMode); // no run home
        Assert.Equal(resets + 1, ai.Resets);
        Assert.Same(follower, Assert.Single(seen));
    }

    /// <summary>The guard: an ordinary evade (the creature runs home) still raises Evaded exactly once, not twice.</summary>
    [Fact]
    public void AnOrdinaryEvade_RunningHome_RaisesEvadedExactlyOnce()
    {
        using Rig rig = Setup(withPath: false);
        Creature other = Assert.Single(rig.System.Creatures, c => c.Entry == OtherEntry);
        rig.Escort.Relocate(8, 0, Z, 0, 0); // the escort script without an escort: the ordinary evade, home to its spawn point
        var seen = new List<Creature>();
        rig.System.Evaded += seen.Add;

        rig.System.EnterEvadeMode(other);
        rig.System.EnterEvadeMode(rig.Escort);

        Assert.True(other.IsInEvadeMode);
        Assert.True(rig.Escort.IsInEvadeMode);
        Assert.Equal(MovementGeneratorType.Home, rig.Escort.Motion.CurrentType);
        Assert.Equal([other, rig.Escort], seen);
    }

    private sealed class TestFollowerAI(Creature creature) : FollowerAI(creature)
    {
        public int Resets { get; private set; }

        protected override void Reset() => Resets++;
    }

    private static void RunUntil(Rig rig, Func<bool> condition, uint limitMs = 60_000)
    {
        for (uint done = 0; done < limitMs && !condition(); done += 100)
        {
            rig.World.RunTick(100);
        }

        Assert.True(condition(), $"the condition did not hold within the simulated time limit: x {rig.Escort.X} y {rig.Escort.Y} moving {rig.Escort.IsMoving} state {rig.Ai.State} combat {rig.Escort.Combat.IsInCombat} motion {rig.Escort.Motion.CurrentType} reached {string.Join(",", rig.Ai.Reached)} alive {rig.Escort.IsAlive}");
    }

    private sealed class NeverHostile : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => false;

        public bool CanAssist(Creature helper, Creature caller) => false;
    }
}
