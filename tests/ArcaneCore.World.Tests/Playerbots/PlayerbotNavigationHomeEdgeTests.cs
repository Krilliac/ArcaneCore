using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// An exploring bot beyond <see cref="PlayerbotHomeArea.ExploreFraction"/> of its home radius heads back in, and keeps moving when
/// the straight way in is refused. Live 2026-10-11 (bot QA issue #154, 200 bots homed at Sentinel Hill, radius 550, build 567dbfeb):
/// 74 of the 204 stalls of the run lay 460 to 480 yards from the centre, just past the 440-yard explore radius, 49 of them with goal
/// Explore; replayed from the live characters database on the real terrain, the bots stood there while their one leg towards the
/// centre was on the motion's loop blacklist (PlayerbotMotion.LoopBlacklistMs): the brain refused it again on every think
/// (hundreds of refusals a minute) because beyond the explore radius it always aimed straight at the centre, the refusals' turn
/// included.
/// </summary>
public sealed class PlayerbotNavigationHomeEdgeTests
{
    [Fact]
    public async Task Brain_BeyondTheExploreRadius_WhenTheStraightWayInIsRefused_StillWalksBackIn()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                // The centre lies 90 yards east of the bot; radius 100, explore radius 80: the bot is inside its home and past the
                // explore radius, so its legs head for the centre.
                var centre = new Vector3(player.X + 90f, player.Y, player.Z);
                var area = new PlayerbotHomeArea(player.Map!.MapId, centre, 100f, "Test");
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new StraightInRefused(centre));
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                float before = area.DistanceTo(player.X, player.Y);

                bool explored = false;
                for (int think = 0; think < 30 && area.DistanceTo(player.X, player.Y) > before - 10f; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(1_000);
                    explored |= brain.Goal == PlayerbotGoalKind.Explore;
                    PlayerbotMotion.ElapseForTests(player, 1_000);
                }

                Assert.True(explored, $"the bot never explored (goal {brain.Goal})");
                float after = area.DistanceTo(player.X, player.Y);
                Assert.True(after <= before - 10f,
                    $"the bot stood {after:F1} yards from the centre (started {before:F1}): every leg went straight at the refused way in");
                Assert.True(area.Contains(player), "the bot left its home on the way back in");
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A small home (radius 60, explore radius 48) with the bot 55 yards out and every way within 70 degrees of the centre refused:
    /// only the widest turns of the fan are walkable. Each leg must still end closer to the centre than the bot stood, inside the
    /// home. Before the leg was shortened, a 48-yard leg 75 degrees off the centre from 55 yards ended 62.9 yards out, past the
    /// radius, and the walk home took over.
    /// </summary>
    [Fact]
    public async Task Brain_InASmallHome_AWideTurnBackIn_NeverEndsFartherOutOrLeavesTheHome()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var centre = new Vector3(player.X + 55f, player.Y, player.Z);
                var area = new PlayerbotHomeArea(player.Map!.MapId, centre, 60f, "Small");
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new StraightInRefused(centre, 70f));
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                float start = area.DistanceTo(player.X, player.Y);
                float farthest = start;

                for (int think = 0; think < 60 && area.DistanceTo(player.X, player.Y) > area.ExploreRadius; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(1_000);
                    Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
                    PlayerbotMotion.ElapseForTests(player, 1_000);
                    farthest = MathF.Max(farthest, area.DistanceTo(player.X, player.Y));
                }

                Assert.True(farthest <= start + 0.5f, $"a leg back in took the bot {farthest:F1} yards out (started {start:F1}, radius {area.Radius})");
                Assert.True(area.DistanceTo(player.X, player.Y) <= area.ExploreRadius,
                    $"the bot is still {area.DistanceTo(player.X, player.Y):F1} yards out (explore radius {area.ExploreRadius})");
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Open ground, except that a path whose end lies within <paramref name="degrees"/> (default 10) degrees of the straight line from its start to <paramref name="centre"/>
    /// is refused (no path): as a looped destination on the motion's blacklist, a hazard or a fence in the way is.
    /// </summary>
    private sealed class StraightInRefused(Vector3 centre, float degrees = 10f) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Vector2 way = new(end.X - start.X, end.Y - start.Y), inward = new(centre.X - start.X, centre.Y - start.Y);
            if (way.Length() < 1f || inward.Length() < 1f) return PathResult.StraightLine(start, end, PathType.Normal);
            float cos = Vector2.Dot(Vector2.Normalize(way), Vector2.Normalize(inward));
            return cos >= MathF.Cos(degrees * MathF.PI / 180f) ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    [Fact]
    public void DeflectZoneBorderLeg_InsideExploreRadius_DeflectsAlongBorderInsteadOfSnappingBackToStartCentre()
    {
        // Model Tirisfal Glades graduated home centred on Deathknell (1676, 1677) with radius 4500 (ExploreRadius 3600)
        // and a southern zone border at X = 2200 (near Cold Hearth Manor, where stepping further south crosses into Silverpine).
        var deathknell = new Vector3(1676f, 1677f, 35f);
        var home = new PlayerbotHomeArea(0, deathknell, 4500f, "zone:deathknell", zoneClipped: true,
            zones: new PlayerbotZoneLookup((x, _, _) => x <= 2200f ? 85u : 130u));

        // Bot stands at Cold Hearth Manor (2185, 660), 1137 yards from Deathknell (well inside ExploreRadius 3600),
        // heading south-southeast (angle = -pi/6) so a straight 48-yard leg crosses X > 2200 into Silverpine (zone 130).
        var manor = new Vector3(2185f, 660f, 35f);
        float angle = -MathF.PI / 6f;
        Vector3 deflected = PlayerbotBrain.DeflectZoneBorderLeg(home, manor, angle, preferPositive: false, refusals: 0);

        Assert.True(home.InZone(deflected.X, deflected.Y, deflected.Z), "deflected leg must remain in Tirisfal Glades");
        Assert.True(home.DistanceTo(deflected.X, deflected.Y) <= home.ExploreRadius);
        // Rather than snapping northwest toward Deathknell (Y > manor.Y), the deflected leg continues further across the zone (Y < manor.Y).
        Assert.True(deflected.Y < manor.Y, $"expected deflection along the zone border (Y < {manor.Y}), got ({deflected.X:F1}, {deflected.Y:F1})");
    }
}
