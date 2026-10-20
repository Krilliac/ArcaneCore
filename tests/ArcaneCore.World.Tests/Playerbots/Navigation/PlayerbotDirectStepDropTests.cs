using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Navigation;

/// <summary>
/// The drop limit of a non-navigated step (<see cref="PlayerbotMoveCheck.TryValidateStep"/>): a bot standing on the floor steps down 2.5 yd
/// at most, never off an edge into a pit; a waypoint height that drifted above the floor is draped.
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
    public async Task ARouteWithDriftedWaypoints_DownA35DegreeSlope_FallingMoreThanTheLimit_IsRefused()
    {
        // Documented current behaviour: 5 yd on a 35 degree slope falls 3.5 yd, more than DirectStepMaxDropYards, so the step is refused.
        // Walking down walkable slopes under drifted waypoints is a separate design task.
        float tan = MathF.Tan(35f * MathF.PI / 180f);
        (bool ok, _, string? reason) = await Step(
            new Vector3(StepX, StepY, FloorZ), new Vector3(StepX + 5f, StepY, FloorZ), x => FloorZ - ((x - StepX) * tan));
        Assert.False(ok);
        Assert.Equal("NoGroundSupport", reason);
    }

    [Fact]
    public async Task A60DegreeDescent_IsRefused()
    {
        float tan = MathF.Tan(60f * MathF.PI / 180f);
        (bool ok, _, string? reason) = await Step(
            new Vector3(StepX, StepY, FloorZ), new Vector3(StepX + 5f, StepY, FloorZ), x => FloorZ - ((x - StepX) * tan));
        Assert.False(ok);
        Assert.Equal("NoGroundSupport", reason);
    }

    [Fact]
    public async Task AGapWithNoFloor_InTheMiddleOfAStep_WhoseEndIsFourYardsLower_IsRefused()
    {
        (bool ok, _, string? reason) = await Step(
            new Vector3(StepX, StepY, FloorZ), new Vector3(StepX + 5f, StepY, FloorZ),
            x => x > StepX + 0.5f && x < StepX + 4.5f ? null : x <= StepX + 0.5f ? FloorZ : FloorZ - 4f);
        Assert.False(ok);
        Assert.Equal("NoGroundSupport", reason);
    }

    [Fact]
    public async Task AFourYardLedge_InTheMiddleOfAFiveYardStep_IsRefused()
    {
        // The floors under the two ends differ by 4 yd over 5 yd of run; the drop is all in one yard.
        (bool ok, _, string? reason) = await Step(
            new Vector3(StepX, StepY, FloorZ), new Vector3(StepX + 5f, StepY, FloorZ), x => x < StepX + 2.5f ? FloorZ : FloorZ - 4f);
        Assert.False(ok);
        Assert.Equal("NoGroundSupport", reason);
    }

    [Fact]
    public async Task ALongStep_OverATenYardPit_IsRefused_HoweverLongTheRun()
    {
        // A tick stall makes the step as long as 8.5 yd (run speed times the age of the anchor): 10 yd of drop is a pit however long the step.
        (bool ok, _, string? reason) = await Step(
            new Vector3(StepX, StepY, FloorZ), new Vector3(StepX + 8.5f, StepY, FloorZ), x => x < StepX + 4f ? FloorZ : FloorZ - 10f);
        Assert.False(ok);
        Assert.Equal("NoGroundSupport", reason);
    }

    [Fact]
    public async Task ADipInTheMiddleOfAStep_WhoseEndsAreOnTheFloor_IsNotDetected_ByTheEndsOnlyRule()
    {
        // Documented current behaviour: only the floor under the destination is judged, so a dip between two ends on the floor is not seen.
        // Judging the floor along the step is a separate design task.
        (bool ok, _, string? reason) = await Step(
            new Vector3(StepX, StepY, FloorZ), new Vector3(StepX + 5f, StepY, FloorZ),
            x => x >= StepX + 1.5f && x < StepX + 3.5f ? FloorZ - 4f : FloorZ);
        Assert.True(ok, $"refused: {reason}");
    }

    private static async Task<(bool Ok, Vector3 Draped, string? Reason)> Step(Vector3 from, Vector3 to, Func<float, float?>? floorAtX = null)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            return await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FloorGround(floorAtX ?? (_ => (float?)FloorZ)));
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

    private sealed class FloorGround(Func<float, float?> floorAtX) : ILineOfSight
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
