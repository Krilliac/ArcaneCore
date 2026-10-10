using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.SpawnGroups.ClassicDbSpawnGroupRows;

namespace ArcaneCore.Game.Tests.SpawnGroups;

/// <summary>
/// Spawn group formations (cmangos FormationData, Maps/SpawnGroup.cpp:736-1290) over classic-db z2815 rows: group 2 "Kargath Expeditionary
/// Force" (formation type 4 fanned out behind, spread 4, <c>waypoint_path</c> 6883 MovementType 2; slots 0 6883, 1 6886, 2 6885, 3 6877,
/// 4 6880). Its spawns and the path's first seven points are the dump's, shifted by (+6852.72, +2249.63) into one grid near the test map's origin;
/// the dump's point 1 waits 225 s, and point 7 is given a 60 s wait here so the leader stands still at the far end too.
/// </summary>
public sealed class FormationTests
{
    private const float ShiftX = 6852.72f;
    private const float ShiftY = 2249.63f;
    private const float Z = 83.5f;

    private static readonly (uint Point, float X, float Y, float O, uint WaitMs)[] PathPoints =
    [
        (1, -6692.72f, -2159.63f, 4.03171f, 225_000), (2, -6698.09f, -2166.71f, 100f, 0), (3, -6724.88f, -2177.67f, 100f, 0),
        (4, -6754.51f, -2188.96f, 100f, 0), (5, -6789.04f, -2200.47f, 100f, 0), (6, -6836.76f, -2216.8f, 100f, 0), (7, -6842.6f, -2224.08f, 100f, 60_000),
    ];

    private static readonly (uint Guid, uint Entry, float X, float Y, float O)[] Spawns =
    [
        (6877, 9085, -6687.94f, -2159.12f, 3.9968f), (6880, 9083, -6690.19f, -2156.94f, 4.01426f), (6883, 9086, -6692.72f, -2159.63f, 4.03171f),
        (6885, 9082, -6686.64f, -2162.67f, 3.80482f), (6886, 9084, -6689.95f, -2161.96f, 3.9968f),
    ];

    private static (WorldRuntime World, CreatureMapSystem System) Start()
    {
        var content = new CreatureContent(
            [.. Spawns.Select(s => Template(s.Entry))],
            Spawns.Select(s => Spawn(s.Guid, s.Entry, s.X + ShiftX, s.Y + ShiftY, Z, respawnSeconds: 300) with { Orientation = s.O }),
            [], [], [],
            entryWaypoints: PathPoints.Select(p => (CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | 6883u,
                new CreatureWaypoint(p.Point, p.X + ShiftX, p.Y + ShiftY, Z, p.O, p.WaitMs))))
        {
            SpawnGroups = new SpawnGroupCatalog([KargathExpeditionaryForce with { Flags = SpawnGroupFlags.None }]),
        };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        AddPlayer(world, 1, 300, 300); // in their grid, out of their sight
        Run(world, 200);
        return (world, system);
    }

    private static Creature Member(CreatureMapSystem system, uint guid) => system.Creatures.Single(c => c.Spawn?.Guid == guid);

    /// <summary>The point a follower's slot is at around the standing leader (the leader's facing plus the slot angle).</summary>
    private static (float X, float Y) SlotPoint(Creature leader, FormationSlot slot)
        => (leader.X + (slot.Distance * MathF.Cos(leader.Orientation + slot.Angle)), leader.Y + (slot.Distance * MathF.Sin(leader.Orientation + slot.Angle)));

    [Fact]
    public void FannedOutBehind_TheFollowersSlots_AreAHalfCircleBehindTheLeader()
    {
        // cmangos FixSlotsPositions SPAWN_GROUP_FORMATION_TYPE_FANNED_OUT_BEHIND: π/2 + (π / followers) * (n - 1), at the spread.
        var formation = new FormationState(2, KargathExpeditionaryForce.Formation!, KargathExpeditionaryForce.Members);
        object leader = new();
        formation.MasterSlot!.Owner = leader;
        formation.FixSlotsPositions(1f, new Random(1));
        FormationSlot[] followers = [.. formation.Slots.Where(s => s.SlotId != 0)];
        Assert.Equal([1, 2, 3, 4], followers.Select(s => s.SlotId));
        Assert.All(followers, s => Assert.Equal(4f, s.Distance));
        Assert.Equal([MathF.PI / 2, (MathF.PI / 2) + (MathF.PI / 4), MathF.PI, (MathF.PI / 2) + (3 * MathF.PI / 4)], followers.Select(s => s.Angle));
    }

    [Theory]
    [InlineData(FormationShape.SingleFile, 3, 3f, MathF.PI, 9f)]
    [InlineData(FormationShape.SideBySide, 3, 2f, MathF.PI / 2, 4f)]
    [InlineData(FormationShape.SideBySide, 4, 2f, 3 * MathF.PI / 2, 4f)]
    [InlineData(FormationShape.LikeGeese, 1, 3f, 3 * MathF.PI / 4, 3f)]
    [InlineData(FormationShape.LikeGeese, 2, 3f, 5 * MathF.PI / 4, 3f)]
    [InlineData(FormationShape.CircleTheLeader, 3, 6f, MathF.PI, 6f)]
    [InlineData(FormationShape.FannedOutInFront, 1, 5f, 3 * MathF.PI / 2, 5f)]
    public void EveryShape_PlacesFollowersAsCmangos(FormationShape shape, int follower, float spread, float angle, float distance)
    {
        (float a, float d) = FormationState.Place(shape, spread, follower, followers: 4, modelWidth: 1f, new Random(1));
        Assert.Equal(angle, a, 4);
        Assert.Equal(distance, d, 4);
    }

    [Fact]
    public void Kargath_TheLeaderWalksTheFormationsPath_AndTheFollowersKeepTheirSlots()
    {
        (WorldRuntime w, CreatureMapSystem system) = Start();
        using WorldRuntime world = w;
        Creature leader = Member(system, 6883);
        Assert.Same(leader, system.FormationLeader(2));
        Assert.Equal(MovementGeneratorType.Waypoint, leader.Motion.DefaultType); // its spawn row says MovementType 0: the formation moves it
        FormationState formation = system.FormationOf(2)!;

        Run(world, 20_000, 100); // at point 1, waiting its 225 s
        Assert.False(leader.IsMoving);
        AssertInSlots(system, leader, formation, 1.5f);

        // After the 225 s wait it walks to point 7 (about 160 yd) and waits there 60 s: the followers keep up all along.
        float farthest = 0;
        for (int second = 20; second < 320; second++)
        {
            Run(world, 1000, 200);
            foreach (FormationSlot slot in formation.Slots.Where(s => s.SlotId != 0))
            {
                farthest = Math.Max(farthest, Distance2D((Creature)slot.Owner!, leader));
            }
        }

        Assert.True(Distance2D(leader, PathPoints[6].X + ShiftX, PathPoints[6].Y + ShiftY) < 1f, $"the leader is at {leader.X},{leader.Y}");
        Assert.False(leader.IsMoving);
        AssertInSlots(system, leader, formation, 1.5f);
        Assert.True(farthest < 4f + 10f, $"a follower fell {farthest} yd behind");
    }

    [Fact]
    public void Kargath_TheLeaderDies_TheFirstLivingSlotLeadsOnFromWhereHeWas()
    {
        (WorldRuntime w, CreatureMapSystem system) = Start();
        using WorldRuntime world = w;
        Creature leader = Member(system, 6883);
        Run(world, 240_000, 200); // off point 1 and walking
        system.KillCreature(leader);
        Run(world, 1000);

        Creature next = Member(system, 6886); // slot 1
        Assert.Same(next, system.FormationLeader(2));
        Assert.Equal(MovementGeneratorType.Waypoint, next.Motion.DefaultType);
        Assert.Equal(MovementGeneratorType.Formation, Member(system, 6885).Motion.DefaultType);
        Run(world, 80_000, 200); // about 100 yd of path left, then the 60 s wait at point 7
        Assert.True(Distance2D(next, PathPoints[6].X + ShiftX, PathPoints[6].Y + ShiftY) < 1f, $"the new leader is at {next.X},{next.Y}"); // waiting at point 7
    }

    [Fact]
    public void Kargath_AFollowerSwitchesVictims_EvadeStillReturnsItToWhereTheFightTookIt()
    {
        // cmangos FormationMovementGenerator::Interrupt saves the reset point only while UNIT_STAT_FOLLOW_MOVE is set (first interrupt).
        (WorldRuntime w, CreatureMapSystem system) = Start();
        using WorldRuntime world = w;
        Creature follower = Member(system, 6886);
        (Player a, _) = AddPlayer(world, 2, follower.X + 5, follower.Y);
        (Player b, _) = AddPlayer(world, 3, follower.X - 5, follower.Y);
        (float startX, float startY) = (follower.X, follower.Y);

        follower.Motion.MoveChase(a);                          // the fight takes it here
        system.StopMoving(follower);
        follower.SetPosition(startX + 20, startY + 20, Z, 0f); // it chased somewhere else
        Assert.True(Distance2D(follower, startX, startY) > 20f);
        follower.Motion.MoveChase(b);                          // victim switch

        CreatureHome? reset = follower.Motion.Default.GetResetPosition(follower);
        Assert.NotNull(reset);
        Assert.True(MathF.Abs(reset!.Value.X - startX) < 0.1f && MathF.Abs(reset.Value.Y - startY) < 0.1f,
            $"evade would send it to {reset.Value.X},{reset.Value.Y}, not where the fight took it ({startX},{startY})");
    }

    private static void AssertInSlots(CreatureMapSystem system, Creature leader, FormationState formation, float leeway)
    {
        foreach (FormationSlot slot in formation.Slots.Where(s => s.SlotId != 0))
        {
            var member = (Creature)slot.Owner!;
            (float x, float y) = SlotPoint(leader, slot);
            Assert.True(Distance2D(member, x, y) <= leeway, $"slot {slot.SlotId} ({member.Spawn!.Guid}) is {Distance2D(member, x, y)} yd from its place");
        }
    }
}
