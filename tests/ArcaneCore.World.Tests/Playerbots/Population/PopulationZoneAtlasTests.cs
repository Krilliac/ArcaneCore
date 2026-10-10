using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

public sealed class PopulationZoneAtlasTests
{
    private const uint Capital = (uint)AreaFlags.Capital;
    private const uint City = (uint)AreaFlags.City;

    // Zones: 12 Elwynn (Alliance, 5-10), 14 Durotar (Horde, 5-10), 85 Tirisfal (Horde, 5-10, map 0), 10 Duskwood (contested, 10-30),
    // 33 Stranglethorn (contested, 30-50; only an Alliance graveyard), 1519 Stormwind (city), 99 an instance zone, 77 a zone whose
    // only graveyard is on another map.
    private static AreaTemplate Area(uint entry, uint map, uint zone, uint flags, int level, string name, uint team)
        => new(entry, map, zone, entry, flags, level, name, team, 0);

    private static List<AreaTemplate> Areas() =>
    [
        Area(12, 0, 0, 0, 0, "Elwynn Forest", AreaTeams.Ally),
        Area(24, 0, 12, 0, 5, "Fargodeep Mine", 0), Area(25, 0, 12, 0, 10, "Stormwind Mountains", 0), Area(26, 0, 12, 0, 0, "Unleveled", 0),
        Area(14, 1, 0, 0, 0, "Durotar", AreaTeams.Horde), Area(34, 1, 14, 0, 5, "Razor Hill", 0), Area(35, 1, 14, 0, 10, "Skull Rock", 0),
        Area(85, 0, 0, 0, 0, "Tirisfal Glades", AreaTeams.Horde), Area(86, 0, 85, 0, 5, "Brill", 0), Area(87, 0, 85, 0, 10, "Garrens Haunt", 0),
        Area(10, 0, 0, 0, 0, "Duskwood", AreaTeams.None), Area(42, 0, 10, 0, 10, "Darkshire", 0), Area(43, 0, 10, 0, 30, "Manor Mistmantle", 0),
        Area(33, 0, 0, 0, 0, "Stranglethorn Vale", AreaTeams.None), Area(44, 0, 33, 0, 30, "Rebel Camp", 0), Area(45, 0, 33, 0, 50, "Jaguero Isle", 0),
        Area(1519, 0, 0, City, 10, "Stormwind City", AreaTeams.Ally), Area(1500, 0, 1519, 0, 5, "Trade District", 0),
        Area(1537, 0, 0, Capital, 10, "Ironforge", AreaTeams.Ally), Area(1538, 0, 1537, 0, 5, "The Commons", 0),
        Area(99, 36, 0, 0, 0, "The Deadmines", AreaTeams.None), Area(98, 36, 99, 0, 20, "Deadmines hall", 0),
        Area(77, 0, 0, 0, 0, "Nowhere Vale", AreaTeams.None), Area(78, 0, 77, 0, 8, "Nowhere camp", 0),
    ];

    private static GraveyardCatalog Graveyards(params (uint Id, uint Map, uint Link, uint Team)[] rows)
    {
        WorldSafeLoc[] locs = [.. rows.Select(r => r.Id).Distinct().Select(id => new WorldSafeLoc(id, rows.First(r => r.Id == id).Map, id * 10f, id * 20f, 50f, 0f, "GY" + id))];
        GraveyardLink[] links = [.. rows.Select(r => new GraveyardLink(r.Id, r.Link, r.Team))];
        return GraveyardCatalog.Build(new GraveyardContent(locs, links), null, out _);
    }

    private static GraveyardCatalog World() => Graveyards(
        (1, 0, 12, 0), (2, 1, 14, 0), (3, 0, 85, 0), (4, 0, 42, GraveyardCatalog.TeamAlliance), (7, 0, 10, GraveyardCatalog.TeamHorde),
        (5, 0, 33, GraveyardCatalog.TeamAlliance), (6, 0, 1519, 0), (8, 36, 99, 0), (9, 1, 78, 0));

    private static PopulationZoneAtlas Atlas(IReadOnlyCollection<uint>? maps = null, params uint[] excluded)
        => PopulationZoneAtlas.Build(Areas(), World(), maps ?? [0, 1], new HashSet<uint>(excluded));

    [Fact]
    public void AZonesLevelRange_IsTheRangeOfItsAreasLevels_WithTheSlackBelow()
    {
        PopulationZone elwynn = Atlas().Find(12)!;
        Assert.Equal((5, 10), (elwynn.MinLevel, elwynn.MaxLevel)); // the unleveled sub-area (0) does not count
        Assert.True(PopulationZoneAtlas.Fits(elwynn, 5, Team.Alliance, 0));
        Assert.True(PopulationZoneAtlas.Fits(elwynn, 10, Team.Alliance, 0));
        Assert.False(PopulationZoneAtlas.Fits(elwynn, 11, Team.Alliance, 4));
        Assert.True(PopulationZoneAtlas.Fits(elwynn, 1, Team.Alliance, 4)); // a level 1 bot may start in a zone whose lowest area is 5
        Assert.False(PopulationZoneAtlas.Fits(elwynn, 1, Team.Alliance, 3));
        Assert.Equal(30, Atlas().Find(10)!.MaxLevel);
    }

    [Fact]
    public void AnAllianceOnlyZone_IsNeverOfferedToHorde()
    {
        PopulationZoneAtlas atlas = Atlas();
        Assert.DoesNotContain(atlas.FittingZones(1, 60, Team.Horde, 4), z => z.ZoneId == 12);
        var random = new Random(1);
        for (int i = 0; i < 200; i++)
            Assert.NotEqual(12u, atlas.Pick(1, 10, Team.Horde, 4, new Dictionary<uint, int>(), random)!.Zone.ZoneId);
        Assert.False(PopulationZoneAtlas.Fits(atlas.Find(12)!, 6, Team.Horde, 4));
    }

    [Fact]
    public void AHordeOnlyZone_IsNeverOfferedToAlliance()
    {
        PopulationZoneAtlas atlas = Atlas();
        Assert.DoesNotContain(atlas.FittingZones(1, 60, Team.Alliance, 4), z => z.ZoneId is 14 or 85);
        var random = new Random(2);
        for (int i = 0; i < 200; i++)
            Assert.DoesNotContain(atlas.Pick(1, 10, Team.Alliance, 4, new Dictionary<uint, int>(), random)!.Zone.ZoneId, new uint[] { 14, 85 });
        Assert.False(PopulationZoneAtlas.Fits(atlas.Find(14)!, 6, Team.Alliance, 4));
    }

    [Fact]
    public void AContestedZone_OffersOnlyTheGraveyardsOfTheBotsTeam()
    {
        PopulationZone duskwood = Atlas().Find(10)!;
        Assert.Equal([4u], duskwood.AllianceLocations.Select(l => l.Id).ToArray()); // the Alliance link of Darkshire (a sub-area)
        Assert.Equal([7u], duskwood.HordeLocations.Select(l => l.Id).ToArray());
        Assert.Equal([1u], Atlas().Find(12)!.AllianceLocations.Select(l => l.Id).ToArray()); // link team 0 serves both teams
        Assert.Equal([1u], Atlas().Find(12)!.HordeLocations.Select(l => l.Id).ToArray());
        var random = new Random(3);
        PopulationZoneAtlas atlas = Atlas([0], 12, 33, 85); // only Duskwood is left on map 0 for level 25
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(4u, atlas.Pick(25, 25, Team.Alliance, 0, new Dictionary<uint, int>(), random)!.Location.Id);
            Assert.Equal(7u, atlas.Pick(25, 25, Team.Horde, 0, new Dictionary<uint, int>(), random)!.Location.Id);
        }
    }

    [Fact]
    public void ZonesOffAllowedMaps_CapitalsAndExcludedZones_AreNotOffered()
    {
        uint[] all = [.. Atlas().Zones.Select(z => z.ZoneId)];
        Assert.DoesNotContain(1519u, all); // a city
        Assert.DoesNotContain(1537u, all); // a capital
        Assert.DoesNotContain(99u, all); // map 36 is not allowed
        Assert.Contains(10u, all);
        Assert.DoesNotContain(14u, Atlas([0]).Zones.Select(z => z.ZoneId)); // Durotar is on map 1
        Assert.DoesNotContain(10u, Atlas(null, 10).Zones.Select(z => z.ZoneId)); // excluded
        Assert.Contains(99u, Atlas([0, 1, 36]).Zones.Select(z => z.ZoneId));
    }

    [Fact]
    public void AZoneWithoutAGraveyardForTheTeam_IsNotOffered()
    {
        PopulationZoneAtlas atlas = Atlas();
        // Stranglethorn has only an Alliance link: offered to the Alliance, never to the Horde.
        Assert.Contains(atlas.FittingZones(30, 50, Team.Alliance, 0), z => z.ZoneId == 33);
        Assert.DoesNotContain(atlas.FittingZones(30, 50, Team.Horde, 0), z => z.ZoneId == 33);
        Assert.Empty(atlas.Find(33)!.HordeLocations);
        // The graveyard of zone 77 is on another map than the zone, so the zone has none and is not offered at all.
        Assert.DoesNotContain(atlas.FittingZones(1, 60, Team.Alliance, 4), z => z.ZoneId == 77);
        Assert.DoesNotContain(atlas.FittingZones(1, 60, Team.Horde, 4), z => z.ZoneId == 77);
        Assert.Equal(0, PopulationZoneAtlas.Empty.CountFor(Team.Alliance));
        Assert.Null(PopulationZoneAtlas.Empty.Pick(1, 60, Team.Alliance, 4, new Dictionary<uint, int>(), new Random(4)));
    }

    [Fact]
    public void Pick_SpreadsBotsToTheLeastPopulatedFittingZone()
    {
        PopulationZoneAtlas atlas = Atlas();
        var random = new Random(5);
        // Level 10 Alliance fits Elwynn (5-10, slack 4) and Duskwood (10-30): the empty one wins.
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(10u, atlas.Pick(10, 10, Team.Alliance, 4, new Dictionary<uint, int> { [12] = 1 }, random)!.Zone.ZoneId);
            Assert.Equal(12u, atlas.Pick(10, 10, Team.Alliance, 4, new Dictionary<uint, int> { [10] = 2, [12] = 1 }, random)!.Zone.ZoneId);
        }

        // A tie is broken at random: both zones appear.
        var seen = new HashSet<uint>();
        for (int i = 0; i < 100; i++) seen.Add(atlas.Pick(10, 10, Team.Alliance, 4, new Dictionary<uint, int> { [10] = 1, [12] = 1 }, random)!.Zone.ZoneId);
        Assert.Equal([10u, 12u], seen.Order().ToArray());

        // The levels a placement allows are the band's range cut by the zone's: band 1-20 in Elwynn is 1-10 (5 minus the slack, 10).
        PopulationPlacement placement = atlas.Pick(1, 20, Team.Alliance, 4, new Dictionary<uint, int> { [10] = 5 }, random)!;
        Assert.Equal(12u, placement.Zone.ZoneId);
        Assert.Equal((1, 10), (placement.LowLevel, placement.HighLevel));
    }

    [Fact]
    public void RealAreaTable_ElwynnIsAllianceFiveToTen_TheBarrensIsHorde()
    {
        // Rows of the 1.12.1 (5875) AreaTable.dbc for six zones and their levelled sub-areas, read with the vmangos layout (field 10
        // AreaLevel, field 20 Team): "entry map zone flags level team". Zone rows carry level 0; the level sits on the sub-areas.
        var rows = new List<AreaTemplate>();
        foreach (string line in RealRows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            uint[] f = [.. line.Split(' ').Select(uint.Parse)];
            rows.Add(Area(f[0], f[1], f[2], f[3], (int)f[4], "Area" + f[0], f[5]));
        }

        GraveyardCatalog graveyards = Graveyards(
            (1, 0, 12, 0), (2, 1, 14, 0), (3, 1, 17, 0), (4, 0, 10, 0), (5, 0, 33, 0), (6, 0, 40, 0));
        PopulationZoneAtlas atlas = PopulationZoneAtlas.Build(rows, graveyards, [0, 1], new HashSet<uint>());
        Assert.Equal((5, 10, AreaTeams.Ally), (atlas.Find(12)!.MinLevel, atlas.Find(12)!.MaxLevel, atlas.Find(12)!.AreaTeam)); // Elwynn Forest
        Assert.Equal((9, 18, AreaTeams.Ally), (atlas.Find(40)!.MinLevel, atlas.Find(40)!.MaxLevel, atlas.Find(40)!.AreaTeam)); // Westfall
        Assert.Equal((5, 10, AreaTeams.Horde), (atlas.Find(14)!.MinLevel, atlas.Find(14)!.MaxLevel, atlas.Find(14)!.AreaTeam)); // Durotar
        Assert.Equal((10, 33, AreaTeams.Horde), (atlas.Find(17)!.MinLevel, atlas.Find(17)!.MaxLevel, atlas.Find(17)!.AreaTeam)); // The Barrens
        Assert.Equal((10, 30, AreaTeams.None), (atlas.Find(10)!.MinLevel, atlas.Find(10)!.MaxLevel, atlas.Find(10)!.AreaTeam)); // Duskwood
        Assert.Equal((30, 50, AreaTeams.None), (atlas.Find(33)!.MinLevel, atlas.Find(33)!.MaxLevel, atlas.Find(33)!.AreaTeam)); // Stranglethorn Vale
        Assert.DoesNotContain(atlas.FittingZones(1, 60, Team.Alliance, 4), z => z.ZoneId is 14 or 17);
        Assert.DoesNotContain(atlas.FittingZones(1, 60, Team.Horde, 4), z => z.ZoneId is 12 or 40);
        // The game's own AreaTable gives the same zone list.
        var table = new AreaTable(rows, ArcaneCore.Game.Maps.Templates.MapRegistry.Default);
        Assert.Equal(atlas.Zones.Select(z => z.ZoneId), PopulationZoneAtlas.Build(table.All, graveyards, [0, 1], new HashSet<uint>()).Zones.Select(z => z.ZoneId));
    }

    private const string RealRows = """
10 0 0 64 0 0
12 0 0 64 0 2
14 1 0 64 0 4
17 1 0 64 0 4
18 0 12 64 7 0
19 0 33 64 40 0
20 0 40 64 14 0
33 0 0 64 0 0
35 0 33 64 42 0
37 0 33 64 36 0
40 0 0 64 0 2
42 0 10 64 25 0
43 0 33 64 42 0
54 0 12 64 8 0
57 0 12 64 5 0
60 0 12 64 8 0
62 0 12 64 8 0
63 0 12 64 6 0
64 0 12 64 6 0
80 0 12 64 10 2
86 0 12 64 8 0
87 0 12 64 5 0
88 0 12 64 6 0
91 0 12 64 8 0
93 0 10 64 28 0
94 0 10 64 24 0
99 0 33 64 30 0
100 0 33 64 31 0
101 0 33 64 32 0
102 0 33 64 41 0
103 0 33 64 40 0
104 0 33 64 37 0
105 0 33 64 42 0
107 0 40 64 14 0
108 0 40 64 15 0
109 0 40 64 15 0
111 0 40 64 12 0
113 0 40 64 15 0
115 0 40 64 17 0
117 0 33 64 35 0
120 0 12 64 8 0
121 0 10 64 28 0
122 0 33 64 37 0
123 0 33 64 34 0
125 0 33 64 35 0
127 0 33 64 40 0
128 0 33 64 36 0
129 0 33 64 37 0
219 0 40 64 15 0
241 0 10 64 26 0
242 0 10 64 28 0
245 0 10 64 25 0
297 0 33 64 50 0
310 0 33 64 41 0
311 0 33 64 44 0
359 1 17 64 25 0
362 1 14 64 5 0
366 1 14 64 7 0
367 1 14 64 6 0
368 1 14 64 7 0
369 1 14 64 9 0
370 1 14 64 8 0
372 1 14 64 6 0
378 1 17 64 10 0
379 1 17 64 10 0
380 1 17 64 15 0
381 1 17 64 17 0
382 1 17 64 15 0
383 1 17 64 14 0
384 1 17 64 11 0
385 1 17 64 15 0
386 1 17 64 12 0
387 1 17 64 12 0
388 1 17 64 15 0
390 1 17 64 20 0
391 1 17 64 12 0
392 1 17 64 15 0
407 1 14 64 10 0
410 1 14 64 8 0
477 0 33 64 43 0
492 0 10 64 26 0
536 0 10 64 24 0
576 0 10 64 25 0
797 0 12 64 8 0
798 0 12 64 8 0
799 0 10 64 10 0
816 1 14 64 8 0
817 1 14 64 10 0
856 0 10 64 19 0
916 0 40 64 9 0
917 0 40 64 18 0
918 0 40 64 14 0
919 0 40 64 14 0
920 0 40 64 17 0
921 0 40 64 16 0
922 0 40 64 18 0
1097 0 10 64 19 0
1098 0 10 64 30 0
1316 1 17 64 33 0
1697 1 17 64 18 4
1698 1 17 64 20 4
1699 1 17 64 13 4
1700 1 17 64 19 4
1701 1 17 64 20 4
1702 1 17 64 18 4
1703 1 17 64 17 4
1704 1 17 64 11 4
1717 1 17 64 24 4
1737 0 33 64 40 0
1738 0 33 64 43 0
1739 0 33 64 42 0
1740 0 33 64 36 0
1741 0 33 64 43 0
""";
}
