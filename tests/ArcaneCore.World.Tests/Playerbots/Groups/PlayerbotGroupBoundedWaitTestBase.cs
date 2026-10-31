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
public abstract class PlayerbotGroupBoundedWaitTestBase
{
    private protected const byte Human = 1;
    private protected const byte Warrior = 1, Priest = 5, Mage = 8;
    private protected const uint Fireball = 133, LesserHeal = 2050, Smite = 585;

    private protected static uint NowMs(GroupTestWorld world) => world.World.Host.World.NowMs;

    private protected static float Flat(Player a, Player b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>No way leads anywhere.</summary>
    private protected sealed class NoPath : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }

    private protected static void Block(GroupTestWorld world) => WorldCollision.Of(world.World.Host.World).Install(pathfinder: new NoPath());

    /// <summary>A two-bot group in the Travelling state, its second bot (by leadership) moved <paramref name="yards"/> behind the leader and unable to walk.</summary>
    private protected static async Task<(PlayerbotGroupCoordinator.BotGroup Group, Guid LeaderId, Guid LaggardId)> TravellingWithALaggardAsync(GroupTestWorld world, float yards)
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

    // --- B2: the gathering --------------------------------------------------------------------------------------------

    private protected static async Task<(PlayerbotGroupCoordinator.BotGroup Group, Guid LeaderId, Guid FarId, uint StartMs)> GatheringWithAMissingMemberAsync(GroupTestWorld world)
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

    /// <summary>Plans straight lines and answers a ray along the mesh with a stop at <paramref name="stop"/> (the mesh ends there).</summary>
    private protected sealed class MeshEndsAt(Vector3 stop) : IPathfinder, IMeshPointQuery
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
    private protected sealed class NoMeshQuery : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.StraightLine(start, end, PathType.Normal);
    }

    private protected sealed class MeshUnavailable : IPathfinder, IMeshPointQuery
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

    // --- B5: a leader that cannot plan the last yards to the approach point but stands near it has arrived ------------------

    /// <summary>
    /// A dungeon group in the Travelling state on a map with no way anywhere, its bots moved <paramref name="yards"/> past the approach
    /// point (on the side away from the entrance) and <paramref name="rise"/> above it: out of the approach ring, near the approach point.
    /// </summary>
    private protected static async Task<PlayerbotGroupCoordinator.BotGroup> TravellingNearAnUnreachableApproachAsync(GroupTestWorld world, (Guid Id, string Name)[] bots, float yards, float rise)
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
}
