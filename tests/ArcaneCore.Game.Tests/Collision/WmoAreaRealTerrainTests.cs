using System.Diagnostics;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The WMO group areas on the real 5875 extraction (<c>ARCANECORE_TEST_TERRAIN_DIR</c>: maps/ and vmaps/; <c>ARCANECORE_TEST_DBC_DIR</c>:
/// AreaTable.dbc and WMOAreaTable.dbc, read by the production readers): Undercity lies under Tirisfal Glades and Ironforge in Dun
/// Morogh, and a position inside either took the zone of the terrain above it (live: an Alliance bot at Undercity's Trade Quarter,
/// Zone 85, no PvP enforcement, the flag timed out after five minutes). Skipped, visibly, without both directories.
/// </summary>
public sealed class WmoAreaRealTerrainTests(ITestOutputHelper output)
{
    private const uint Tirisfal = 85, Undercity = 1497, DunMorogh = 1, Ironforge = 1537;

    // Undercity's Trade Quarter (the live report), the Ironforge floor of the under-map tests (WmoUnderMapRealTerrainTests) and the
    // terrain above and around them.
    private const float TradeX = 1608.86f, TradeY = 236.80f, TradeZ = -51.47f;
    private const float IronforgeX = -4601.65f, IronforgeY = -896.47f, IronforgeZ = 503.01f;
    private const float DeathknellX = 1676.2f, DeathknellY = 1677.5f;
    private const float KharanosX = -5582.0f, KharanosY = -526.0f;

    private static string Terrain => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    private static string Dbc => Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;

    internal static string? SkipReason()
    {
        string? dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR");
        bool haveDbc = !string.IsNullOrWhiteSpace(dbc)
            && File.Exists(Path.Combine(dbc, "WMOAreaTable.dbc")) && File.Exists(Path.Combine(dbc, "AreaTable.dbc"));
        return RealTerrain.SkipReason()
            ?? (haveDbc ? null : "ARCANECORE_TEST_DBC_DIR is not set to a directory with AreaTable.dbc and WMOAreaTable.dbc; the real WMO area checks did NOT run.");
    }

    public sealed class RealWmoAreaFactAttribute : FactAttribute
    {
        public RealWmoAreaFactAttribute()
        {
            if (SkipReason() is { } reason)
            {
                Skip = reason;
            }
        }
    }

    /// <summary>The map content of the real client tables: the two continents, every area, every WMO group row.</summary>
    internal static MapContent RealContent(bool wmoRows = true)
    {
        AreaTemplate[] areas = [.. AreaTableDbcReader.Read(Path.Combine(Dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
            r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId))];
        WmoAreaTemplate[] wmo = wmoRows
            ? [.. WmoAreaTableDbcReader.Read(Path.Combine(Dbc, "WMOAreaTable.dbc")).Select(r => new WmoAreaTemplate(r.Id, r.RootId, r.AdtId, r.GroupId, r.AreaId, r.Flags))]
            : [];
        return new MapContent(
            [new(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""), new(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", "")],
            areas, [], [], []) { WmoAreas = wmo };
    }

    /// <summary>A world with the real terrain, models (the tiles around each point loaded) and area tables.</summary>
    internal static WorldRuntime Load(bool wmoRows = true, params (float X, float Y)[] around)
    {
        var vmaps = new VMapManager(Path.Combine(Terrain, "vmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Terrain;
        WorldCollision.Of(world).Install(lineOfSight: vmaps);
        foreach ((float x, float y) in around)
        {
            (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    vmaps.LoadTile(0, tx + dx, ty + dy);
                }
            }
        }

        WorldMaps.Of(world).Load(RealContent(wmoRows));
        return world;
    }

    [RealWmoAreaFact]
    public void UndercityTradeQuarter_IsZone1497_AndACapital()
    {
        using WorldRuntime world = Load(true, (TradeX, TradeY));
        Map map = world.GetMap(0);

        (uint zone, uint area) = map.GetZoneAndAreaId(TradeX, TradeY, TradeZ);

        Assert.Equal((Undercity, Undercity), (zone, area));
        AreaTemplate entry = WorldMaps.Of(world).Areas.GetById(zone)!;
        Assert.NotEqual(0u, entry.Flags & (uint)AreaFlags.Capital);
        Assert.Equal(AreaTeams.Horde, entry.Team);
        Assert.True(PvpAreaRules.IsEnforced(entry.Team, (AreaFlags)entry.Flags, Team.Alliance, PvpRealmMode.Normal, inBattleground: false));
        Assert.False(PvpAreaRules.IsEnforced(entry.Team, (AreaFlags)entry.Flags, Team.Horde, PvpRealmMode.Normal, inBattleground: false));
    }

    [RealWmoAreaFact]
    public void TirisfalSurface_OverAndBesideUndercity_StillResolvesTo85()
    {
        using WorldRuntime world = Load(true, (TradeX, TradeY), (DeathknellX, DeathknellY));
        Map map = world.GetMap(0);

        // On the ground over and around the city: the WMO test finds a city group below the point (or none), but terrain rising to the
        // point makes it outdoors. Every ground point with terrain data within 300 yards of the Trade Quarter is Tirisfal (the ADT has no height data right over the city).
        int checkedPoints = 0;
        for (float dx = -300f; dx <= 300f; dx += 30f)
        {
            for (float dy = -300f; dy <= 300f; dy += 30f)
            {
                float x = TradeX + dx, y = TradeY + dy;
                float ground = map.Terrain.GetHeight(x, y, 500f);
                if (ground <= TerrainTile.InvalidHeightValue)
                {
                    continue;
                }

                checkedPoints++;
                Assert.True(Tirisfal == map.GetZoneAndAreaId(x, y, ground + 0.5f).ZoneId, $"({x:F1}, {y:F1}, {ground + 0.5f:F1}) is not Tirisfal Glades");
            }
        }

        output.WriteLine($"ground points over Undercity with terrain data: {checkedPoints} of 441 (terrain over the Trade Quarter: {map.Terrain.GetHeight(TradeX, TradeY, 500f):F2})");
        Assert.True(checkedPoints > 0, "no terrain data over Undercity at all: the check read nothing");

        float deathknell = map.Terrain.GetHeight(DeathknellX, DeathknellY, 500f);
        Assert.True(deathknell > TerrainTile.InvalidHeightValue, "no terrain at Deathknell");
        Assert.Equal(Tirisfal, map.GetZoneAndAreaId(DeathknellX, DeathknellY, deathknell + 0.5f).ZoneId);
    }

    [RealWmoAreaFact]
    public void IronforgeInterior_IsZone1537_AndDunMorogh_OutsideIt()
    {
        using WorldRuntime world = Load(true, (IronforgeX, IronforgeY), (KharanosX, KharanosY));
        Map map = world.GetMap(0);

        Assert.Equal(Ironforge, map.GetZoneAndAreaId(IronforgeX, IronforgeY, IronforgeZ).ZoneId);

        float kharanos = map.Terrain.GetHeight(KharanosX, KharanosY, 1000f);
        Assert.True(kharanos > TerrainTile.InvalidHeightValue, "no terrain at Kharanos");
        Assert.Equal(DunMorogh, map.GetZoneAndAreaId(KharanosX, KharanosY, kharanos + 0.5f).ZoneId);
    }

    [RealWmoAreaFact]
    public void WithoutTheWmoRows_TheLookupIsTheOldTerrainOnlyAnswer()
    {
        using WorldRuntime world = Load(false, (TradeX, TradeY));

        Assert.Equal(Tirisfal, world.GetMap(0).GetZoneAndAreaId(TradeX, TradeY, TradeZ).ZoneId);
    }

    /// <summary>The cost of a lookup: reported, and bounded so that a regression to milliseconds is caught.</summary>
    [RealWmoAreaFact]
    public void LookupCost_IsReported()
    {
        using WorldRuntime world = Load(true, (TradeX, TradeY));
        TerrainInfo terrain = world.GetMap(0).Terrain;
        float ground = terrain.GetHeight(TradeX, TradeY, 500f);
        double inside = Measure(terrain, TradeX, TradeY, TradeZ);
        double overland = Measure(terrain, TradeX, TradeY, ground + 20f);
        double tileOnly = Measure(terrain, 100f, 100f, 50f); // no WMO: the group query misses
        output.WriteLine($"BENCH GetZoneAndAreaId: inside a WMO group {inside:F0} ns, over it (open sky) {overland:F0} ns, tile only (no model there) {tileOnly:F0} ns");
        Assert.True(inside < 100_000, $"a lookup inside a WMO took {inside:F0} ns");
    }

    private static double Measure(TerrainInfo terrain, float x, float y, float z)
    {
        const int warm = 2_000, runs = 50_000;
        uint sink = 0;
        for (int i = 0; i < warm; i++)
        {
            sink += terrain.GetZoneAndAreaId(x, y, z).ZoneId;
        }

        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < runs; i++)
        {
            sink += terrain.GetZoneAndAreaId(x, y, z).ZoneId;
        }

        double ns = (Stopwatch.GetTimestamp() - started) * 1e9 / Stopwatch.Frequency / runs;
        GC.KeepAlive(sink);
        return ns;
    }

    /// <summary>
    /// The live symptom end to end: an Alliance player inside Undercity, flagged for PvP and out of combat, keeps the flag past the five
    /// minute linger because Undercity enforces PvP on the Alliance, and a Horde observer in the same map sees it.
    /// </summary>
    [RealWmoAreaFact]
    public void AlliancePlayerInUndercity_KeepsThePvpFlagPastFiveMinutes_AndAnObserverSeesIt()
    {
        using WorldRuntime world = Load(true, (TradeX, TradeY));
        WorldStateHooks hooks = WorldStateHooks.For(world);
        hooks.AddLocationListener(new PvpAreaTracker(hooks));

        Player player = TestWorld.CreatePlayer(1, TradeX, TradeY, new FakeSession(1));
        player.Relocate(TradeX, TradeY, TradeZ);
        Player observer = TestWorld.CreatePlayer(2, TradeX + 3f, TradeY, new FakeSession(2), race: Race.Orc);
        observer.Relocate(TradeX + 3f, TradeY, TradeZ);
        world.AddPlayer(player);
        world.AddPlayer(observer);
        world.RunTick(50);
        Assert.Equal(Team.Alliance, player.Team);
        Assert.Equal(Team.Horde, observer.Team);

        MapCombat.UpdatePvp(player, true);
        Assert.NotEqual(0u, (uint)(player.UnitFlags & UnitFlags.Pvp));
        Assert.False(player.Combat.InPvpCombat);

        for (int i = 0; i < 4; i++)
        {
            world.RunTick(100_000); // 400 s of manual-clock time: more than the 300 s linger
        }

        // The symptom first: the flag, then why.
        Assert.True((player.UnitFlags & UnitFlags.Pvp) != 0, $"the PvP flag timed out inside Undercity (timer {player.Combat.PvpFlagTimer} ms, enforced area: {PvpAreaState.IsInEnforcedArea(player)})");
        Assert.Equal((Undercity, Undercity), player.Map!.GetZoneAndAreaId(player.X, player.Y, player.Z));
        Assert.True(PvpAreaState.IsInEnforcedArea(player), "Undercity does not enforce PvP on the Alliance player");
        Assert.Equal(CombatConstants.PvpFlagTimerMs, player.Combat.PvpFlagTimer);

        Assert.Contains(player.Guid, observer.VisibleObjects);
        Assert.NotEqual(0u, player.GetUInt32(UpdateFields.UnitFieldFlags) & (uint)UnitFlags.Pvp);
    }
}
