using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The evade events of an escort, a follower and an ordinary creature when its AI takes the evade movement over.
/// <list type="bullet">
/// <item>An escort raises them as any creature. cMaNGOS npc_escortAI has no EnterEvadeMode of its own (mangos-classic
/// AI/ScriptDevAI/base/escort_ai.h:20). It takes CreatureAI::EnterEvadeMode (AI/BaseAI/CreatureAI.cpp:66-71), then
/// UnitAI::EnterEvadeMode (AI/BaseAI/UnitAI.cpp:113-130), which ends in Unit::TriggerEvadeEvents (Entities/Unit.cpp:584-598). That
/// raises LINKING_EVENT_EVADE (:591-592) and CREATURE_GROUP_EVENT_EVADE (:594-595; Maps/SpawnGroup.cpp:562-573 evades the other
/// members of an EVADE_TOGETHER group).</item>
/// <item>A follower does not raise them: FollowerAI::EnterEvadeMode (AI/ScriptDevAI/base/follower_ai.cpp:105-131) replaces the
/// evade and never calls TriggerEvadeEvents.</item>
/// </list>
/// </summary>
public sealed class EscortEvadeEventsTests
{
    private const uint MasterEntry = 47101, SlaveEntry = 47102;
    private const uint MasterGuid = 1, SlaveGuid = 2;
    private const uint LinkEvadeOnEvade = 0x4000; // cmangos FLAG_EVADE_ON_EVADE (CreatureLinkingMgr.h)
    private const float Z = 83.5f;

    private static readonly CreatureWaypoint[] s_path =
    [
        new(1, 10, 0, Z, 0, 0),
        new(2, 15, 0, Z, 0, 1000),
        new(3, 20, 0, Z, 0, 0),
    ];

    private enum MasterKind
    {
        Ordinary,
        Escort,
        FormationEscort,
        Follower,
    }

    public enum Relation
    {
        Linked,
        SpawnGroup,
    }

    private sealed record Rig(WorldRuntime World, CreatureMapSystem System, Player Player, Creature Master, Creature Slave) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Rig Setup(MasterKind kind, Relation relation)
    {
        CreatureContent content = new(
            [Template(MasterEntry), Template(SlaveEntry)],
            [Spawn(MasterGuid, MasterEntry, 5, 0, Z), Spawn(SlaveGuid, SlaveEntry, 0, 5, Z)],
            [], [], [],
            entryWaypoints: s_path.Select(p => (MasterEntry, EscortAI.EscortPathId, p)),
            links: relation == Relation.Linked ? [new CreatureLink(SlaveGuid, MasterGuid, LinkEvadeOnEvade)] : null)
        {
            SpawnGroups = relation == Relation.SpawnGroup
                ? new SpawnGroupCatalog([new SpawnGroupDefinition
                {
                    Id = 1,
                    Type = SpawnGroupType.Creature,
                    Flags = SpawnGroupFlags.EvadeTogether,
                    Members = [new(MasterGuid, 0, 0), new(SlaveGuid, 1, 0)],
                }])
                : SpawnGroupCatalog.Empty,
        };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new NeverHostile() });
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        switch (kind)
        {
            case MasterKind.Escort:
                system.RegisterEntryAi(MasterEntry, c => new TestEscortAI(c));
                break;
            case MasterKind.FormationEscort:
                system.RegisterEntryAi(MasterEntry, c => new FormationEscortAI(c));
                break;
            case MasterKind.Follower:
                system.RegisterEntryAi(MasterEntry, c => new TestFollowerAI(c));
                break;
        }

        Creature master = Assert.Single(system.Creatures, c => c.Entry == MasterEntry);
        Creature slave = Assert.Single(system.Creatures, c => c.Entry == SlaveEntry);
        switch (master.AI)
        {
            case EscortAI escort:
                Assert.True(escort.Start(run: true));
                Assert.True(escort.HasEscortState(EscortAI.EscortState.Escorting));
                break;
            case FollowerAI follower:
                follower.StartFollow(player);
                Assert.True(follower.HasFollowState(FollowerAI.FollowState.InProgress));
                break;
        }

        Run(world, 200);
        return new Rig(world, system, player, master, slave);
    }

    [Theory]
    [InlineData(Relation.Linked)]
    [InlineData(Relation.SpawnGroup)]
    public void AnEscortEvade_RunningBackToTheCombatStart_EvadesItsLinkedCreatureAndItsGroup(Relation relation)
    {
        using Rig rig = Setup(MasterKind.Escort, relation);
        var seen = new List<Creature>();
        rig.System.Evaded += seen.Add;

        rig.System.EnterEvadeMode(rig.Master);

        Assert.True(((EscortAI)rig.Master.AI!).HasEscortState(EscortAI.EscortState.Returning)); // the escort's own movement
        Assert.False(rig.Master.IsInEvadeMode);
        Assert.True(rig.Slave.IsInEvadeMode); // FLAG_EVADE_ON_EVADE / CREATURE_GROUP_EVADE_TOGETHER: the other one runs home
        Assert.Equal([rig.Master, rig.Slave], seen); // each evades once: the escort is not evaded again by its own group
    }

    /// <summary>The AV riders and soldiers (AvCavalryAI, AvTroopsChiefAI) rejoin their formation in OnEvade without the escort's
    /// return. vmangos AV_NpcEventTroopsAI (scripts/battlegrounds/battleground_alterac.cpp:1335) is a plain npc_escortAI, so its evade
    /// is the escort's.</summary>
    [Theory]
    [InlineData(Relation.Linked)]
    [InlineData(Relation.SpawnGroup)]
    public void AFormationRejoinEvade_OfAnEscortScript_EvadesItsLinkedCreatureAndItsGroup(Relation relation)
    {
        using Rig rig = Setup(MasterKind.FormationEscort, relation);
        var seen = new List<Creature>();
        rig.System.Evaded += seen.Add;

        rig.System.EnterEvadeMode(rig.Master);

        Assert.False(rig.Master.IsInEvadeMode);
        Assert.True(rig.Slave.IsInEvadeMode);
        Assert.Equal([rig.Master, rig.Slave], seen);
    }

    [Theory]
    [InlineData(Relation.Linked)]
    [InlineData(Relation.SpawnGroup)]
    public void AFollowerEvade_WalkingBackToTheCombatStart_LeavesItsLinkedCreatureAndItsGroupAlone(Relation relation)
    {
        using Rig rig = Setup(MasterKind.Follower, relation);
        var seen = new List<Creature>();
        rig.System.Evaded += seen.Add;

        rig.System.EnterEvadeMode(rig.Master);

        Assert.False(rig.Master.IsInEvadeMode);
        Assert.False(rig.Slave.IsInEvadeMode);
        Assert.Equal([rig.Master], seen);
    }

    /// <summary>The guard: an ordinary evade still evades the linked creature or the group once, and nobody twice.</summary>
    [Theory]
    [InlineData(Relation.Linked)]
    [InlineData(Relation.SpawnGroup)]
    public void AnOrdinaryEvade_RunningHome_EvadesItsLinkedCreatureAndItsGroupOnce(Relation relation)
    {
        using Rig rig = Setup(MasterKind.Ordinary, relation);
        var seen = new List<Creature>();
        rig.System.Evaded += seen.Add;

        rig.System.EnterEvadeMode(rig.Master);

        Assert.True(rig.Master.IsInEvadeMode);
        Assert.True(rig.Slave.IsInEvadeMode);
        Assert.Equal([rig.Master, rig.Slave], seen);
    }

    private sealed class TestEscortAI(Creature creature) : EscortAI(creature)
    {
        protected override void WaypointReached(uint pointId)
        {
        }
    }

    /// <summary>The shape of AvCavalryAI / AvTroopsChiefAI.OnEvade with a leader to follow: no escort return, the formation takes it.</summary>
    private sealed class FormationEscortAI(Creature creature) : EscortAI(creature)
    {
        public override void OnEvade()
        {
            Me.IsEvading = false;
            Reset();
        }

        protected override void WaypointReached(uint pointId)
        {
        }
    }

    private sealed class TestFollowerAI(Creature creature) : FollowerAI(creature);

    private sealed class NeverHostile : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => false;

        public bool CanAssist(Creature helper, Creature caller) => false;
    }
}
