using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The waits of a bot-led group that is not a siege raid are bounded: a lagging member is caught up (or the wait waived) after 30 s,
/// the gathering ends after 60 s, an approach point the leader makes no progress toward counts as reached, a quest group that can plan
/// no way gives up as a run does, and a group that cannot get out of an instance is let go after two bring-outs.
/// </summary>
public sealed class PlayerbotGroupBoundedWaitTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint Fireball = 133, LesserHeal = 2050, Smite = 585;

    private static uint NowMs(GroupTestWorld world) => world.World.Host.World.NowMs;

    private static float Flat(Player a, Player b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>No way leads anywhere.</summary>
    private sealed class NoPath : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }

    private static void Block(GroupTestWorld world) => WorldCollision.Of(world.World.Host.World).Install(pathfinder: new NoPath());

    /// <summary>A two-bot group in the Travelling state, its second bot (by leadership) moved <paramref name="yards"/> behind the leader and unable to walk.</summary>
    private static async Task<(PlayerbotGroupCoordinator.BotGroup Group, Guid LeaderId, Guid LaggardId)> TravellingWithALaggardAsync(GroupTestWorld world, float yards)
    {
        var first = await world.AddBotAsync("Lagwar", Human, Warrior, 1, []);
        var second = await world.AddBotAsync("Lagmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Guid leaderId = group.LeaderBotId, laggardId = leaderId == first.Id ? second.Id : first.Id;
        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(leaderId);
            Block(world);
            world.Player(laggardId).Relocate(leader.X, leader.Y - yards, leader.Z, 0, NowMs(world));
            return true;
        });
        return (group, leaderId, laggardId);
    }

    // --- B1: the lag bound --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALeaderWaitingOnALaggard_NamesTheWait_AndTheLaggardIsCaughtUpAfterThirtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        (PlayerbotGroupCoordinator.BotGroup group, Guid leaderId, Guid laggardId) = await TravellingWithALaggardAsync(world, 60f);
        PlayerbotGroupAI leaderAI = world.Coordinator.FindAI(leaderId)!;

        // The first twenty seconds: the leader holds, and says which wait it is.
        var steps = new HashSet<string>();
        await world.RunUntilAsync(20_000, () =>
        {
            steps.Add(leaderAI.Step);
            return false;
        });
        Assert.Contains("lead:hold-members", steps);
        Assert.DoesNotContain(steps, step => step.StartsWith("lead:walk", StringComparison.Ordinal));
        Assert.True(await world.OnWorldAsync(() => Flat(world.Player(leaderId), world.Player(laggardId)) > PlayerbotGroupAI.WaitForMembersYards), "the laggard was caught up before the bound");

        // Past the thirty seconds the laggard is brought to the leader (the follow catch-up teleport) and the wait ends.
        Assert.True(await world.RunUntilAsync(20_000, () => Flat(world.Player(leaderId), world.Player(laggardId)) <= PlayerbotGroupAI.WaitForMembersYards), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
    }

    [Fact]
    public async Task ALeaderWaitingOnALaggardThatCannotBeTeleported_WaivesTheWaitAfterThirtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
            options.Party.TeleportToLeader = false;
        });
        (_, Guid leaderId, Guid laggardId) = await TravellingWithALaggardAsync(world, 60f);
        PlayerbotGroupAI leaderAI = world.Coordinator.FindAI(leaderId)!;

        await world.RunAsync(20_000);
        Assert.Equal("lead:hold-members", leaderAI.Step);

        // The laggard stays where it is; the leader goes on without waiting for it.
        Assert.True(await world.RunUntilAsync(20_000, () => leaderAI.Step != "lead:hold-members"), await world.TraceAsync());
        Assert.True(await world.OnWorldAsync(() => Flat(world.Player(leaderId), world.Player(laggardId)) > PlayerbotGroupAI.WaitForMembersYards));
    }

    // --- B2: the gathering --------------------------------------------------------------------------------------------

    private static async Task<(PlayerbotGroupCoordinator.BotGroup Group, Guid LeaderId, Guid FarId, uint StartMs)> GatheringWithAMissingMemberAsync(GroupTestWorld world)
    {
        var first = await world.AddBotAsync("Gatherwar", Human, Warrior, 1, []);
        var second = await world.AddBotAsync("Gathermage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Guid leaderId = group.LeaderBotId, farId = leaderId == first.Id ? second.Id : first.Id;
        uint start = await world.OnWorldAsync(() =>
        {
            // A member far away that cannot walk to the leader: the gathering would wait for it for the whole step.
            Block(world);
            world.Player(farId).Relocate(-9200f, 300f, 70f, 0f, NowMs(world));
            group.State = PlayerbotGroupState.Gathering;
            group.StateSinceMs = NowMs(world);
            return NowMs(world);
        });
        return (group, leaderId, farId, start);
    }

    [Fact]
    public async Task AGatheringThatWaitsOnAMissingMember_CatchesItUpAfterSixtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        (PlayerbotGroupCoordinator.BotGroup group, Guid leaderId, Guid farId, uint start) = await GatheringWithAMissingMemberAsync(world);

        await world.RunAsync(50_000);
        Assert.Equal(PlayerbotGroupState.Gathering, group.State);

        Assert.True(await world.RunUntilAsync(25_000, () => group.State != PlayerbotGroupState.Gathering), await world.TraceAsync());
        Assert.InRange(unchecked(await world.OnWorldAsync(() => NowMs(world)) - start), 55_000u, 75_000u);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
        await world.OnWorldAsync(() =>
        {
            Assert.True(Flat(world.Player(leaderId), world.Player(farId)) <= PlayerbotGroupCoordinator.GatherYards, "the missing member was brought to the leader");
            return true;
        });
    }

    [Fact]
    public async Task AGatheringThatWaitsOnAMemberItCannotCatchUp_DropsItAfterSixtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
            options.Party.TeleportToLeader = false;
        });
        (PlayerbotGroupCoordinator.BotGroup group, Guid leaderId, Guid farId, uint start) = await GatheringWithAMissingMemberAsync(world);

        await world.RunAsync(50_000);
        Assert.Equal(PlayerbotGroupState.Gathering, group.State);

        Assert.True(await world.RunUntilAsync(25_000, () => group.State != PlayerbotGroupState.Gathering), await world.TraceAsync());
        Assert.InRange(unchecked(await world.OnWorldAsync(() => NowMs(world)) - start), 55_000u, 75_000u);
        Assert.True(await world.OnWorldAsync(() => Flat(world.Player(leaderId), world.Player(farId)) > PlayerbotGroupCoordinator.GatherYards), "the member is still where it was: the gathering went on without it");
    }

    // --- B3: the approach point ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnApproachPointTheLeaderMakesNoProgressTo_CountsAsReached()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        await world.AddBotAsync("Approachwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Approachmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        uint start = await world.OnWorldAsync(() =>
        {
            // The leader plans its way, but something holds it where it stands (a root, a snare, a wall the mesh does not show).
            world.Player(group.LeaderBotId).AddMovementFlags(ArcaneCore.Protocol.MovementFlags.Root);
            return NowMs(world);
        });

        await world.RunAsync(35_000);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);

        Assert.True(await world.RunUntilAsync(60_000, () => group.State != PlayerbotGroupState.Travelling), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupState.Engaging, group.State);
        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(group.LeaderBotId);
            Assert.True(Vector3.Distance(new Vector3(leader.X, leader.Y, leader.Z), group.Approach) > 20f, "the leader never got near the approach point");
            Assert.True(unchecked(NowMs(world) - start) >= 40_000u);
            return true;
        });
    }

    /// <summary>Plans straight lines and answers a ray along the mesh with a stop at <paramref name="stop"/> (the mesh ends there).</summary>
    private sealed class MeshEndsAt(Vector3 stop) : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.StraightLine(start, end, PathType.Normal);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => new(true, true, 0.5f, stop);
    }

    /// <summary>A pathfinder that knows no mesh query, and one whose mesh cannot answer.</summary>
    private sealed class NoMeshQuery : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.StraightLine(start, end, PathType.Normal);
    }

    private sealed class MeshUnavailable : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => MeshRaycastResult.Unavailable(end);
    }

    [Fact]
    public void AnApproachPointOffTheMesh_IsPulledToTheLastPointOfTheMeshOnTheWayToIt()
    {
        var from = new Vector3(0, 0, 10);
        var offMesh = new Vector3(100, 0, 10);
        var stop = new Vector3(60, 0, 12.5f);

        Assert.Equal(stop, PlayerbotGroupCoordinator.SnapToMesh(new MeshEndsAt(stop), 0, from, offMesh));
        // No mesh query, or a mesh with no answer for this place: the point stays.
        Assert.Equal(offMesh, PlayerbotGroupCoordinator.SnapToMesh(new NoMeshQuery(), 0, from, offMesh));
        Assert.Equal(offMesh, PlayerbotGroupCoordinator.SnapToMesh(new MeshUnavailable(), 0, from, offMesh));
        Assert.Equal(offMesh, PlayerbotGroupCoordinator.SnapToMesh(null, 0, from, offMesh));
    }

    [Fact]
    public async Task TheApproachPointOfANewGroup_IsSnappedToTheMesh()
    {
        var stop = new Vector3(StartX + 5f, StartY + 20f, StartZ);
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options => options.Risk.Tolerance = 0.25f);
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new MeshEndsAt(stop));
            return true;
        });
        await world.AddBotAsync("Snapwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Snapmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 1), await world.TraceAsync());
        Assert.Equal(stop, world.Coordinator.Groups.Single().Approach);
    }

    // --- B4: a quest group that can plan no way gives up as a run does -----------------------------------------------

    [Fact]
    public async Task AQuestGroupThatCanPlanNoWayToItsApproachPoint_GivesUpAfterThirtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        await world.OnWorldAsync(() =>
        {
            Block(world);
            return true;
        });
        await world.AddBotAsync("Noapwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Noapmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 1), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();

        Assert.True(await world.RunUntilAsync(120_000, () => group.Reason is not null), await world.TraceAsync());
        Assert.Equal("no-path-to-approach", group.Reason);
        Assert.False(group.Succeeded);
    }

    [Fact]
    public async Task AQuestGroupThatCanPlanNoWayToItsObjective_GivesUpAfterThirtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        await world.AddBotAsync("Noobwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Noobmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        await world.OnWorldAsync(() =>
        {
            Block(world);
            return true;
        });

        Assert.True(await world.RunUntilAsync(120_000, () => group.Reason is not null), await world.TraceAsync());
        Assert.Equal("no-path-to-objective", group.Reason);
        Assert.False(group.Succeeded);
    }

    // --- B5: the way out of an instance --------------------------------------------------------------------------------

    [Fact]
    public async Task AGroupThatCannotGetEveryoneOutOfTheInstance_IsLetGoAfterTwoBringOuts()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Capwar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Capprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Capmage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == 36)), await world.TraceAsync());

        // The run is over and one member lies dead inside: the others are brought out, the body is not, and the group waits for it.
        await world.OnWorldAsync(() =>
        {
            Block(world);
            Player dead = world.Player(mage.Id);
            dead.Health = 0;
            dead.Combat.DeathState = ArcaneCore.Game.Combat.DeathState.Corpse;
            group.Find(mage.Id)!.AI = null;
            group.Succeeded = true;
            group.Reason = null;
            group.State = PlayerbotGroupState.Leaving;
            group.StateSinceMs = NowMs(world);
            return true;
        });

        // Two bring-outs (a step limit apart) and then the group is let go, not brought out again for good.
        Assert.True(await world.RunUntilAsync(4 * PlayerbotGroupCoordinator.StepTimeoutMs, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Equal(2, world.Coordinator.Events.Count(line => line.Contains("could not walk out: brought to the entrance", StringComparison.Ordinal)));
        Assert.True(group.Succeeded);
        Assert.Equal("completed", group.Reason);
        Assert.Equal(0, world.Coordinator.Totals.Failed);
    }

    // --- B5: a leader that cannot plan the last yards to the approach point but stands near it has arrived ------------------

    /// <summary>
    /// A dungeon group in the Travelling state on a map with no way anywhere, its bots moved <paramref name="yards"/> past the approach
    /// point (on the side away from the entrance) and <paramref name="rise"/> above it: out of the approach ring, near the approach point.
    /// </summary>
    private static async Task<PlayerbotGroupCoordinator.BotGroup> TravellingNearAnUnreachableApproachAsync(GroupTestWorld world, (Guid Id, string Name)[] bots, float yards, float rise)
    {
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        await world.OnWorldAsync(() =>
        {
            Block(world);
            Vector3 away = Vector3.Normalize(new Vector3(group.Approach.X - group.Goal.Meeting.X, group.Approach.Y - group.Goal.Meeting.Y, 0));
            Vector3 spot = group.Approach + (away * yards) + new Vector3(0, 0, rise);
            foreach ((Guid id, _) in bots)
                world.Player(id).Relocate(spot.X, spot.Y, spot.Z, 0, NowMs(world));
            return true;
        });
        return group;
    }

    [Fact]
    public async Task ADungeonLeaderThatCanPlanNoWayToTheApproachPoint_ButStandsNearIt_StartsEntering()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Nearwar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Nearprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Nearmage", Human, Mage, 12, [Fireball], EntranceArea);
        PlayerbotGroupCoordinator.BotGroup group = await TravellingNearAnUnreachableApproachAsync(world, [tank, healer, mage], 10f, 0f);
        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(group.LeaderBotId);
            float ring = PlayerbotGroupCoordinator.ApproachYards + PlayerbotGroupCoordinator.ApproachSlackYards;
            Assert.True(MathF.Sqrt(((leader.X - group.Goal.Meeting.X) * (leader.X - group.Goal.Meeting.X)) + ((leader.Y - group.Goal.Meeting.Y) * (leader.Y - group.Goal.Meeting.Y))) > ring, "outside the ring");
            Assert.True(Vector3.Distance(new Vector3(leader.X, leader.Y, leader.Z), group.Approach) > 5f, "outside the arrival radius");
            return true;
        });

        // Well inside the 30 s no-path bound: the group goes on as soon as the refusal is seen, it does not wait for the bound or the travel timeout.
        Assert.True(await world.RunUntilAsync(10_000, () => group.State is not (PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupState.Entering, group.State);
        Assert.Null(group.Reason);
    }

    [Fact]
    public async Task ADungeonLeaderThatCanPlanNoWayToTheApproachPoint_AndStandsOnAnotherFloorOfIt_DoesNotArrive()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Floorwar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Floorprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Floormage", Human, Mage, 12, [Fireball], EntranceArea);
        PlayerbotGroupCoordinator.BotGroup group = await TravellingNearAnUnreachableApproachAsync(world, [tank, healer, mage], 10f, PlayerbotGroupCoordinator.ApproachHeightYards + 5f);

        await world.RunAsync(10_000);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
    }

    [Fact]
    public async Task ADungeonLeaderThatCanPlanNoWayToTheApproachPoint_AndStandsFarFromIt_DoesNotArrive()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Distwar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Distprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Distmage", Human, Mage, 12, [Fireball], EntranceArea);
        PlayerbotGroupCoordinator.BotGroup group = await TravellingNearAnUnreachableApproachAsync(world, [tank, healer, mage], PlayerbotGroupCoordinator.ApproachYards + 15f, 0f);

        await world.RunAsync(10_000);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
    }
}
