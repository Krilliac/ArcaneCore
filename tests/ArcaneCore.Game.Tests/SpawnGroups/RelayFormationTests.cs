using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.SpawnGroups;

/// <summary>
/// dbscript command 51 SCRIPT_COMMAND_SPAWN_GROUP, the formation subcommands (cmangos ScriptMgr.cpp; SpawnGroup.cpp SetFormationData,
/// FormationData::Reset / Disband / SetMovementInfo). The rows are classic-db z2815's Gizelton caravan: spawn group 19019 "Desolace - Gizelton
/// Caravan" (no spawn_group_formation row; slots 0 Cork Gizelton 28714, 1 Rigger Gizelton 28728, 2 and 3 the Kolkar guards 27290 and 27289),
/// shifted by (+800, -1400) into one grid of the test map, and relay 1162501's step at 615 s: 51, datalong 150, datalong2 19019, dataint 1, x 10.
/// </summary>
public sealed class RelayFormationTests
{
    private const uint Relay = 1162501;
    private const uint Group = 19019;
    private const uint Cork = 28714, Rigger = 28728, GuardA = 27290, GuardB = 27289;
    private const float Z = 90.36f;

    private static readonly (uint Guid, uint Entry, float X, float Y, int Slot)[] Caravan =
    [
        (GuardB, 11564, -700.383f, 1522.39f, 3), (GuardA, 11564, -694.285f, 1513.55f, 2), (Cork, 11625, -691.46f, 1520.09f, 0), (Rigger, 11626, -694.267f, 1524.21f, 1),
    ];

    /// <summary>The dump's create step (1162501, 615000, 1, 51, 150, 19019, ..., dataint 1, ..., x 10).</summary>
    private static RelayScriptStep CreateStep(uint group = Group, int shape = 1, float spread = 10f, uint relay = Relay)
        => new(relay, 0, 1, 51, 150, group, 0, 0, 0, 0, shape, 0, 0, 0, 0, spread, 0, 0, 0, 0, 0);

    private static RelayScriptStep Step(uint relay, uint sub, uint data1 = 0, float x = 0)
        => new(relay, 0, 0, 51, sub, data1, 0, 0, 0, 0, 0, 0, 0, 0, 0, x, 0, 0, 0, 0, 0);

    private static (WorldRuntime World, CreatureMapSystem System) Start(params RelayScriptStep[] steps)
    {
        var ai = new CreatureAiContent([], [], new BroadcastTextCatalog([])) { RelayScripts = new RelayScriptCatalog(steps, []) };
        var content = new CreatureContent(
            [Template(11564), Template(11625), Template(11626)],
            Caravan.Select(c => Spawn(c.Guid, c.Entry, c.X + 800f, c.Y - 1400f, Z, respawnSeconds: 600)),
            [], [], [], ai)
        {
            SpawnGroups = new SpawnGroupCatalog([new SpawnGroupDefinition
            {
                Id = Group, Name = "Desolace - Gizelton Caravan", Type = SpawnGroupType.Creature, MaxCount = 4,
                Flags = SpawnGroupFlags.AggroTogether | SpawnGroupFlags.RespawnTogether,
                Members = [.. Caravan.Select(c => new SpawnGroupMember(c.Guid, c.Slot, 0))],
            }]),
        };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        AddPlayer(world, 1, 300, 300);
        Run(world, 200);
        return (world, system);
    }

    private static Creature Member(CreatureMapSystem system, uint guid) => system.Creatures.SingleOrDefault(c => c.Spawn?.Guid == guid)
        ?? throw new InvalidOperationException($"{guid} missing; in world: {string.Join(',', system.Creatures.Select(c => c.Spawn?.Guid))}");

    private static void Play(WorldRuntime world, CreatureMapSystem system, uint relay, uint guid = Cork)
    {
        Creature source = Member(system, guid);
        Assert.True(system.StartRelayScript(relay, source, source));
        Run(world, 300);
    }

    [Fact]
    public void Create_CorkGizeltonRelay_MakesADynamicSingleFileFormation_TheFollowersFollowAndCorkKeepsHisMovement()
    {
        (WorldRuntime w, CreatureMapSystem system) = Start(CreateStep());
        using WorldRuntime world = w;
        Assert.Null(system.FormationOf(Group));
        Creature cork = Member(system, Cork);
        ICreatureMovementGenerator corkMovement = cork.Motion.Default;

        Play(world, system, Relay);

        FormationState formation = Assert.IsType<FormationState>(system.FormationOf(Group));
        Assert.True(formation.IsDynamic);
        Assert.Equal(FormationShape.SingleFile, formation.Shape);
        Assert.Equal(10f, formation.Spread);
        Assert.Same(cork, system.FormationLeader(Group));
        Assert.Same(corkMovement, cork.Motion.Default); // TrySetNewMaster: no SetMasterMovement for a dynamic formation
        foreach (uint follower in new[] { Rigger, GuardA, GuardB })
        {
            var follow = Assert.IsType<FormationMovementGenerator>(Member(system, follower).Motion.Default);
            Assert.Same(formation.SlotOf(Member(system, follower)), follow.Slot);
        }

        // Single file at spread 10: slot n stands 10 * n yd straight behind (cmangos FixSlotsPositions).
        Assert.Equal([10f, 20f, 30f], formation.Slots.Where(s => s.SlotId != 0).Select(s => s.Distance));
        Assert.All(formation.Slots.Where(s => s.SlotId != 0), s => Assert.Equal(MathF.PI, s.Angle));
    }

    [Fact]
    public void Create_ByTheTargetsOwnGroup_AndASecondCreate_LeavesTheFirstFormation()
    {
        (WorldRuntime w, CreatureMapSystem system) = Start(CreateStep(group: 0), CreateStep(group: 0, shape: 2, relay: Relay + 1));
        using WorldRuntime world = w;
        Play(world, system, Relay, Rigger); // datalong2 0: the group of the target (Rigger is in 19019)
        FormationState first = Assert.IsType<FormationState>(system.FormationOf(Group));
        Assert.Same(Member(system, Cork), system.FormationLeader(Group)); // slot 0's owner leads, whoever ran the script

        Play(world, system, Relay + 1); // "Target group have already a formation!": the user has to remove it first
        Assert.Same(first, system.FormationOf(Group));
        Assert.Equal(FormationShape.SingleFile, first.Shape);
    }

    [Theory]
    [InlineData(7, 10f)] // SPAWN_GROUP_FORMATION_TYPE_COUNT: cmangos drops the step at load
    [InlineData(-1, 10f)]
    public void Create_WithAnInvalidShape_DoesNothing(int shape, float spread)
    {
        (WorldRuntime w, CreatureMapSystem system) = Start(CreateStep(shape: shape, spread: spread));
        using WorldRuntime world = w;
        Play(world, system, Relay);
        Assert.Null(system.FormationOf(Group));
    }

    [Fact]
    public void SwitchShape_SetSpread_AndRemove_ActOnTheTargetsFormation()
    {
        (WorldRuntime w, CreatureMapSystem system) = Start(
            CreateStep(), Step(Relay + 1, 100, data1: 6), Step(Relay + 2, 101, x: 4f), Step(Relay + 3, 101, x: 20f), Step(Relay + 4, 102, data1: 1),
            Step(Relay + 5, 151, data1: Group));
        using WorldRuntime world = w;
        Play(world, system, Relay);
        FormationState formation = system.FormationOf(Group)!;

        Play(world, system, Relay + 1);
        Assert.Equal(FormationShape.CircleTheLeader, formation.Shape);
        Play(world, system, Relay + 2);
        Assert.Equal(4f, formation.Spread);
        Assert.All(formation.Slots.Where(s => s.SlotId != 0), s => Assert.Equal(4f, s.Distance)); // circle: every follower at the spread
        Play(world, system, Relay + 3); // over 15 yd: dropped
        Assert.Equal(4f, formation.Spread);
        Play(world, system, Relay + 4);
        Assert.Equal(1u, formation.Options);

        ICreatureMovementGenerator corkMovement = Member(system, Cork).Motion.Default;
        Play(world, system, Relay + 5);
        Assert.Null(system.FormationOf(Group));
        Assert.Same(corkMovement, Member(system, Cork).Motion.Default); // a dynamic formation's leader keeps its movement
        Assert.All(new[] { Rigger, GuardA, GuardB }, g => Assert.IsNotType<FormationMovementGenerator>(Member(system, g).Motion.Default));
    }

    [Fact]
    public void Remove_OfAGroupWithoutAFormation_OrNotOnTheMap_DoesNothing()
    {
        (WorldRuntime w, CreatureMapSystem system) = Start(Step(Relay, 151, data1: Group), Step(Relay + 1, 151, data1: 4242), Step(Relay + 2, 100, data1: 2));
        using WorldRuntime world = w;
        Play(world, system, Relay);
        Play(world, system, Relay + 1);
        Play(world, system, Relay + 2); // switch shape: Cork is in no formation
        Assert.Null(system.FormationOf(Group));
        Assert.All(system.Creatures, c => Assert.IsNotType<FormationMovementGenerator>(c.Motion.Default));
    }

    [Fact]
    public void Movement_OnTheLeaderSetsTheFormationsMovement_AndOnAFollowerIsSkipped()
    {
        // The caravan relay's next step (616 s): MOVEMENT 2 (waypoint) path 19019 from waypoint_path (datalong3 0x2).
        RelayScriptStep move(uint relay) => new(relay, 0, 0, 20, 2, Group, 2, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        (WorldRuntime w, CreatureMapSystem system) = Start(CreateStep(), move(Relay + 1), move(Relay + 2));
        using WorldRuntime world = w;
        Play(world, system, Relay);
        FormationState formation = system.FormationOf(Group)!;

        Play(world, system, Relay + 2, Rigger);
        Assert.IsType<FormationMovementGenerator>(Member(system, Rigger).Motion.Default); // "call for creature in formation, skipping"
        Assert.Equal(0, formation.MovementType);

        Play(world, system, Relay + 1, Cork);
        Assert.Equal(2, formation.MovementType); // FormationData::SetMovementInfo
        Assert.Equal(Group, formation.PathId);
    }
}
