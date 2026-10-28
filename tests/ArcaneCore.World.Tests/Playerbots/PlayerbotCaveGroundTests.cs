using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Goal points of the retreat and the inward explore leg stand on the floor of a cave. Live 2026-10-25 (issues #1478 and #1479,
/// Timbermaw Hold): the floor under a goal point was searched from 20 yards above it only, which inside a tunnel starts in the rock over
/// the bot, so the point stayed at the bot's height in rock, on no region of the region graph, and every plan toward it was refused.
/// </summary>
public sealed class PlayerbotCaveGroundTests
{
    private const float BotZ = 100f;

    /// <summary>
    /// A tunnel: the floor is 1 yard under the bot, the roof 40 yards over it. A probe that starts 10 yards or more above the bot starts
    /// in the rock over the tunnel and finds the roof, as the old search from 20 yards up did.
    /// </summary>
    private static float Tunnel(float x, float y, float probeFrom) => probeFrom >= BotZ + 10f ? BotZ + 40f : BotZ - 1f;

    [Fact]
    public void GroundNear_InsideACave_PutsThePointOnTheFloor_NotTheRoofNorTheBotsHeight()
    {
        Vector3 point = new(10f, 20f, BotZ);
        // The old search: from 20 yards up, the roof 40 yards over the bot, out of reach, so the point stayed at the bot's height.
        Assert.Equal(BotZ + 40f, Tunnel(point.X, point.Y, BotZ + 20f));

        Vector3 grounded = PlayerbotNavigation.GroundNear(Tunnel, null, BotZ, point);

        Assert.Equal(BotZ - 1f, grounded.Z);
        Assert.Equal(point.X, grounded.X);
        Assert.Equal(point.Y, grounded.Y);
    }

    [Fact]
    public void GroundNear_TakesTheFloorNearestTheBot_AndLeavesThePointWhenThereIsNone()
    {
        // The low probe finds a ledge 9 yards under the bot, the high one the floor the bot stands near (1 under it).
        static float Ledges(float x, float y, float probeFrom) => probeFrom <= BotZ + 2f ? BotZ - 9f : BotZ - 1f;
        Assert.Equal(BotZ - 1f, PlayerbotNavigation.GroundNear(Ledges, null, BotZ, new Vector3(0f, 0f, BotZ)).Z);

        Vector3 point = new(1f, 2f, 55f);
        Assert.Equal(point, PlayerbotNavigation.GroundNear((_, _, _) => BotZ + 40f, null, BotZ, point));
        Assert.Equal(point, PlayerbotNavigation.GroundNear((_, _, _) => float.NaN, null, BotZ, point));
    }

    [Fact]
    public void GroundNear_DropsAFloorThatIsOnNoRegionOrAnotherComponent()
    {
        Vector3 point = new(0f, 0f, BotZ);
        // Both floors are dropped: the point is left as it was (no new height claimed).
        Assert.Equal(point, PlayerbotNavigation.GroundNear(Tunnel, _ => false, BotZ, point));
        // Only the floor at BotZ - 1 is walkable.
        Assert.Equal(BotZ - 1f, PlayerbotNavigation.GroundNear(Tunnel, floor => floor.Z < BotZ, BotZ, point).Z);
    }

    [Fact]
    public void InwardLeg_EndsOnTheFloor_WhenGroundIsGiven()
    {
        var home = new PlayerbotHomeArea(1, new Vector3(0f, 0f, BotZ), 100f, "Test");
        var from = new Vector3(80f, 0f, BotZ);

        Vector3 ungrounded = PlayerbotBrain.InwardLeg(home, from, 0);
        Vector3 grounded = PlayerbotBrain.InwardLeg(home, from, 0, end => PlayerbotNavigation.GroundNear(Tunnel, null, BotZ, end));

        Assert.Equal(BotZ, ungrounded.Z);
        Assert.Equal(BotZ - 1f, grounded.Z);
        Assert.Equal(ungrounded.X, grounded.X);
        Assert.Equal(ungrounded.Y, grounded.Y);
    }

    /// <summary>
    /// Live 2026-10-27 (issues #1442, #1567, Timbermaw Hold): the wander leg looked for the floor under its goal from 22 yards over the
    /// bot, which inside a tunnel starts in the rock over it and meets the roof, so the leg was planned toward the roof and refused.
    /// The floor 1 yard under the bot and the roof 12 yards over it: the leg has to be asked for the floor.
    /// </summary>
    [Fact]
    public async Task PlanWander_InsideATunnel_AsksForTheFloor_NotTheRoof()
    {
        var recording = new RecordingPathfinder();
        PlanOutcome outcome = await PlanWanderAsync(new SurfacesLos(99f, 112f), recording);

        Assert.Equal(PlanOutcome.Route, outcome);
        Assert.NotEmpty(recording.Ends);
        Assert.Equal(99f, recording.Ends[0].Z);
    }

    /// <summary>Open ground whose only surface is a slope 15 yards over the bot: the leg still climbs onto it (the old snap's job).</summary>
    [Fact]
    public async Task PlanWander_TowardAHillsideRisingAboveTheBot_StillSnapsOntoIt()
    {
        var recording = new RecordingPathfinder();
        PlanOutcome outcome = await PlanWanderAsync(new SurfacesLos(BotZ + 15f), recording);

        Assert.Equal(PlanOutcome.Route, outcome);
        Assert.NotEmpty(recording.Ends);
        Assert.Equal(BotZ + 15f, recording.Ends[0].Z);
    }

    /// <summary>
    /// A hillside floor 15 yards over the bot that the region graph puts in another walking component (a ledge across a cliff) is not
    /// snapped onto: the goal point stays where it was. The same hillside in the bot's own component is snapped onto.
    /// </summary>
    [Fact]
    public void GroundNear_HillsideInAnotherWalkingComponent_IsNotSnappedOnto()
    {
        const float hillside = BotZ + 15f;
        static float Hill(float x, float y, float probeFrom) => hillside;
        Vector3 point = new(48f, 0f, BotZ);

        Assert.Equal(point, PlayerbotNavigation.GroundNear(Hill, floor => floor.Z < hillside, BotZ, point));
        Assert.Equal(hillside, PlayerbotNavigation.GroundNear(Hill, floor => floor.Z >= hillside, BotZ, point).Z);
    }

    private static async Task<PlanOutcome> PlanWanderAsync(ILineOfSight los, IPathfinder pathfinder)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var options = new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } };
        var brain = new PlayerbotBrain(session, options);
        try
        {
            return await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.SetPosition(100f, 100f, BotZ, 0f);
                WorldCollision.Of(host.World).Install(lineOfSight: los, pathfinder: pathfinder);
                TileWait wait = default;
                return PlayerbotNavigation.PlanWander(player, new Vector3(148f, 100f, BotZ), options, ref wait, host.World.NowMs, out _);
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Model surfaces at fixed heights: a probe returns the highest one that is not over it and within the search distance.</summary>
    private sealed class SurfacesLos(params float[] surfaces) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        {
            float? best = null;
            foreach (float surface in surfaces)
                if (surface <= z && surface >= z - maxSearchDistance && (best is null || surface > best)) best = surface;
            return best;
        }
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class RecordingPathfinder : IPathfinder
    {
        public List<Vector3> Ends { get; } = [];
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Ends.Add(end);
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }
}
