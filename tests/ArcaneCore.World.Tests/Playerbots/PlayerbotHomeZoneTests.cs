using System.Numerics;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A fact that needs both the client's AreaTable.dbc (<c>ARCANECORE_TEST_DBC_DIR</c>) and the terrain extraction
/// (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, the directory holding <c>maps/</c>). Neither is committed; without them the test is reported
/// Skipped, never a silent pass.
/// </summary>
internal sealed class RealAreaTerrainFactAttribute : FactAttribute
{
    public RealAreaTerrainFactAttribute()
    {
        string? dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR");
        string? terrain = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(dbc) || !File.Exists(Path.Combine(dbc, "AreaTable.dbc")))
            Skip = "ARCANECORE_TEST_DBC_DIR is not set to a directory with AreaTable.dbc: the real zone-clip test did NOT run.";
        else if (string.IsNullOrWhiteSpace(terrain) || !Directory.Exists(Path.Combine(terrain, "maps")))
            Skip = $"{RealTerrainBotFactAttribute.Variable} is not set to a terrain data root with maps/: the real zone-clip test did NOT run.";
    }
}

/// <summary>
/// The zone clip of the bot home area (<see cref="PlayerbotHomeArea"/>): a home counts a point as inside only when it is inside the
/// circle and in the zone of the centre. The live failure it answers: 200 level-14 bots homed at Sentinel Hill (Westfall, zone 40)
/// with radius 750 died again and again at Raven Hill in Duskwood (zone 10, QA death-loop findings near -10650,450, creatures
/// 202/213/539/565/569 of level 20 and more), which lies inside the circle. Zone ids are the client's AreaTable.dbc; the real test
/// reads them from the terrain and the DBC, the others use a two-zone fixture.
/// </summary>
public sealed class PlayerbotHomeZoneTests
{
    private const uint Westfall = 40;
    private const uint Duskwood = 10;

    // The Sentinel Hill tower (Westfall) and Raven Hill (Duskwood), 631 yards apart: inside a 750-yard circle.
    private static readonly Vector3 SentinelHill = new(-10630f, 1080f, 40f);
    private static readonly Vector3 RavenHill = new(-10650f, 450f, 40f);

    // Fixture: the Westfall-Duskwood border as the line y = 600 (Duskwood south-east of it, at smaller y).
    private static readonly PlayerbotZoneLookup BorderAt600 = new((x, y, z) => y >= 600f ? Westfall : Duskwood);

    [Fact]
    public void ClippedHome_ACreatureInsideTheCircleButAcrossTheZoneBorder_IsOutside_AndOneInTheZoneIsInside()
    {
        var clipped = new PlayerbotHomeArea(0, SentinelHill, 750f, "SentinelHill", zoneClipped: true, BorderAt600);
        var circle = new PlayerbotHomeArea(0, SentinelHill, 750f, "SentinelHill");

        Assert.True(clipped.DistanceTo(RavenHill.X, RavenHill.Y) < 750f);
        Assert.Equal(Westfall, clipped.ZoneId);
        Assert.Equal("40", clipped.ZoneText);
        // The circle alone (the old home) takes Raven Hill in; the clipped home does not.
        Assert.True(circle.Contains(0, RavenHill.X, RavenHill.Y, RavenHill.Z));
        Assert.False(clipped.Contains(0, RavenHill.X, RavenHill.Y, RavenHill.Z));
        Assert.False(clipped.Contains(new CreatureSpawn { Guid = 1, Entry = 202, MapId = 0, X = RavenHill.X, Y = RavenHill.Y, Z = 40f }));
        // Same zone, inside the circle: inside. Same zone, outside the circle: outside.
        Assert.True(clipped.Contains(0, -10500f, 1200f, 40f));
        Assert.False(clipped.Contains(0, -10630f, 1900f, 40f));
        Assert.Equal("off", circle.ZoneText);
    }

    [Fact]
    public void ClippedHome_FailsClosed_WhereTheTerrainKnowsNoZone()
    {
        // A position whose zone is not known (0) is outside.
        var holes = new PlayerbotZoneLookup((x, y, z) => x > -10600f ? 0u : Westfall);
        var area = new PlayerbotHomeArea(0, SentinelHill, 750f, null, zoneClipped: true, holes);
        Assert.True(area.Contains(0, -10650f, 1080f, 0f));
        Assert.False(area.Contains(0, -10590f, 1080f, 0f));

        // A centre with no known zone: the home contains nothing (not even its centre), and says so once.
        int reported = 0;
        var unknown = new PlayerbotHomeArea(0, SentinelHill, 750f, null, zoneClipped: true, new PlayerbotZoneLookup((x, y, z) => 0u),
            _ => reported++);
        Assert.False(unknown.Contains(0, SentinelHill.X, SentinelHill.Y, SentinelHill.Z));
        Assert.True(unknown.ZoneUnknown);
        Assert.Equal("unknown", unknown.ZoneText);
        Assert.False(unknown.Contains(0, -10500f, 1200f, 0f));
        Assert.Equal(1, reported);

        // No lookup at all (no world): the same.
        var none = new PlayerbotHomeArea(0, SentinelHill, 750f, null, zoneClipped: true, zones: null);
        Assert.True(none.ZoneUnknown);
        Assert.False(none.Contains(0, SentinelHill.X, SentinelHill.Y, SentinelHill.Z));
    }

    /// <summary>
    /// The terrain's own answer at the real places: Sentinel Hill is Westfall, Raven Hill (where the bots died) is Duskwood, and a
    /// zone-clipped Sentinel Hill home of radius 750 leaves Raven Hill out while the circle alone takes it in.
    /// </summary>
    [RealAreaTerrainFact]
    public void RealTerrain_SentinelHillHome_ExcludesRavenHillInDuskwood()
    {
        string dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;
        string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
        var terrain = new TerrainManager(root)
        {
            Areas = new AreaTable(AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
                r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId)), MapRegistry.Default),
        };
        PlayerbotZoneLookup zones = PlayerbotZoneLookup.FromTerrain(terrain, 0);

        Assert.Equal(Westfall, zones.ZoneAt(SentinelHill.X, SentinelHill.Y, SentinelHill.Z));
        Assert.Equal(Duskwood, zones.ZoneAt(RavenHill.X, RavenHill.Y, RavenHill.Z));
        // The lookup is the map's (TerrainInfo.GetZoneAndAreaId, which Map.GetZoneAndAreaId calls).
        Assert.Equal(terrain.For(0).GetZoneAndAreaId(RavenHill.X, RavenHill.Y, RavenHill.Z).ZoneId,
            zones.ZoneAt(RavenHill.X, RavenHill.Y, RavenHill.Z));
        // Every QA death-loop position near Raven Hill is Duskwood.
        foreach ((float x, float y) in new[] { (-10650f, 400f), (-10650f, 450f), (-10700f, 450f), (-10600f, 500f), (-10700f, 500f) })
            Assert.Equal(Duskwood, zones.ZoneAt(x, y, 0f));

        var clipped = new PlayerbotHomeArea(0, SentinelHill, 750f, "SentinelHill", zoneClipped: true, zones);
        var circle = new PlayerbotHomeArea(0, SentinelHill, 750f, "SentinelHill");
        Assert.Equal(Westfall, clipped.ZoneId);
        Assert.True(circle.Contains(0, RavenHill.X, RavenHill.Y, RavenHill.Z));
        Assert.False(clipped.Contains(0, RavenHill.X, RavenHill.Y, RavenHill.Z));
        Assert.True(clipped.Contains(0, SentinelHill.X, SentinelHill.Y, SentinelHill.Z));
    }

    /// <summary>
    /// The brain's grind choice with a zone-clipped home: two creatures inside the circle, the nearer one across the zone border.
    /// The circle alone picks the nearer one (RED: the old behaviour); the clipped home picks the one in the zone (GREEN).
    /// </summary>
    [Fact]
    public async Task Brain_WithZoneClippedHome_DoesNotGrindAcrossTheZoneBorder()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature? acrossBorder = null, inZone = null;
            float border = 0f;
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                border = player.X + 4f;
                acrossBorder = Hostile(16, 7, 12);
                inZone = Hostile(17, 7, 13);
                acrossBorder.Relocate(player.X + 8, player.Y, player.Z, 0, host.World.NowMs);
                inZone.Relocate(player.X - 12, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(acrossBorder);
                player.Map.AddObject(inZone);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(acrossBorder!.Guid)
                && session.Player.VisibleObjects.Contains(inZone!.Guid), "both candidates visible");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var zones = new PlayerbotZoneLookup((x, y, z) => x > border ? Duskwood : Westfall);
                var centre = new Vector3(player.X, player.Y, player.Z);

                // Both creatures are inside a 30-yard circle round the bot, and the nearer one is the plain choice (the circle alone
                // would let the bot take it).
                var circle = new PlayerbotHomeArea(player.Map!.MapId, centre, 30f, null);
                Assert.True(circle.Contains(acrossBorder!) && circle.Contains(inZone!));
                Assert.Same(acrossBorder, PlayerbotBrain.FindTarget(player));

                var clipped = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true })
                {
                    Home = new PlayerbotHomeArea(player.Map!.MapId, centre, 30f, null, zoneClipped: true, zones),
                };
                session.ManagedBudget = new ManagedActionBudget(2);
                clipped.Update(1_000);
                Assert.Equal(inZone!.Entry, clipped.TargetEntry);
                Assert.NotEqual(PlayerbotGoalKind.Home, clipped.Goal);
                clipped.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A bot inside the circle but in another zone (a bot that wandered to Raven Hill) walks back: the circle alone calls it inside
    /// (RED), the clipped home calls it returning and the bot walks until it is in the zone and inside the return radius (GREEN).
    /// The walk itself crosses nothing the zone forbids: the way back from another zone is never restricted.
    /// </summary>
    [Fact]
    public async Task Brain_InsideTheCircleButOutOfTheZone_WalksBackIntoTheZone()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                // The centre is 60 yards east, radius 100 (return radius 75): the bot is inside the circle. The border is 30 yards
                // east of the bot: the bot stands in the other zone.
                float border = player.X + 30f;
                var zones = new PlayerbotZoneLookup((x, y, z) => x >= border ? Westfall : Duskwood);
                var centre = new Vector3(player.X + 60f, player.Y, player.Z);

                var circleReturn = new PlayerbotHomeReturn();
                Assert.Equal(PlayerbotHomeState.Inside, circleReturn.Evaluate(player, new PlayerbotHomeArea(player.Map!.MapId, centre, 100f, null), 0));

                var area = new PlayerbotHomeArea(player.Map!.MapId, centre, 100f, "Test", zoneClipped: true, zones);
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
                Assert.Equal(PlayerbotHomeState.Returning, brain.HomeReturn.State);

                int steps = 0;
                while (brain.HomeReturn.State != PlayerbotHomeState.Inside && steps++ < 200)
                {
                    PlayerbotMotion.ElapseForTests(player, 1_000);
                    session.ManagedBudget = new ManagedActionBudget(2);
                    brain.Update(1_000);
                }

                Assert.Equal(PlayerbotHomeState.Inside, brain.HomeReturn.State);
                Assert.True(player.X >= border, $"stopped at x={player.X}, the zone starts at {border}");
                Assert.True(area.DistanceTo(player.X, player.Y) <= area.ReturnRadius + 10f);
                Assert.Contains("zone=40", brain.HomeDescription, StringComparison.Ordinal);
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// The walk-home evaluation of a clipped home whose centre's zone is not known: it cannot be walked into, so the walk uses the
    /// circle alone (the bot is not sent walking for ever), while the area itself contains nothing.
    /// </summary>
    [Fact]
    public async Task HomeReturn_WithAnUnknownHomeZone_UsesTheCircleForTheWalk()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 20f, player.Y, player.Z), 100f, null,
                    zoneClipped: true, new PlayerbotZoneLookup((x, y, z) => 0u));
                var ret = new PlayerbotHomeReturn();
                Assert.Equal(PlayerbotHomeState.Inside, ret.Evaluate(player, area, 0));
                Assert.False(area.Contains(player));
                player.Relocate(player.X - 200f, player.Y, player.Z, 0, host.World.NowMs);
                Assert.Equal(PlayerbotHomeState.Returning, ret.Evaluate(player, area, 100));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// An errand outside the home zone keeps its priority (a capital trip for a trainer or vendor is allowed as before the clip): the
    /// bot stands in another zone next to the trainer with money to spend, and trains instead of walking home.
    /// </summary>
    [Fact]
    public async Task Brain_OutOfTheZone_WithATrainerErrand_RunsTheErrandFirst()
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices:
                services => services.AddSingleton<ArcaneCore.Data.Content.Spells.ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "town NPC visible");
                await host.World.InvokeAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    Player player = session.Player!;
                    // The home is 50 yards east, radius 100 (the bot is inside the circle) and the bot's side of x + 25 is another zone.
                    float border = player.X + 25f;
                    var area = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(player.X + 50f, player.Y, player.Z), 100f, null,
                        zoneClipped: true, new PlayerbotZoneLookup((x, y, z) => x >= border ? Westfall : Duskwood));

                    player.Money = 0;
                    var broke = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                    session.ManagedBudget = new ManagedActionBudget(4);
                    broke.Update(500);
                    Assert.Equal(PlayerbotGoalKind.Home, broke.Goal);
                    broke.Stop();

                    player.Money = 100;
                    var errand = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = area };
                    session.ManagedBudget = new ManagedActionBudget(4);
                    errand.Update(500);
                    Assert.Equal(PlayerbotGoalKind.Train, errand.Goal);
                    Assert.Equal(TownVendorFixture.Entry, errand.TargetEntry);
                    errand.Stop();
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    private static Creature Hostile(uint entry, uint type, uint low)
        => new(low, new CreatureTemplate { Entry = entry, Name = "candidate", CreatureType = type, MinLevelHealth = 20, MaxLevelHealth = 20 },
            null, new CreatureContent([], [], [], [], []), new Random(1));
}
