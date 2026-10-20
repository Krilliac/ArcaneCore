using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Navigation;

/// <summary>
/// The drop limit of a non-navigated step (<see cref="PlayerbotMoveCheck.TryValidateStep"/>): a bot standing on the floor steps down a few
/// yards at most (and down any walkable slope), never off an edge into a pit; a waypoint height that drifted above the floor is draped.
/// </summary>
public sealed class PlayerbotDirectStepDropTests
{
    private const float FloorZ = 47f;
    private const float StepX = 1320f, StepY = -4656f;

    [Fact]
    public async Task AStandingBot_JustUnderTheSnapThreshold_StepsAlong_WithoutARejection()
    {
        // A bot 2.2 yd above its floor is under DirectStepDrapeMinYards, so it is not snapped before it steps: its first step must be
        // allowed rather than refused as a drop (one rejection and an anomaly report for nothing).
        float above = PlayerbotMotion.DirectStepDrapeMinYards - 0.3f;
        Assert.True(above > 2f, "the case is a height in the old gap between the drop limit and the snap threshold");
        (bool ok, Vector3 draped, string? reason) = await Step(new Vector3(StepX, StepY, FloorZ + above), new Vector3(StepX + 2f, StepY, FloorZ + above));
        Assert.True(ok, $"refused: {reason}");
        Assert.Equal(FloorZ + 0.05f, draped.Z, 0.01f);
    }

    [Fact]
    public async Task ADropJustUnderTheLimit_IsAccepted_JustOverItIsRefused()
    {
        float limit = PlayerbotMoveCheck.DirectStepMaxDropYards;
        (bool under, _, string? underReason) = await Step(new Vector3(StepX, StepY, FloorZ + limit - 0.1f), new Vector3(StepX + 2f, StepY, FloorZ + limit - 0.1f));
        Assert.True(under, $"refused: {underReason}");
        (bool over, _, string? overReason) = await Step(new Vector3(StepX, StepY, FloorZ + limit + 0.1f), new Vector3(StepX + 2f, StepY, FloorZ + limit + 0.1f));
        Assert.False(over);
        Assert.Equal("NoGroundSupport", overReason);
    }

    [Fact]
    public async Task ARouteWithDriftedWaypoints_DownA35DegreeSlope_IsWalkable()
    {
        // The anchor is the previous heartbeat position, on the slope; the waypoint heights are flat chords above it. Five yards on, the
        // slope has fallen 3.5 yd: that is a walkable slope, not a pit.
        float tan = MathF.Tan(35f * MathF.PI / 180f);
        (bool ok, Vector3 draped, string? reason) = await Step(
            new Vector3(StepX, StepY, FloorZ), new Vector3(StepX + 5f, StepY, FloorZ), x => FloorZ - ((x - StepX) * tan));
        Assert.True(ok, $"refused: {reason}");
        Assert.Equal(FloorZ - (5f * tan) + 0.05f, draped.Z, 0.05f);
    }

    [Fact]
    public async Task ACliffEdge_IsRefused_EvenWithTheSlopeAllowance()
    {
        (bool ok, _, string? reason) = await Step(
            new Vector3(StepX, StepY, FloorZ), new Vector3(StepX + 5f, StepY, FloorZ), x => x < StepX + 2f ? FloorZ : FloorZ - 10f);
        Assert.False(ok);
        Assert.Equal("NoGroundSupport", reason);
    }

    private static async Task<(bool Ok, Vector3 Draped, string? Reason)> Step(Vector3 from, Vector3 to, Func<float, float>? floorAtX = null)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            return await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FloorGround(floorAtX ?? (_ => FloorZ)));
                Map map = session.Player!.Map!;
                bool ok = PlayerbotMoveCheck.TryValidateStep(map, from, to, out Vector3 draped, out string? reason);
                return (ok, draped, reason);
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private sealed class FloorGround(Func<float, float> floorAtX) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => floorAtX(x);

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
