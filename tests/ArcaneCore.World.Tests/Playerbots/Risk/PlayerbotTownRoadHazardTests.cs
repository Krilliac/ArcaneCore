using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Live 2026-10-12: completed turn-ins were set aside over and over (quest 8, ender 5688, bots near 2176,891 map 0;
/// quest 2160, ender 6806, Dun Morogh around -5600) because the only road into town clipped a death or retreat mark.
/// The mark's centre stands beside the road. That must not refuse the walk to a friendly quest ender. A mark on the
/// road, one that covers the ender, a walk to an ender that is not friendly, and a creature hazard still do.
/// </summary>
public sealed class PlayerbotTownRoadHazardTests
{
    private const float GoalYards = 80f;

    [Fact]
    public void ATownFactionIsFriendly_AMonsterFactionIsNot()
    {
        var factions = new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 1, FriendlyMask: 0, HostileMask: 0),
            new FactionTemplateRecord(11, 72, 0, OwnMask: 1, FriendlyMask: 1, HostileMask: 0),
            new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
        ]);
        Assert.True(PlayerbotNavigation.IsFriendlyTownNpc(factions, 1, 11));
        Assert.False(PlayerbotNavigation.IsFriendlyTownNpc(factions, 1, 14));
        Assert.False(PlayerbotNavigation.IsFriendlyTownNpc(null, 1, 11));
        Assert.False(PlayerbotNavigation.IsFriendlyTownNpc(factions, 1, 99));
    }

    [Theory]
    [InlineData("death")]
    [InlineData("retreat")]
    public async Task APlaceMarkBesideTheOnlyRoad_DoesNotBlockAFriendlyTownEnder(string kind)
    {
        float radius = kind == "death" ? PlayerbotHazards.DeathYards : PlayerbotHazards.RetreatYards;
        Walk walk = await WalkAsync(kind, radius, Beside(radius), friendly: true);
        Assert.True(walk.Planned, kind);
        Assert.True(walk.OnTheRoad, kind);
        Assert.True(walk.Crossed, kind);
        Assert.True(walk.Advanced, kind);
        Assert.False(walk.Diagnosed, kind);
        Assert.True(walk.CrossBesidePlaceMarks, kind);
    }

    [Fact]
    public async Task ADeathMarkOnTheRoad_StillBlocksTheFriendlyTownEnder()
    {
        Walk walk = await WalkAsync("death", PlayerbotHazards.DeathYards, PlayerbotRisk.PlaceMarkBesideRoadYards * 0.25f, friendly: true);
        Assert.False(walk.Planned);
        Assert.True(walk.Diagnosed);
        Assert.Equal("death", walk.Kind);
    }

    [Fact]
    public async Task ADeathMarkBesideTheRoad_StillBlocksWhenTheEnderIsNotFriendly()
    {
        Walk walk = await WalkAsync("death", PlayerbotHazards.DeathYards, Beside(PlayerbotHazards.DeathYards), friendly: false);
        Assert.False(walk.Planned);
        Assert.True(walk.Diagnosed);
        Assert.Equal("death", walk.Kind);
    }

    [Fact]
    public async Task ADeathMarkThatCoversTheFriendlyEnder_StillBlocks()
    {
        float radius = PlayerbotHazards.DeathYards;
        Walk walk = await WalkAsync("death", radius, Beside(radius), friendly: true, alongYards: GoalYards - 10f);
        Assert.False(walk.Planned);
        Assert.True(walk.Diagnosed);
        Assert.Equal("death", walk.Kind);
    }

    [Fact]
    public async Task ALethalHazardBesideTheRoad_StillBlocksTheFriendlyTownEnder()
    {
        Walk walk = await WalkAsync("lethal", 20f, Beside(20f), friendly: true);
        Assert.False(walk.Planned);
        Assert.True(walk.Diagnosed);
        Assert.Equal("lethal", walk.Kind);
    }

    private static float Beside(float radius) => (PlayerbotRisk.PlaceMarkBesideRoadYards + radius) * 0.5f;

    private readonly record struct Walk(bool Planned, bool OnTheRoad, bool Crossed, bool Advanced, bool Diagnosed,
        bool CrossBesidePlaceMarks, string? Kind);

    private static async Task<Walk> WalkAsync(string kind, float radius, float sideYards, bool friendly, float alongYards = 40f)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        try
        {
            return await world.OnWorldAsync(() =>
            {
                var player = world.Player;
                float center = player.X;
                var goal = new Vector3(center, player.Y + GoalYards, player.Z);
                var mark = new Vector3(center + sideYards, player.Y + alongYards, player.Z);
                WorldCollision.Of(world.Host.World).Install(pathfinder: new RoadOnly(center, halfWidth: 4f));
                world.Brain.Risk.Hazards.Add(player.MapId, mark, radius, world.Host.World.NowMs, 300, kind);
                PlayerbotNavigation.Guard(player, world.Brain.Risk);
                bool planned = PlayerbotNavigation.TryPlan(player, goal, world.Options, out PlayerbotRoute? route,
                    completedQuestReturn: true, friendlyTownEnder: friendly);
                bool diagnosed = PlayerbotNavigation.HasBlockingReturnHazard(player, goal, world.Options, out PlayerbotHazard hazard,
                    friendlyTownEnder: friendly);
                bool advanced = false;
                if (planned && route is not null)
                {
                    world.Session.ManagedBudget = new ManagedActionBudget(8);
                    advanced = PlayerbotNavigation.TryAdvance(world.Session, route, world.Options, RiskTestWorld.ThinkMs,
                        world.Host.World.NowMs);
                }

                return new Walk(planned, planned && route is not null && route.Points.All(point => MathF.Abs(point.X - center) < 1f),
                    planned && route is not null && Crosses(new PlayerbotHazard(player.MapId, mark, radius, uint.MaxValue, default, kind), route.Points),
                    advanced, diagnosed, route?.CrossBesidePlaceMarks == true, diagnosed ? hazard.Kind : null);
            });
        }
        finally
        {
            await world.OnWorldAsync(() =>
            {
                if (world.Session.Player is { } player) PlayerbotNavigation.Guard(player, null);
                return true;
            });
        }
    }

    private static bool Crosses(PlayerbotHazard hazard, IReadOnlyList<Vector3> points)
    {
        for (int index = 1; index < points.Count; index++)
        {
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(points[index - 1], points[index]) / 2f));
            for (int step = 0; step <= steps; step++)
                if (hazard.Covers(Vector3.Lerp(points[index - 1], points[index], (float)step / steps))) return true;
        }

        return false;
    }

    /// <summary>The road is the only walk: a query that leaves the strip has no path, so a detour off it cannot be planned.</summary>
    private sealed class RoadOnly(float centerX, float halfWidth) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => MathF.Abs(start.X - centerX) <= halfWidth && MathF.Abs(end.X - centerX) <= halfWidth
                ? new PathResult(PathType.Normal, [start, end])
                : PathResult.None(start);
    }
}
